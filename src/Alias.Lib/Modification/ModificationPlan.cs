using System.Reflection.Metadata.Ecma335;
using Alias.Lib.Metadata;
using Alias.Lib.Metadata.Tables;

namespace Alias.Lib.Modification;

/// <summary>
/// Output strategy based on modifications required.
/// </summary>
public enum ModificationStrategy
{
    /// <summary>
    /// Direct byte patches - no metadata size change.
    /// Only modifying existing rows with indices that already exist in heaps.
    /// </summary>
    InPlacePatch,

    /// <summary>
    /// Metadata needs rebuild but fits in existing section padding.
    /// </summary>
    MetadataRebuildWithPadding,

    /// <summary>
    /// Metadata section must grow, shifting subsequent sections.
    /// </summary>
    FullMetadataSectionRebuild
}

/// <summary>
/// Collects planned modifications and determines optimal output strategy.
/// </summary>
public sealed class ModificationPlan
{
    private readonly StreamingMetadataReader _metadata;

    // Modified rows (keyed by RID)
    private readonly Dictionary<uint, AssemblyRow> _assemblyMods = new();
    private readonly Dictionary<uint, AssemblyRefRow> _assemblyRefMods = new();
    private readonly Dictionary<uint, TypeDefRow> _typeDefMods = new();

    // New rows to add
    private readonly List<CustomAttributeRow> _newCustomAttributes = new();
    private readonly List<TypeRefRow> _newTypeRefs = new();
    private readonly List<MemberRefRow> _newMemberRefs = new();

    // String additions (value -> new index)
    private readonly Dictionary<string, uint> _newStrings = new();
    private uint _nextStringIndex;

    // Blob additions (value -> new index)
    private readonly List<(byte[] data, uint index)> _newBlobs = new();
    private uint _nextBlobIndex;

    // Tracks if we need to add new heap data
    private bool _hasNewStrings;
    private bool _hasNewBlobs;
    private bool _hasNewRows;

    public ModificationPlan(StreamingMetadataReader metadata)
    {
        _metadata = metadata;
        _nextStringIndex = metadata.StringHeapSize;
        _nextBlobIndex = metadata.BlobHeapSize;
    }

    #region Row Modifications

    /// <summary>
    /// Sets a modified assembly row.
    /// </summary>
    public void SetAssemblyRow(uint rid, AssemblyRow row) =>
        _assemblyMods[rid] = row;

    /// <summary>
    /// Gets the assembly row (modified or original).
    /// </summary>
    public AssemblyRow GetAssemblyRow(uint rid)
    {
        if (_assemblyMods.TryGetValue(rid, out var modified))
            return modified;
        return _metadata.ReadAssemblyRow(rid);
    }

    /// <summary>
    /// Sets a modified assembly reference row.
    /// </summary>
    public void SetAssemblyRefRow(uint rid, AssemblyRefRow row) =>
        _assemblyRefMods[rid] = row;

    /// <summary>
    /// Gets the assembly reference row (modified or original).
    /// </summary>
    public AssemblyRefRow GetAssemblyRefRow(uint rid)
    {
        if (_assemblyRefMods.TryGetValue(rid, out var modified))
            return modified;
        return _metadata.ReadAssemblyRefRow(rid);
    }

    /// <summary>
    /// Sets a modified type definition row.
    /// </summary>
    public void SetTypeDefRow(uint rid, TypeDefRow row) =>
        _typeDefMods[rid] = row;

    /// <summary>
    /// Gets the type definition row (modified or original).
    /// </summary>
    public TypeDefRow GetTypeDefRow(uint rid)
    {
        if (_typeDefMods.TryGetValue(rid, out var modified))
            return modified;
        return _metadata.ReadTypeDefRow(rid);
    }

    /// <summary>
    /// Adds a new custom attribute row.
    /// </summary>
    public void AddCustomAttribute(CustomAttributeRow row)
    {
        _newCustomAttributes.Add(row);
        _hasNewRows = true;
    }

    /// <summary>
    /// Adds a new type reference row.
    /// Returns the RID (1-based, counting from after existing rows).
    /// </summary>
    public uint AddTypeRef(TypeRefRow row)
    {
        _newTypeRefs.Add(row);
        _hasNewRows = true;
        return (uint)(_metadata.GetRowCount(TableIndex.TypeRef) + _newTypeRefs.Count);
    }

    /// <summary>
    /// Adds a new member reference row.
    /// Returns the RID (1-based, counting from after existing rows).
    /// </summary>
    public uint AddMemberRef(MemberRefRow row)
    {
        _newMemberRefs.Add(row);
        _hasNewRows = true;
        return (uint)(_metadata.GetRowCount(TableIndex.MemberRef) + _newMemberRefs.Count);
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
            return 0;

        // Check if we already added it
        if (_newStrings.TryGetValue(value, out var existing))
            return existing;

        // TODO: Could search existing heap, but for now just add new
        // This is a simplification - production code should search first

        var index = _nextStringIndex;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        _nextStringIndex += (uint)bytes.Length + 1; // +1 for null terminator

        _newStrings[value] = index;
        _hasNewStrings = true;

        return index;
    }

    /// <summary>
    /// Gets or adds a blob to the heap.
    /// Returns the index.
    /// </summary>
    public uint GetOrAddBlob(byte[] value)
    {
        if (value.Length == 0)
            return 0;

        // Calculate compressed length header size
        int headerSize;
        if (value.Length < 0x80)
            headerSize = 1;
        else if (value.Length < 0x4000)
            headerSize = 2;
        else
            headerSize = 4;

        var index = _nextBlobIndex;
        _nextBlobIndex += (uint)(headerSize + value.Length);

        _newBlobs.Add((value, index));
        _hasNewBlobs = true;

        return index;
    }

    #endregion

    #region Strategy Detection

    /// <summary>
    /// Gets the new strings added to the plan.
    /// </summary>
    public IReadOnlyDictionary<string, uint> NewStrings => _newStrings;

    /// <summary>
    /// Gets the new blobs added to the plan.
    /// </summary>
    public IReadOnlyList<(byte[] data, uint index)> NewBlobs => _newBlobs;

    /// <summary>
    /// Gets the new custom attributes added to the plan.
    /// </summary>
    public IReadOnlyList<CustomAttributeRow> NewCustomAttributes => _newCustomAttributes;

    /// <summary>
    /// Gets the new type references added to the plan.
    /// </summary>
    public IReadOnlyList<TypeRefRow> NewTypeRefs => _newTypeRefs;

    /// <summary>
    /// Gets the new member references added to the plan.
    /// </summary>
    public IReadOnlyList<MemberRefRow> NewMemberRefs => _newMemberRefs;

    /// <summary>
    /// Gets modified assembly rows.
    /// </summary>
    public IReadOnlyDictionary<uint, AssemblyRow> ModifiedAssemblyRows => _assemblyMods;

    /// <summary>
    /// Gets modified assembly reference rows.
    /// </summary>
    public IReadOnlyDictionary<uint, AssemblyRefRow> ModifiedAssemblyRefRows => _assemblyRefMods;

    /// <summary>
    /// Gets modified type definition rows.
    /// </summary>
    public IReadOnlyDictionary<uint, TypeDefRow> ModifiedTypeDefRows => _typeDefMods;

    /// <summary>
    /// Returns true if modifications require adding new heap data.
    /// </summary>
    public bool RequiresHeapGrowth => _hasNewStrings || _hasNewBlobs;

    /// <summary>
    /// Returns true if modifications require adding new table rows.
    /// </summary>
    public bool RequiresNewRows => _hasNewRows;

    /// <summary>
    /// Returns true if only in-place byte patches are needed (no size changes).
    /// </summary>
    public bool CanPatchInPlace => !RequiresHeapGrowth && !RequiresNewRows;

    /// <summary>
    /// Estimates the new metadata size after all modifications.
    /// </summary>
    public int EstimateNewMetadataSize()
    {
        // Start with current size
        var size = (int)_metadata.PEFile.MetadataSize;

        // Add new string heap data
        foreach (var kvp in _newStrings)
        {
            size += System.Text.Encoding.UTF8.GetByteCount(kvp.Key) + 1;
        }

        // Add new blob heap data
        foreach (var (data, _) in _newBlobs)
        {
            size += data.Length + 4; // max header size
        }

        // Add new table rows
        if (_newCustomAttributes.Count > 0)
        {
            size += _newCustomAttributes.Count * _metadata.GetRowSize(TableIndex.CustomAttribute);
        }
        if (_newTypeRefs.Count > 0)
        {
            size += _newTypeRefs.Count * _metadata.GetRowSize(TableIndex.TypeRef);
        }
        if (_newMemberRefs.Count > 0)
        {
            size += _newMemberRefs.Count * _metadata.GetRowSize(TableIndex.MemberRef);
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
            return ModificationStrategy.InPlacePatch;

        // Check if new metadata fits in existing section with padding
        var currentSize = (int)_metadata.PEFile.MetadataSize;
        var estimatedSize = EstimateNewMetadataSize();

        // Get the section containing metadata
        var metadataSection = _metadata.PEFile.GetSectionAtRva(_metadata.PEFile.MetadataRva);
        if (metadataSection != null)
        {
            // Calculate available padding in section
            var metadataEndInSection = _metadata.PEFile.MetadataRva - metadataSection.VirtualAddress + currentSize;
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
        var found = _metadata.FindAssemblyRef(sourceName);
        if (found == null)
            return false;

        var (rid, row) = found.Value;
        row.NameIndex = GetOrAddString(targetName);
        if (publicKeyToken != null)
        {
            row.PublicKeyOrTokenIndex = GetOrAddBlob(publicKeyToken);
        }
        else
        {
            row.PublicKeyOrTokenIndex = 0;
        }
        SetAssemblyRefRow(rid, row);
        return true;
    }

    /// <summary>
    /// Makes all public types internal.
    /// </summary>
    public void MakeTypesInternal()
    {
        var count = _metadata.GetRowCount(TableIndex.TypeDef);
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
