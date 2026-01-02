/// <summary>
/// High-level API for modifying .NET assemblies using streaming to minimize memory usage.
/// Only loads headers on open (~4KB), streams unchanged data during save.
/// </summary>
sealed class StreamingAssemblyModifier : IDisposable
{
    // Constructor signature: HASTHIS (0x20) | ParamCount(1) | ReturnType:VOID(0x01) | Param:STRING(0x0E)
    static readonly byte[] IvtCtorSignature = [0x20, 0x01, 0x01, 0x0E];
    static readonly byte[] PublicKeyPrefix = ", PublicKey="u8.ToArray();
    static readonly string[] RuntimeAssemblyNames = ["System.Runtime", "mscorlib", "netstandard", "System.Private.CoreLib"];

    // Assembly row is always RID 1, encoded as HasCustomAttribute
    static readonly uint AssemblyParentEncoded = CodedIndexHelper.EncodeToken(
        CodedIndex.HasCustomAttribute,
        new(TableIndex.Assembly, 1));

    StreamingPEFile peFile;
    StreamingMetadataReader metadata;
    ModificationPlan plan;
    bool disposed;

    StreamingAssemblyModifier(StreamingPEFile peFile, StreamingMetadataReader metadata, ModificationPlan plan)
    {
        this.peFile = peFile;
        this.metadata = metadata;
        this.plan = plan;
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

    public string SourcePath => peFile.FilePath;

    public void SetAssemblyName(string name) =>
        plan.SetAssemblyName(name);

    public void SetAssemblyPublicKey(byte[] publicKey) =>
        plan.SetAssemblyPublicKey(publicKey);

    public void ClearStrongName() =>
        plan.ClearStrongName();

    public bool RedirectAssemblyRef(string sourceName, string targetName, byte[]? publicKeyToken = null) =>
        plan.RedirectAssemblyRef(sourceName, targetName, publicKeyToken);

    public void MakeTypesInternal() =>
        plan.MakeTypesInternal();

    public void AddInternalsVisibleTo(string assemblyName, byte[]? publicKey = null)
    {
        // Find or create reference to InternalsVisibleToAttribute
        var constructorRid = FindOrCreateInternalsVisibleToConstructor();

        var valueBlobIndex = AddValueBlob(assemblyName, publicKey);
        var typeEncoded = CodedIndexHelper.EncodeToken(
            CodedIndex.CustomAttributeType,
            new(TableIndex.MemberRef, constructorRid));

        plan.AddCustomAttribute(
            new()
            {
                ParentIndex = AssemblyParentEncoded,
                TypeIndex = typeEncoded,
                ValueIndex = valueBlobIndex
            });
    }

    uint AddValueBlob(string assemblyName, byte[]? publicKey)
    {
        var builder = new BlobBuilder();
        // Prolog
        builder.WriteUInt16(0x0001);

        if (publicKey is {Length: > 0})
        {
            // Write serialized string without intermediate allocations
            // Format: "{assemblyName}, PublicKey={hex}"
            var nameByteCount = Encoding.UTF8.GetByteCount(assemblyName);
            builder.WriteCompressedInteger(nameByteCount + 12 + publicKey.Length * 2);
            builder.WriteUTF8(assemblyName);
            builder.WriteBytes(PublicKeyPrefix);
            foreach (var b in publicKey)
            {
                builder.WriteByte((byte) (b >> 4 < 10 ? '0' + (b >> 4) : 'A' + (b >> 4) - 10));
                builder.WriteByte((byte) ((b & 0xF) < 10 ? '0' + (b & 0xF) : 'A' + (b & 0xF) - 10));
            }
        }
        else
        {
            builder.WriteSerializedString(assemblyName);
        }

        // No named arguments
        builder.WriteUInt16(0x0000);
        return plan.GetOrAddBlob(builder.ToArray());
    }

    uint FindOrCreateInternalsVisibleToConstructor()
    {
        // Look for existing TypeRef to InternalsVisibleToAttribute
        var typeRefRid = metadata.FindTypeRef("InternalsVisibleToAttribute", "System.Runtime.CompilerServices");

        if (typeRefRid.HasValue)
        {
            // Look for existing MemberRef to .ctor, or create one
            return metadata.FindMemberRef(typeRefRid.Value, ".ctor") ?? CreateCtorMemberRef(typeRefRid.Value);
        }

        // Find resolution scope (System.Runtime or mscorlib)
        var scopeRid = RuntimeAssemblyNames
                           .Select(name => metadata.FindAssemblyRef(name))
                           .FirstOrDefault(r => r.HasValue)?.rid
                       ?? throw new InvalidOperationException("Could not find runtime assembly reference for InternalsVisibleToAttribute");

        // Create TypeRef for InternalsVisibleToAttribute
        var newTypeRefRid = plan.AddTypeRef(
            new()
            {
                ResolutionScopeIndex = CodedIndexHelper.EncodeToken(CodedIndex.ResolutionScope, new(TableIndex.AssemblyRef, scopeRid)),
                NameIndex = plan.GetOrAddString("InternalsVisibleToAttribute"),
                NamespaceIndex = plan.GetOrAddString("System.Runtime.CompilerServices")
            });

        return CreateCtorMemberRef(newTypeRefRid);
    }

    uint CreateCtorMemberRef(uint typeRefRid) =>
        plan.AddMemberRef(
            new()
            {
                ClassIndex = CodedIndexHelper.EncodeToken(CodedIndex.MemberRefParent, new(TableIndex.TypeRef, typeRefRid)),
                NameIndex = plan.GetOrAddString(".ctor"),
                SignatureIndex = plan.GetOrAddBlob(IvtCtorSignature)
            });

    /// <summary>
    /// Saves the modified assembly using the most efficient strategy.
    /// </summary>
    public void Save(string path, StrongNameKey? key = null)
    {
        var strategy = plan.GetStrategy();

        // Check if we're writing to the same file we're reading from
        var isSameFile = string.Equals(
            Path.GetFullPath(peFile.FilePath),
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

        CopyExternalPdb(peFile.FilePath, path);
    }

    static void CopyExternalPdb(string sourceDll, string targetDll)
    {
        var sourcePdb = Path.ChangeExtension(sourceDll, ".pdb");
        var targetPdb = Path.ChangeExtension(targetDll, ".pdb");

        // Skip if source and target are the same file
        if (string.Equals(Path.GetFullPath(sourcePdb), Path.GetFullPath(targetPdb), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(sourcePdb))
        {
            // Copy the PDB file - no modifications needed since we don't change method tokens
            File.Copy(sourcePdb, targetPdb, overwrite: true);
        }
    }

    void SaveWithInPlacePatching(string path, StrongNameKey? key, bool isSameFile)
    {
        // Build patch list first (while source is still open)
        var patches = BuildInPlacePatches();

        if (isSameFile)
        {
            // Writing to same file - need to close source first, then patch in place
            peFile.Dispose();
        }
        else
        {
            // Copy entire source file to new location
            File.Copy(peFile.FilePath, path, overwrite: true);
        }

        if (patches.Count > 0)
        {
            StreamingPEWriter.ApplyInPlacePatches(path, patches);
        }

        // Sign if key provided
        if (key != null)
        {
            StreamingStrongNameSigner.SignFile(path, key);
        }
    }

    List<(long offset, byte[] data)> BuildInPlacePatches()
    {
        var patches = new List<(long offset, byte[] data)>(
            plan.ModifiedAssemblyRows.Count +
            plan.ModifiedAssemblyRefRows.Count +
            plan.ModifiedTypeDefRows.Count);

        // Patch Assembly table row
        foreach (var (rid, row) in plan.ModifiedAssemblyRows)
        {
            var offset = metadata.GetRowOffset(TableIndex.Assembly, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, metadata.BlobIndexSize, metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch AssemblyRef table rows
        foreach (var (rid, row) in plan.ModifiedAssemblyRefRows)
        {
            var offset = metadata.GetRowOffset(TableIndex.AssemblyRef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, metadata.BlobIndexSize, metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch TypeDef table rows
        foreach (var (rid, row) in plan.ModifiedTypeDefRows)
        {
            var offset = metadata.GetRowOffset(TableIndex.TypeDef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, metadata.StringIndexSize,
                metadata.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                metadata.GetTableIndexSize(TableIndex.Field),
                metadata.GetTableIndexSize(TableIndex.MethodDef));
            patches.Add((offset, ms.ToArray()));
        }

        return patches;
    }

    void SaveWithMetadataRebuild(string path, StrongNameKey? key, bool isSameFile)
    {
        var targetPath = path;
        string? tempPath = null;

        if (isSameFile)
        {
            // Write to temp file first, then replace after closing source
            tempPath = path + ".tmp";
            targetPath = tempPath;
        }

        using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var peWriter = new StreamingPEWriter(peFile, metadata, plan);
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
            metadata.Dispose();
            peFile.Dispose();
            File.Delete(path);
            File.Move(tempPath, path);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        metadata.Dispose();
        peFile.Dispose();
    }
}