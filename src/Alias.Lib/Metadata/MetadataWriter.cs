using System.Text;
using Alias.Lib.Metadata.Tables;

namespace Alias.Lib.Metadata;

/// <summary>
/// Writes a modified metadata section.
/// </summary>
public sealed class MetadataWriter
{
    private readonly MetadataReader _reader;
    private readonly StringHeapBuilder _stringHeap = new();
    private readonly BlobHeapBuilder _blobHeap = new();
    private readonly GuidHeapBuilder _guidHeap = new();

    // Modifications
    private readonly Dictionary<uint, AssemblyRow> _modifiedAssemblyRows = new();
    private readonly Dictionary<uint, AssemblyRefRow> _modifiedAssemblyRefRows = new();
    private readonly Dictionary<uint, TypeDefRow> _modifiedTypeDefRows = new();
    private readonly List<CustomAttributeRow> _newCustomAttributes = new();
    private readonly List<TypeRefRow> _newTypeRefs = new();
    private readonly List<MemberRefRow> _newMemberRefs = new();

    public MetadataWriter(MetadataReader reader)
    {
        _reader = reader;

        // Copy existing heaps
        _stringHeap.CopyFrom(reader.StringHeap);
        _blobHeap.CopyFrom(reader.BlobHeap);
        _guidHeap.CopyFrom(reader.GuidHeap);
    }

    /// <summary>
    /// Adds or updates a string in the string heap.
    /// </summary>
    public uint AddString(string value) =>
        _stringHeap.GetOrAdd(value);

    /// <summary>
    /// Adds a blob to the blob heap.
    /// </summary>
    public uint AddBlob(byte[] value) =>
        _blobHeap.GetOrAdd(value);

    /// <summary>
    /// Modifies the assembly row.
    /// </summary>
    public void SetAssemblyRow(uint rid, AssemblyRow row) =>
        _modifiedAssemblyRows[rid] = row;

    /// <summary>
    /// Tries to get a previously modified assembly row.
    /// </summary>
    public bool TryGetModifiedAssemblyRow(uint rid, out AssemblyRow row) =>
        _modifiedAssemblyRows.TryGetValue(rid, out row);

    /// <summary>
    /// Modifies an assembly reference row.
    /// </summary>
    public void SetAssemblyRefRow(uint rid, AssemblyRefRow row) =>
        _modifiedAssemblyRefRows[rid] = row;

    /// <summary>
    /// Modifies a type definition row.
    /// </summary>
    public void SetTypeDefRow(uint rid, TypeDefRow row) =>
        _modifiedTypeDefRows[rid] = row;

    /// <summary>
    /// Adds a new custom attribute row.
    /// </summary>
    public void AddCustomAttribute(CustomAttributeRow row) =>
        _newCustomAttributes.Add(row);

    /// <summary>
    /// Adds a new TypeRef row and returns its RID.
    /// </summary>
    public uint AddTypeRef(TypeRefRow row)
    {
        _newTypeRefs.Add(row);
        var existingCount = _reader.TableHeap.GetRowCount(Table.TypeRef);
        return (uint)(existingCount + _newTypeRefs.Count);
    }

    /// <summary>
    /// Adds a new MemberRef row and returns its RID.
    /// </summary>
    public uint AddMemberRef(MemberRefRow row)
    {
        _newMemberRefs.Add(row);
        var existingCount = _reader.TableHeap.GetRowCount(Table.MemberRef);
        return (uint)(existingCount + _newMemberRefs.Count);
    }

    /// <summary>
    /// Builds the modified metadata section.
    /// </summary>
    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Build heaps
        var stringsData = _stringHeap.ToArray();
        var blobData = _blobHeap.ToArray();
        var guidData = _guidHeap.ToArray();
        var usHeapData = GetUserStringHeapData();

        // Build table heap with modifications
        var tableHeapData = BuildTableHeap();

        // Calculate sizes for heap indexes
        int stringIndexSize = stringsData.Length > 0xFFFF ? 4 : 2;
        int blobIndexSize = blobData.Length > 0xFFFF ? 4 : 2;
        int guidIndexSize = guidData.Length > 0xFFFF ? 4 : 2;

        // Write metadata root header
        writer.Write(0x424a5342u); // Signature: BSJB
        writer.Write((ushort)1);   // Major version
        writer.Write((ushort)1);   // Minor version
        writer.Write(0u);          // Reserved

        // Write version string (padded to 4-byte boundary)
        var versionString = _reader.Image.MetadataVersionString;
        var versionBytes = Encoding.UTF8.GetBytes(versionString);
        var versionLength = (versionBytes.Length + 4) & ~3;
        writer.Write(versionLength);
        writer.Write(versionBytes);
        for (int i = versionBytes.Length; i < versionLength; i++)
            writer.Write((byte)0);

        // Flags
        writer.Write((ushort)0);

        // Stream count
        int streamCount = 4; // #~, #Strings, #GUID, #Blob
        if (usHeapData.Length > 0) streamCount++;
        writer.Write((ushort)streamCount);

        // Calculate stream offsets
        var headerSize = (int)ms.Position;
        var streamHeadersSize = 8 * streamCount; // Offset + Size for each
        // Plus stream names with padding
        streamHeadersSize += 4;  // "#~\0\0"
        streamHeadersSize += 12; // "#Strings\0\0\0\0"
        if (usHeapData.Length > 0)
            streamHeadersSize += 4; // "#US\0"
        streamHeadersSize += 8;  // "#GUID\0\0\0"
        streamHeadersSize += 8;  // "#Blob\0\0\0"

        var streamsStart = headerSize + streamHeadersSize;

        // Align to 4 bytes
        streamsStart = (streamsStart + 3) & ~3;

        // Stream positions
        var tableHeapOffset = streamsStart;
        var stringsOffset = tableHeapOffset + tableHeapData.Length;
        var usOffset = stringsOffset + stringsData.Length;
        var guidOffset = usHeapData.Length > 0 ? usOffset + usHeapData.Length : usOffset;
        var blobOffset = guidOffset + guidData.Length;

        // Write stream headers
        // #~ (table heap)
        writer.Write((uint)(tableHeapOffset - 0)); // Relative to metadata root
        writer.Write((uint)tableHeapData.Length);
        writer.Write(Encoding.ASCII.GetBytes("#~"));
        writer.Write((ushort)0);

        // #Strings
        writer.Write((uint)(stringsOffset - 0));
        writer.Write((uint)stringsData.Length);
        writer.Write(Encoding.ASCII.GetBytes("#Strings"));
        for (int i = 0; i < 4; i++) writer.Write((byte)0);

        // #US (if present)
        if (usHeapData.Length > 0)
        {
            writer.Write((uint)(usOffset - 0));
            writer.Write((uint)usHeapData.Length);
            writer.Write(Encoding.ASCII.GetBytes("#US"));
            writer.Write((byte)0);
        }

        // #GUID
        writer.Write((uint)(guidOffset - 0));
        writer.Write((uint)guidData.Length);
        writer.Write(Encoding.ASCII.GetBytes("#GUID"));
        for (int i = 0; i < 3; i++) writer.Write((byte)0);

        // #Blob
        writer.Write((uint)(blobOffset - 0));
        writer.Write((uint)blobData.Length);
        writer.Write(Encoding.ASCII.GetBytes("#Blob"));
        for (int i = 0; i < 3; i++) writer.Write((byte)0);

        // Pad to stream start
        while (ms.Position < tableHeapOffset)
            writer.Write((byte)0);

        // Write streams
        writer.Write(tableHeapData);
        writer.Write(stringsData);
        if (usHeapData.Length > 0)
            writer.Write(usHeapData);
        writer.Write(guidData);
        writer.Write(blobData);

        // Pad to original metadata size to preserve debug directory offsets
        var originalSize = _reader.Image.MetadataSize;
        while (ms.Position < originalSize)
            writer.Write((byte)0);

        return ms.ToArray();
    }

    private byte[] GetUserStringHeapData()
    {
        // Copy existing #US heap if present
        var usHeader = _reader.Image.GetStreamHeader("#US");
        if (usHeader == null || usHeader.Size == 0)
            return [];

        var usData = new byte[usHeader.Size];
        Array.Copy(_reader.MetadataData, usHeader.Offset, usData, 0, usHeader.Size);
        return usData;
    }

    private byte[] BuildTableHeap()
    {
        var tableHeap = _reader.TableHeap;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Calculate new row counts
        var newCustomAttributeCount = tableHeap.GetRowCount(Table.CustomAttribute) + _newCustomAttributes.Count;
        var newTypeRefCount = tableHeap.GetRowCount(Table.TypeRef) + _newTypeRefs.Count;
        var newMemberRefCount = tableHeap.GetRowCount(Table.MemberRef) + _newMemberRefs.Count;

        // Calculate new index sizes based on modified heaps
        int stringIndexSize = _stringHeap.IndexSize;
        int blobIndexSize = _blobHeap.IndexSize;
        int guidIndexSize = _guidHeap.IndexSize;

        // Write table heap header
        writer.Write(0u);          // Reserved
        writer.Write((byte)2);     // MajorVersion
        writer.Write((byte)0);     // MinorVersion

        // HeapSizes
        byte heapSizes = 0;
        if (stringIndexSize == 4) heapSizes |= 0x01;
        if (guidIndexSize == 4) heapSizes |= 0x02;
        if (blobIndexSize == 4) heapSizes |= 0x04;
        writer.Write(heapSizes);

        writer.Write((byte)1);     // Reserved

        // Valid and Sorted bitmasks
        long valid = tableHeap.Valid;
        // Mark tables as present if we added new rows
        if (_newCustomAttributes.Count > 0)
            valid |= 1L << (int)Table.CustomAttribute;
        if (_newTypeRefs.Count > 0)
            valid |= 1L << (int)Table.TypeRef;
        if (_newMemberRefs.Count > 0)
            valid |= 1L << (int)Table.MemberRef;

        writer.Write(valid);
        writer.Write(tableHeap.Sorted);

        // Write row counts
        for (int i = 0; i < 58; i++)
        {
            if ((valid & (1L << i)) == 0)
                continue;

            int count = tableHeap.GetRowCount((Table)i);
            if ((Table)i == Table.CustomAttribute)
                count = newCustomAttributeCount;
            else if ((Table)i == Table.TypeRef)
                count = newTypeRefCount;
            else if ((Table)i == Table.MemberRef)
                count = newMemberRefCount;

            writer.Write((uint)count);
        }

        // Create a helper to compute new coded index sizes
        int GetNewCodedIndexSize(CodedIndex codedIndex)
        {
            return CodedIndexHelper.GetSize(codedIndex, t =>
            {
                if (t == Table.CustomAttribute)
                    return newCustomAttributeCount;
                if (t == Table.TypeRef)
                    return newTypeRefCount;
                if (t == Table.MemberRef)
                    return newMemberRefCount;
                return tableHeap.GetRowCount(t);
            });
        }

        // Write table rows
        for (int i = 0; i < 58; i++)
        {
            var table = (Table)i;
            if ((valid & (1L << i)) == 0)
                continue;

            var rowCount = tableHeap.GetRowCount(table);

            switch (table)
            {
                case Table.Assembly:
                    WriteAssemblyTable(writer, rowCount, blobIndexSize, stringIndexSize);
                    break;

                case Table.AssemblyRef:
                    WriteAssemblyRefTable(writer, rowCount, blobIndexSize, stringIndexSize);
                    break;

                case Table.TypeDef:
                    WriteTypeDefTable(writer, rowCount, stringIndexSize,
                        GetNewCodedIndexSize(CodedIndex.TypeDefOrRef),
                        tableHeap.GetTableIndexSize(Table.Field),
                        tableHeap.GetTableIndexSize(Table.Method));
                    break;

                case Table.TypeRef:
                    WriteTypeRefTable(writer, rowCount,
                        GetNewCodedIndexSize(CodedIndex.ResolutionScope),
                        stringIndexSize);
                    break;

                case Table.MemberRef:
                    WriteMemberRefTable(writer, rowCount,
                        GetNewCodedIndexSize(CodedIndex.MemberRefParent),
                        stringIndexSize, blobIndexSize);
                    break;

                case Table.CustomAttribute:
                    WriteCustomAttributeTable(writer, rowCount,
                        GetNewCodedIndexSize(CodedIndex.HasCustomAttribute),
                        GetNewCodedIndexSize(CodedIndex.CustomAttributeType),
                        blobIndexSize);
                    break;

                default:
                    // Copy table data unchanged
                    var tableData = tableHeap.GetTableData(table);
                    writer.Write(tableData);
                    break;
            }
        }

        return ms.ToArray();
    }

    private void WriteAssemblyTable(BinaryWriter writer, int rowCount, int blobIndexSize, int stringIndexSize)
    {
        for (uint rid = 1; rid <= rowCount; rid++)
        {
            AssemblyRow row;
            if (_modifiedAssemblyRows.TryGetValue(rid, out var modified))
                row = modified;
            else
                row = _reader.ReadAssemblyRow(rid);

            row.Write(writer, blobIndexSize, stringIndexSize);
        }
    }

    private void WriteAssemblyRefTable(BinaryWriter writer, int rowCount, int blobIndexSize, int stringIndexSize)
    {
        for (uint rid = 1; rid <= rowCount; rid++)
        {
            AssemblyRefRow row;
            if (_modifiedAssemblyRefRows.TryGetValue(rid, out var modified))
                row = modified;
            else
                row = _reader.ReadAssemblyRefRow(rid);

            row.Write(writer, blobIndexSize, stringIndexSize);
        }
    }

    private void WriteTypeDefTable(BinaryWriter writer, int rowCount, int stringIndexSize,
        int typeDefOrRefSize, int fieldIndexSize, int methodIndexSize)
    {
        for (uint rid = 1; rid <= rowCount; rid++)
        {
            TypeDefRow row;
            if (_modifiedTypeDefRows.TryGetValue(rid, out var modified))
                row = modified;
            else
                row = _reader.ReadTypeDefRow(rid);

            row.Write(writer, stringIndexSize, typeDefOrRefSize, fieldIndexSize, methodIndexSize);
        }
    }

    private void WriteCustomAttributeTable(BinaryWriter writer, int existingRowCount,
        int hasCustomAttributeSize, int customAttributeTypeSize, int blobIndexSize)
    {
        // Collect all rows (existing + new)
        var allRows = new List<CustomAttributeRow>();

        // Add existing rows
        for (uint rid = 1; rid <= existingRowCount; rid++)
        {
            allRows.Add(_reader.ReadCustomAttributeRow(rid));
        }

        // Add new rows
        allRows.AddRange(_newCustomAttributes);

        // Sort by Parent column (ECMA-335 requires CustomAttribute table to be sorted by Parent)
        allRows.Sort((a, b) => a.ParentIndex.CompareTo(b.ParentIndex));

        // Write all sorted rows
        foreach (var row in allRows)
        {
            row.Write(writer, hasCustomAttributeSize, customAttributeTypeSize, blobIndexSize);
        }
    }

    private void WriteTypeRefTable(BinaryWriter writer, int existingRowCount,
        int resolutionScopeSize, int stringIndexSize)
    {
        // Write existing rows
        for (uint rid = 1; rid <= existingRowCount; rid++)
        {
            var row = _reader.ReadTypeRefRow(rid);
            row.Write(writer, resolutionScopeSize, stringIndexSize);
        }

        // Write new rows
        foreach (var row in _newTypeRefs)
        {
            row.Write(writer, resolutionScopeSize, stringIndexSize);
        }
    }

    private void WriteMemberRefTable(BinaryWriter writer, int existingRowCount,
        int memberRefParentSize, int stringIndexSize, int blobIndexSize)
    {
        // Write existing rows
        for (uint rid = 1; rid <= existingRowCount; rid++)
        {
            var row = _reader.ReadMemberRefRow(rid);
            row.Write(writer, memberRefParentSize, stringIndexSize, blobIndexSize);
        }

        // Write new rows
        foreach (var row in _newMemberRefs)
        {
            row.Write(writer, memberRefParentSize, stringIndexSize, blobIndexSize);
        }
    }
}
