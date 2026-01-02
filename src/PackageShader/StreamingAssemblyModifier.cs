/// <summary>
/// High-level API for modifying .NET assemblies using streaming to minimize memory usage.
/// Only loads headers on open (~4KB), streams unchanged data during save.
/// </summary>
sealed class StreamingAssemblyModifier : IDisposable
{
    // Constructor signature: HASTHIS (0x20) | ParamCount(1) | ReturnType:VOID(0x01) | Param:STRING(0x0E)
    static readonly byte[] IvtCtorSignature = [0x20, 0x01, 0x01, 0x0E];
    static readonly string[] RuntimeAssemblyNames = ["System.Runtime", "mscorlib", "netstandard", "System.Private.CoreLib"];

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

    /// <summary>
    /// Gets the source file path.
    /// </summary>
    public string SourcePath => peFile.FilePath;

    /// <summary>
    /// Sets the assembly name.
    /// </summary>
    public void SetAssemblyName(string name) =>
        plan.SetAssemblyName(name);

    /// <summary>
    /// Sets the assembly public key.
    /// </summary>
    public void SetAssemblyPublicKey(byte[] publicKey) =>
        plan.SetAssemblyPublicKey(publicKey);

    /// <summary>
    /// Clears the assembly's strong name (removes public key).
    /// </summary>
    public void ClearStrongName() =>
        plan.ClearStrongName();

    /// <summary>
    /// Redirects an assembly reference to a new name.
    /// </summary>
    /// <param name="sourceName">The original assembly name to find.</param>
    /// <param name="targetName">The new assembly name.</param>
    /// <param name="publicKeyToken">The new public key token, or null to clear it.</param>
    public bool RedirectAssemblyRef(string sourceName, string targetName, byte[]? publicKeyToken = null) =>
        plan.RedirectAssemblyRef(sourceName, targetName, publicKeyToken);

    /// <summary>
    /// Makes all public types internal.
    /// </summary>
    public void MakeTypesInternal() =>
        plan.MakeTypesInternal();

    /// <summary>
    /// Adds an InternalsVisibleTo attribute.
    /// </summary>
    public void AddInternalsVisibleTo(string assemblyName, byte[]? publicKey = null)
    {
        // Find or create reference to InternalsVisibleToAttribute
        var constructorRid = FindOrCreateInternalsVisibleToConstructor();

        var valueBlobIndex = AddValueBlob(assemblyName, publicKey);

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

        plan.AddCustomAttribute(attributeRow);
    }

    uint AddValueBlob(string assemblyName, byte[]? publicKey)
    {
        // Build attribute value

        var builder = new BlobBuilder();
        builder.WriteUInt16(0x0001); // Prolog
        if (publicKey is {Length: > 0})
        {
            builder.WriteSerializedString($"{assemblyName}, PublicKey={Convert.ToHexString(publicKey)}");
        }
        else
        {
            builder.WriteSerializedString(assemblyName);
        }
        builder.WriteUInt16(0x0000); // No named arguments
        var valueBlob = builder.ToArray();
        return plan.GetOrAddBlob(valueBlob);
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

    public static void CopyExternalPdb(string sourceDll, string targetDll)
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

            // Apply patches directly to the file
            if (patches.Count > 0)
            {
                StreamingPEWriter.ApplyInPlacePatches(path, patches);
            }
        }
        else
        {
            // Copy entire source file to new location
            File.Copy(peFile.FilePath, path, overwrite: true);

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