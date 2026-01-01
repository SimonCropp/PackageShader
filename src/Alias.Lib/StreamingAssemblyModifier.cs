using System.Reflection.Metadata.Ecma335;
using System.Text;
using Alias.Lib.Metadata;
using Alias.Lib.Metadata.Tables;
using CodedIndex = Alias.Lib.Metadata.Tables.CodedIndex;
using Alias.Lib.Modification;
using Alias.Lib.Pdb;
using Alias.Lib.PE;
using Alias.Lib.Signing;

namespace Alias.Lib;

/// <summary>
/// High-level API for modifying .NET assemblies using streaming to minimize memory usage.
/// Only loads headers on open (~4KB), streams unchanged data during save.
/// </summary>
public sealed class StreamingAssemblyModifier : IDisposable
{
    private readonly StreamingPEFile _peFile;
    private readonly StreamingMetadataReader _metadata;
    private readonly ModificationPlan _plan;
    private bool _disposed;

    private StreamingAssemblyModifier(StreamingPEFile peFile, StreamingMetadataReader metadata, ModificationPlan plan)
    {
        _peFile = peFile;
        _metadata = metadata;
        _plan = plan;
    }

    /// <summary>
    /// Opens an assembly for modification using streaming access.
    /// Only loads headers (~4KB), not the full file.
    /// </summary>
    public static StreamingAssemblyModifier Open(string path)
    {
        var peFile = StreamingPEFile.Open(path);
        try
        {
            var metadata = new StreamingMetadataReader(peFile);
            var plan = new ModificationPlan(metadata);
            return new(peFile, metadata, plan);
        }
        catch
        {
            peFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the source file path.
    /// </summary>
    public string SourcePath => _peFile.FilePath;

    /// <summary>
    /// Sets the assembly name.
    /// </summary>
    public void SetAssemblyName(string name) =>
        _plan.SetAssemblyName(name);

    /// <summary>
    /// Sets the assembly public key.
    /// </summary>
    public void SetAssemblyPublicKey(byte[] publicKey) =>
        _plan.SetAssemblyPublicKey(publicKey);

    /// <summary>
    /// Clears the assembly's strong name (removes public key).
    /// </summary>
    public void ClearStrongName() =>
        _plan.ClearStrongName();

    /// <summary>
    /// Redirects an assembly reference to a new name.
    /// </summary>
    /// <param name="sourceName">The original assembly name to find.</param>
    /// <param name="targetName">The new assembly name.</param>
    /// <param name="publicKeyToken">The new public key token, or null to clear it.</param>
    public bool RedirectAssemblyRef(string sourceName, string targetName, byte[]? publicKeyToken = null) =>
        _plan.RedirectAssemblyRef(sourceName, targetName, publicKeyToken);

    /// <summary>
    /// Makes all public types internal.
    /// </summary>
    public void MakeTypesInternal() =>
        _plan.MakeTypesInternal();

    /// <summary>
    /// Adds an InternalsVisibleTo attribute.
    /// </summary>
    public void AddInternalsVisibleTo(string assemblyName, byte[]? publicKey = null)
    {
        // Find or create reference to InternalsVisibleToAttribute
        var constructorRid = FindOrCreateInternalsVisibleToConstructor();
        if (constructorRid == 0)
        {
            throw new InvalidOperationException("Could not find or create InternalsVisibleTo constructor reference");
        }

        // Build attribute value
        var value = publicKey is { Length: > 0 }
            ? $"{assemblyName}, PublicKey={Convert.ToHexString(publicKey)}"
            : assemblyName;

        var valueBlob = CreateCustomAttributeBlob(value);
        var valueBlobIndex = _plan.GetOrAddBlob(valueBlob);

        // Create custom attribute row
        // Parent = Assembly (token 0x20000001, encoded as HasCustomAttribute)
        var assemblyToken = new MetadataToken(TableIndex.Assembly, 1);
        var parentEncoded = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomAttribute, assemblyToken);

        // Type = MemberRef to constructor (encoded as CustomAttributeType)
        var constructorToken = new MetadataToken(TableIndex.MemberRef, constructorRid);
        var typeEncoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, constructorToken);

        var attributeRow = new CustomAttributeRow
        {
            ParentIndex = parentEncoded,
            TypeIndex = typeEncoded,
            ValueIndex = valueBlobIndex
        };

        _plan.AddCustomAttribute(attributeRow);
    }

    private uint FindOrCreateInternalsVisibleToConstructor()
    {
        // Look for existing TypeRef to InternalsVisibleToAttribute
        var typeRefRid = _metadata.FindTypeRef("InternalsVisibleToAttribute", "System.Runtime.CompilerServices");

        if (typeRefRid.HasValue)
        {
            // Look for existing MemberRef to .ctor
            var memberRefRid = _metadata.FindMemberRef(typeRefRid.Value, ".ctor");
            if (memberRefRid.HasValue)
                return memberRefRid.Value;

            // TypeRef exists but no MemberRef - create one
            return CreateConstructorMemberRef(typeRefRid.Value);
        }

        // TypeRef doesn't exist - need to create both
        // First find the resolution scope (System.Runtime or mscorlib)
        uint? resolutionScope = FindSystemRuntimeAssemblyRef();
        if (!resolutionScope.HasValue)
            return 0;

        // Create TypeRef for InternalsVisibleToAttribute
        var typeRefRow = new TypeRefRow
        {
            ResolutionScopeIndex = CodedIndexHelper.EncodeToken(
                CodedIndex.ResolutionScope,
                new(TableIndex.AssemblyRef, resolutionScope.Value)),
            NameIndex = _plan.GetOrAddString("InternalsVisibleToAttribute"),
            NamespaceIndex = _plan.GetOrAddString("System.Runtime.CompilerServices")
        };
        var newTypeRefRid = _plan.AddTypeRef(typeRefRow);

        // Create MemberRef for .ctor(string)
        return CreateConstructorMemberRef(newTypeRefRid);
    }

    private uint? FindSystemRuntimeAssemblyRef()
    {
        // Look for common .NET runtime assembly references
        string[] runtimeAssemblyNames = ["System.Runtime", "mscorlib", "netstandard", "System.Private.CoreLib"];

        foreach (var name in runtimeAssemblyNames)
        {
            var found = _metadata.FindAssemblyRef(name);
            if (found.HasValue)
                return found.Value.rid;
        }

        return null;
    }

    private uint CreateConstructorMemberRef(uint typeRefRid)
    {
        // Constructor signature for InternalsVisibleToAttribute(string):
        // HASTHIS (0x20) | ParamCount(1) | ReturnType:VOID(0x01) | Param:STRING(0x0E)
        byte[] ctorSignature = [0x20, 0x01, 0x01, 0x0E];

        var memberRefRow = new MemberRefRow
        {
            ClassIndex = CodedIndexHelper.EncodeToken(
                CodedIndex.MemberRefParent,
                new(TableIndex.TypeRef, typeRefRid)),
            NameIndex = _plan.GetOrAddString(".ctor"),
            SignatureIndex = _plan.GetOrAddBlob(ctorSignature)
        };

        return _plan.AddMemberRef(memberRefRow);
    }

    private static byte[] CreateCustomAttributeBlob(string value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Prolog
        writer.Write((ushort)0x0001);

        // String argument (SerString format)
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteCompressedUInt32(writer, (uint)bytes.Length);
        writer.Write(bytes);

        // No named arguments
        writer.Write((ushort)0x0000);

        return ms.ToArray();
    }

    private static void WriteCompressedUInt32(BinaryWriter writer, uint value)
    {
        if (value < 0x80)
        {
            writer.Write((byte)value);
        }
        else if (value < 0x4000)
        {
            writer.Write((byte)(0x80 | (value >> 8)));
            writer.Write((byte)(value & 0xff));
        }
        else
        {
            writer.Write((byte)(0xc0 | (value >> 24)));
            writer.Write((byte)((value >> 16) & 0xff));
            writer.Write((byte)((value >> 8) & 0xff));
            writer.Write((byte)(value & 0xff));
        }
    }

    /// <summary>
    /// Saves the modified assembly using the most efficient strategy.
    /// </summary>
    public void Save(string path, StrongNameKey? key = null)
    {
        var strategy = _plan.GetStrategy();

        // Check if we're writing to the same file we're reading from
        var isSameFile = string.Equals(
            Path.GetFullPath(_peFile.FilePath),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

        if (strategy == ModificationStrategy.InPlacePatch)
        {
            // Most efficient: copy file and patch in place
            SaveWithInPlacePatching(path, key, isSameFile);
        }
        else
        {
            // Need to rebuild metadata section
            SaveWithMetadataRebuild(path, key, isSameFile);
        }
        PdbHandler.CopyExternalPdb(_peFile.FilePath, path);
    }

    private void SaveWithInPlacePatching(string path, StrongNameKey? key, bool isSameFile)
    {
        // Build patch list first (while source is still open)
        var patches = BuildInPlacePatches();

        if (isSameFile)
        {
            // Writing to same file - need to close source first, then patch in place
            _peFile.Dispose();

            // Apply patches directly to the file
            if (patches.Count > 0)
            {
                StreamingPEWriter.ApplyInPlacePatches(path, patches);
            }
        }
        else
        {
            // Copy entire source file to new location
            File.Copy(_peFile.FilePath, path, overwrite: true);

            // Apply patches
            if (patches.Count > 0)
            {
                StreamingPEWriter.ApplyInPlacePatches(path, patches);
            }
        }

        // Sign if key provided
        if (key != null)
        {
            StreamingStrongNameSigner.SignFile(path, key);
        }
    }

    private List<(long offset, byte[] data)> BuildInPlacePatches()
    {
        var patches = new List<(long offset, byte[] data)>();

        // Patch Assembly table row
        foreach (var (rid, row) in _plan.ModifiedAssemblyRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.Assembly, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.BlobIndexSize, _metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch AssemblyRef table rows
        foreach (var (rid, row) in _plan.ModifiedAssemblyRefRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.AssemblyRef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.BlobIndexSize, _metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch TypeDef table rows
        foreach (var (rid, row) in _plan.ModifiedTypeDefRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.TypeDef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.StringIndexSize,
                _metadata.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                _metadata.GetTableIndexSize(TableIndex.Field),
                _metadata.GetTableIndexSize(TableIndex.MethodDef));
            patches.Add((offset, ms.ToArray()));
        }

        return patches;
    }

    private void SaveWithMetadataRebuild(string path, StrongNameKey? key, bool isSameFile)
    {
        string targetPath = path;
        string? tempPath = null;

        if (isSameFile)
        {
            // Write to temp file first, then replace after closing source
            tempPath = path + ".tmp";
            targetPath = tempPath;
        }

        using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var peWriter = new StreamingPEWriter(_peFile, _metadata, _plan);
            peWriter.Write(output);

            output.Flush();

            // Sign if key provided
            if (key != null)
            {
                output.Position = 0;
                StreamingStrongNameSigner.SignStream(output, key);
            }
        }

        if (isSameFile && tempPath != null)
        {
            // Close source file and replace with temp
            _metadata.Dispose();
            _peFile.Dispose();
            File.Delete(path);
            File.Move(tempPath, path);
        }
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _metadata.Dispose();
        _peFile.Dispose();
    }
}
