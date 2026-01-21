/// <summary>
/// Collects planned modifications and determines optimal output strategy.
/// </summary>
sealed class ModificationPlan(StreamingMetadataReader metadata)
{
    // Modified rows (keyed by RID)
    Dictionary<uint, AssemblyRow> assemblyMods = new();
    Dictionary<uint, AssemblyRefRow> assemblyRefMods = new();
    Dictionary<uint, TypeDefRow> typeDefMods = new();

    // New rows to add
    List<CustomAttributeRow> newCustomAttributes = [];
    List<TypeRefRow> newTypeRefs = [];
    List<MemberRefRow> newMemberRefs = [];

    // String additions (value -> new index)
    Dictionary<string, uint> newStrings = new();
    uint nextStringIndex = metadata.StringHeapSize;

    // Blob additions (value -> new index)
    List<(byte[] data, uint index)> newBlobs = [];
    uint nextBlobIndex = metadata.BlobHeapSize;

    // Tracks if we need to add new heap data
    bool hasNewStrings;
    bool hasNewBlobs;
    bool hasNewRows;

    #region Row Modifications

    /// <summary>
    /// Sets a modified assembly row.
    /// </summary>
    public void SetAssemblyRow(uint rid, AssemblyRow row) =>
        assemblyMods[rid] = row;

    /// <summary>
    /// Gets the assembly row (modified or original).
    /// </summary>
    public AssemblyRow GetAssemblyRow(uint rid)
    {
        if (assemblyMods.TryGetValue(rid, out var modified))
        {
            return modified;
        }

        return metadata.ReadAssemblyRow(rid);
    }

    /// <summary>
    /// Sets a modified assembly reference row.
    /// </summary>
    public void SetAssemblyRefRow(uint rid, AssemblyRefRow row) =>
        assemblyRefMods[rid] = row;

    /// <summary>
    /// Gets the assembly reference row (modified or original).
    /// </summary>
    public AssemblyRefRow GetAssemblyRefRow(uint rid)
    {
        if (assemblyRefMods.TryGetValue(rid, out var modified))
        {
            return modified;
        }

        return metadata.ReadAssemblyRefRow(rid);
    }

    /// <summary>
    /// Sets a modified type definition row.
    /// </summary>
    public void SetTypeDefRow(uint rid, TypeDefRow row) =>
        typeDefMods[rid] = row;

    /// <summary>
    /// Gets the type definition row (modified or original).
    /// </summary>
    public TypeDefRow GetTypeDefRow(uint rid)
    {
        if (typeDefMods.TryGetValue(rid, out var modified))
        {
            return modified;
        }

        return metadata.ReadTypeDefRow(rid);
    }

    /// <summary>
    /// Adds a new custom attribute row.
    /// </summary>
    public void AddCustomAttribute(CustomAttributeRow row)
    {
        newCustomAttributes.Add(row);
        hasNewRows = true;
    }

    /// <summary>
    /// Adds a new type reference row.
    /// Returns the RID (1-based, counting from after existing rows).
    /// </summary>
    public uint AddTypeRef(TypeRefRow row)
    {
        newTypeRefs.Add(row);
        hasNewRows = true;
        return (uint) (metadata.GetRowCount(TableIndex.TypeRef) + newTypeRefs.Count);
    }

    /// <summary>
    /// Adds a new member reference row.
    /// Returns the RID (1-based, counting from after existing rows).
    /// </summary>
    public uint AddMemberRef(MemberRefRow row)
    {
        newMemberRefs.Add(row);
        hasNewRows = true;
        return (uint) (metadata.GetRowCount(TableIndex.MemberRef) + newMemberRefs.Count);
    }

    #endregion

    #region Heap Modifications

    /// <summary>
    /// Gets or adds a string to the heap.
    /// Returns the index (existing or new).
    /// </summary>
    public uint GetOrAddString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        // Check if we already added it in this modification plan
        if (newStrings.TryGetValue(value, out var existing))
        {
            return existing;
        }

        // ECMA-335 II.24.2.3: String heap deduplication (optimization, not required for compliance)
        // Note: ECMA-335 allows duplicate strings in the heap ("The physical heap can contain garbage").
        // Searching the existing heap would require building a reverse index (string -> offset) which
        // adds complexity and memory overhead. Current implementation only deduplicates within new strings.
        // This is a valid trade-off: correctness vs optimization.

        var index = nextStringIndex;
        nextStringIndex += (uint) Encoding.UTF8.GetByteCount(value) + 1; // +1 for null terminator

        newStrings[value] = index;
        hasNewStrings = true;

        return index;
    }

    /// <summary>
    /// Gets or adds a blob to the heap.
    /// Returns the index.
    /// </summary>
    public uint GetOrAddBlob(byte[] value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        // Calculate compressed length header size
        int headerSize;
        if (value.Length < 0x80)
        {
            headerSize = 1;
        }
        else if (value.Length < 0x4000)
        {
            headerSize = 2;
        }
        else
        {
            headerSize = 4;
        }

        var index = nextBlobIndex;
        nextBlobIndex += (uint) (headerSize + value.Length);

        newBlobs.Add((value, index));
        hasNewBlobs = true;

        return index;
    }

    #endregion

    #region Strategy Detection

    /// <summary>
    /// Gets the new strings added to the plan.
    /// </summary>
    public IReadOnlyDictionary<string, uint> NewStrings => newStrings;

    /// <summary>
    /// Gets the new blobs added to the plan.
    /// </summary>
    public IReadOnlyList<(byte[] data, uint index)> NewBlobs => newBlobs;

    /// <summary>
    /// Gets the new custom attributes added to the plan.
    /// </summary>
    public IReadOnlyList<CustomAttributeRow> NewCustomAttributes => newCustomAttributes;

    /// <summary>
    /// Gets the new type references added to the plan.
    /// </summary>
    public IReadOnlyList<TypeRefRow> NewTypeRefs => newTypeRefs;

    /// <summary>
    /// Gets the new member references added to the plan.
    /// </summary>
    public IReadOnlyList<MemberRefRow> NewMemberRefs => newMemberRefs;

    /// <summary>
    /// Gets modified assembly rows.
    /// </summary>
    public IReadOnlyDictionary<uint, AssemblyRow> ModifiedAssemblyRows => assemblyMods;

    /// <summary>
    /// Gets modified assembly reference rows.
    /// </summary>
    public IReadOnlyDictionary<uint, AssemblyRefRow> ModifiedAssemblyRefRows => assemblyRefMods;

    /// <summary>
    /// Gets modified type definition rows.
    /// </summary>
    public IReadOnlyDictionary<uint, TypeDefRow> ModifiedTypeDefRows => typeDefMods;

    /// <summary>
    /// Returns true if modifications require adding new heap data.
    /// </summary>
    bool RequiresHeapGrowth => hasNewStrings || hasNewBlobs;

    /// <summary>
    /// Returns true if modifications require adding new table rows.
    /// </summary>
    bool RequiresNewRows => hasNewRows;

    /// <summary>
    /// Returns true if only in-place byte patches are needed (no size changes).
    /// </summary>
    bool CanPatchInPlace => !RequiresHeapGrowth && !RequiresNewRows;

    /// <summary>
    /// Estimates the new metadata size after all modifications.
    /// </summary>
    public int EstimateNewMetadataSize()
    {
        // Start with current size
        var size = (int) metadata.PEFile.MetadataSize;

        // Add new string heap data
        foreach (var kvp in newStrings)
        {
            size += Encoding.UTF8.GetByteCount(kvp.Key) + 1;
        }

        // Add new blob heap data
        foreach (var (data, _) in newBlobs)
        {
            size += data.Length + 4; // max header size
        }

        // Add new table rows
        if (newCustomAttributes.Count > 0)
        {
            size += newCustomAttributes.Count * metadata.GetRowSize(TableIndex.CustomAttribute);
        }

        if (newTypeRefs.Count > 0)
        {
            size += newTypeRefs.Count * metadata.GetRowSize(TableIndex.TypeRef);
        }

        if (newMemberRefs.Count > 0)
        {
            size += newMemberRefs.Count * metadata.GetRowSize(TableIndex.MemberRef);
        }

        // Account for table row count field growth (4 bytes per new table that wasn't there)
        // This is a minor factor, usually tables already exist

        return size;
    }

    /// <summary>
    /// Determines the optimal output strategy based on modifications.
    /// </summary>
    public ModificationStrategy GetStrategy()
    {
        if (CanPatchInPlace)
        {
            return ModificationStrategy.InPlacePatch;
        }

        // Check if new metadata fits in existing section with padding
        var currentSize = (int) metadata.PEFile.MetadataSize;
        var estimatedSize = EstimateNewMetadataSize();

        // Get the section containing metadata
        var metadataSection = metadata.PEFile.GetSectionAtRva(metadata.PEFile.MetadataRva);
        if (metadataSection != null)
        {
            // Calculate available padding in section
            var metadataEndInSection = metadata.PEFile.MetadataRva - metadataSection.VirtualAddress + currentSize;
            var availableSpace = metadataSection.SizeOfRawData - metadataEndInSection;

            if (estimatedSize <= currentSize + availableSpace)
            {
                return ModificationStrategy.MetadataRebuildWithPadding;
            }
        }

        return ModificationStrategy.FullMetadataSectionRebuild;
    }

    #endregion

    #region High-Level Modification API

    /// <summary>
    /// Sets the assembly name.
    /// </summary>
    public void SetAssemblyName(string name)
    {
        var row = GetAssemblyRow(1);
        row.NameIndex = GetOrAddString(name);
        SetAssemblyRow(1, row);
    }

    /// <summary>
    /// Sets the assembly public key.
    /// </summary>
    public void SetAssemblyPublicKey(byte[] publicKey)
    {
        var row = GetAssemblyRow(1);
        row.PublicKeyIndex = GetOrAddBlob(publicKey);
        SetAssemblyRow(1, row);
    }

    /// <summary>
    /// Clears the assembly strong name (sets public key to empty).
    /// </summary>
    public void ClearStrongName()
    {
        var row = GetAssemblyRow(1);
        row.PublicKeyIndex = 0;
        SetAssemblyRow(1, row);
    }

    /// <summary>
    /// Redirects an assembly reference to a new name and optional public key token.
    /// </summary>
    public bool RedirectAssemblyRef(string sourceName, string targetName, byte[]? publicKeyToken)
    {
        var found = metadata.FindAssemblyRef(sourceName);
        if (found == null)
        {
            return false;
        }

        var (rid, row) = found.Value;
        row.NameIndex = GetOrAddString(targetName);
        if (publicKeyToken == null)
        {
            row.PublicKeyOrTokenIndex = 0;
        }
        else
        {
            row.PublicKeyOrTokenIndex = GetOrAddBlob(publicKeyToken);
        }

        SetAssemblyRefRow(rid, row);
        return true;
    }

    /// <summary>
    /// Makes all public types internal.
    /// </summary>
    public void MakeTypesInternal()
    {
        var count = metadata.GetRowCount(TableIndex.TypeDef);
        for (uint rid = 1; rid <= count; rid++)
        {
            var row = GetTypeDefRow(rid);
            if (row.IsPublic)
            {
                row.MakeInternal();
                SetTypeDefRow(rid, row);
            }
        }
    }

    #endregion
}