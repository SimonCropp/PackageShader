using System.Text;
using Alias.Lib.Metadata;
using Alias.Lib.Metadata.Tables;
using Alias.Lib.Pdb;
using Alias.Lib.PE;
using Alias.Lib.Signing;

namespace Alias.Lib;

/// <summary>
/// High-level API for modifying .NET assemblies.
/// </summary>
public sealed class AssemblyModifier
{
    private readonly PEImage _image;
    private readonly MetadataReader _reader;
    private readonly MetadataWriter _writer;

    /// <summary>
    /// Gets the assembly name.
    /// </summary>
    public string? AssemblyName => _reader.GetAssemblyName();

    private AssemblyModifier(PEImage image, MetadataReader reader, MetadataWriter writer)
    {
        _image = image;
        _reader = reader;
        _writer = writer;
    }

    /// <summary>
    /// Opens an assembly for modification.
    /// </summary>
    public static AssemblyModifier Open(string path)
    {
        var image = PEReader.Read(path);
        if (!image.IsManagedAssembly)
            throw new BadImageFormatException($"'{path}' is not a managed .NET assembly");
        var reader = MetadataReader.Read(image);
        var writer = new MetadataWriter(reader);
        return new(image, reader, writer);
    }

    /// <summary>
    /// Checks if the file is a managed .NET assembly.
    /// </summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            var image = PEReader.Read(path);
            return image.IsManagedAssembly;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the assembly name.
    /// </summary>
    public void SetAssemblyName(string name)
    {
        var row = GetOrReadAssemblyRow(1);
        row.NameIndex = _writer.AddString(name);
        _writer.SetAssemblyRow(1, row);
    }

    /// <summary>
    /// Sets the assembly public key.
    /// </summary>
    public void SetAssemblyPublicKey(byte[] publicKey)
    {
        var row = GetOrReadAssemblyRow(1);
        row.PublicKeyIndex = _writer.AddBlob(publicKey);
        _writer.SetAssemblyRow(1, row);
    }

    /// <summary>
    /// Clears the assembly's strong name (removes public key).
    /// </summary>
    public void ClearStrongName()
    {
        var row = GetOrReadAssemblyRow(1);
        row.PublicKeyIndex = 0;
        _writer.SetAssemblyRow(1, row);
    }

    private AssemblyRow GetOrReadAssemblyRow(uint rid)
    {
        // Check if we already have a modified version
        if (_writer.TryGetModifiedAssemblyRow(rid, out var modified))
            return modified;
        return _reader.ReadAssemblyRow(rid);
    }

    /// <summary>
    /// Redirects an assembly reference to a new name.
    /// </summary>
    /// <param name="sourceName">The original assembly name to find.</param>
    /// <param name="targetName">The new assembly name.</param>
    /// <param name="publicKeyToken">The new public key token, or null to clear it.</param>
    /// <param name="clearPublicKeyIfNull">If true and publicKeyToken is null, clears the public key. Otherwise preserves it.</param>
    public bool RedirectAssemblyRef(string sourceName, string targetName, byte[]? publicKeyToken = null, bool clearPublicKeyIfNull = true)
    {
        var found = _reader.FindAssemblyRef(sourceName);
        if (found == null)
            return false;

        var (rid, row) = found.Value;
        row.NameIndex = _writer.AddString(targetName);

        if (publicKeyToken != null)
            row.PublicKeyOrTokenIndex = _writer.AddBlob(publicKeyToken);
        else if (clearPublicKeyIfNull)
            row.PublicKeyOrTokenIndex = 0;  // Clear public key token

        _writer.SetAssemblyRefRow(rid, row);
        return true;
    }

    /// <summary>
    /// Makes all public types internal.
    /// </summary>
    public void MakeTypesInternal()
    {
        var count = _reader.TableHeap.GetRowCount(Table.TypeDef);
        for (uint rid = 1; rid <= count; rid++)
        {
            var row = _reader.ReadTypeDefRow(rid);
            if (row.IsPublic)
            {
                row.MakeInternal();
                _writer.SetTypeDefRow(rid, row);
            }
        }
    }

    /// <summary>
    /// Adds an InternalsVisibleTo attribute.
    /// </summary>
    public void AddInternalsVisibleTo(string assemblyName, byte[]? publicKey = null)
    {
        // Find or create reference to InternalsVisibleToAttribute
        var constructorRid = FindOrCreateInternalsVisibleToConstructor();
        if (constructorRid == 0)
            throw new InvalidOperationException("Could not find or create InternalsVisibleTo constructor reference");

        // Build attribute value
        var value = publicKey is {Length: > 0}
            ? $"{assemblyName}, PublicKey={Convert.ToHexString(publicKey)}"
            : assemblyName;

        var valueBlob = CreateCustomAttributeBlob(value);
        var valueBlobIndex = _writer.AddBlob(valueBlob);

        // Create custom attribute row
        // Parent = Assembly (token 0x20000001, encoded as HasCustomAttribute)
        var assemblyToken = new MetadataToken(TokenType.Assembly, 1);
        var parentEncoded = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomAttribute, assemblyToken);

        // Type = MemberRef to constructor (encoded as CustomAttributeType)
        var constructorToken = new MetadataToken(TokenType.MemberRef, constructorRid);
        var typeEncoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, constructorToken);

        var attributeRow = new CustomAttributeRow
        {
            ParentIndex = parentEncoded,
            TypeIndex = typeEncoded,
            ValueIndex = valueBlobIndex
        };

        _writer.AddCustomAttribute(attributeRow);
    }

    private uint FindOrCreateInternalsVisibleToConstructor()
    {
        // Look for existing TypeRef to InternalsVisibleToAttribute
        var typeRefRid = _reader.FindTypeRef("InternalsVisibleToAttribute", "System.Runtime.CompilerServices");

        if (typeRefRid.HasValue)
        {
            // Look for existing MemberRef to .ctor
            var memberRefRid = _reader.FindMemberRef(typeRefRid.Value, ".ctor");
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
                new(TokenType.AssemblyRef, resolutionScope.Value)),
            NameIndex = _writer.AddString("InternalsVisibleToAttribute"),
            NamespaceIndex = _writer.AddString("System.Runtime.CompilerServices")
        };
        var newTypeRefRid = _writer.AddTypeRef(typeRefRow);

        // Create MemberRef for .ctor(string)
        return CreateConstructorMemberRef(newTypeRefRid);
    }

    private uint? FindSystemRuntimeAssemblyRef()
    {
        // Look for common .NET runtime assembly references
        string[] runtimeAssemblyNames = ["System.Runtime", "mscorlib", "netstandard", "System.Private.CoreLib"];

        foreach (var name in runtimeAssemblyNames)
        {
            var found = _reader.FindAssemblyRef(name);
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
                new(TokenType.TypeRef, typeRefRid)),
            NameIndex = _writer.AddString(".ctor"),
            SignatureIndex = _writer.AddBlob(ctorSignature)
        };

        return _writer.AddMemberRef(memberRefRow);
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
    /// Saves the modified assembly.
    /// </summary>
    public void Save(string path, StrongNameKey? key = null)
    {
        // Build new metadata
        var newMetadata = _writer.Build();

        // Write PE file
        var peWriter = new PEWriter(_image, newMetadata);
        var data = peWriter.Build();

        // Sign if key provided
        if (key != null)
        {
            StrongNameSigner.Sign(data, key);
        }

        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Saves the modified assembly and copies PDB if present.
    /// </summary>
    public void SaveWithSymbols(string sourcePath, string targetPath, StrongNameKey? key = null)
    {
        Save(targetPath, key);
        PdbHandler.CopyExternalPdb(sourcePath, targetPath);
    }
}
