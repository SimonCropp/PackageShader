using System.Text;
using Alias.Lib.Metadata.Tables;
using Alias.Lib.Modification;

namespace Alias.Lib.Metadata;

/// <summary>
/// Writes metadata section incrementally to a stream, applying modifications.
/// </summary>
public sealed class StreamingMetadataWriter
{
    private readonly StreamingMetadataReader _source;
    private readonly ModificationPlan _plan;

    public StreamingMetadataWriter(StreamingMetadataReader source, ModificationPlan plan)
    {
        _source = source;
        _plan = plan;
    }

    /// <summary>
    /// Writes the complete metadata section to the output stream.
    /// </summary>
    public void Write(Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // Write metadata root header
        WriteMetadataRoot(writer);

        // Track positions for stream headers (we'll patch these later)
        var streamHeadersPosition = output.Position;

        // Write placeholder stream headers and track their positions
        var streamHeaders = _source.PEFile.StreamHeaders;
        var headerPositions = new long[streamHeaders.Length];
        for (int i = 0; i < streamHeaders.Length; i++)
        {
            headerPositions[i] = output.Position;
            writer.Write((uint)0); // Offset placeholder
            writer.Write((uint)0); // Size placeholder
            WriteAlignedString(writer, streamHeaders[i].Name);
        }

        // Write each stream and patch headers immediately
        // Stream offsets are relative to the start of metadata (position 0)
        for (int i = 0; i < streamHeaders.Length; i++)
        {
            var header = streamHeaders[i];
            var streamOffset = (uint)output.Position; // Offset from start of metadata
            uint streamSize;

            switch (header.Name)
            {
                case "#~":
                case "#-":
                    streamSize = WriteTableHeap(writer);
                    break;

                case "#Strings":
                    streamSize = WriteStringHeap(writer);
                    break;

                case "#Blob":
                    streamSize = WriteBlobHeap(writer);
                    break;

                case "#GUID":
                    streamSize = WriteGuidHeap(writer);
                    break;

                case "#US":
                    // User strings - just copy
                    var usHeader = _source.PEFile.GetStreamHeader("#US");
                    if (usHeader is {Size: > 0})
                    {
                        var usData = _source.PEFile.ReadBytesAt(
                            _source.PEFile.MetadataFileOffset + usHeader.Offset,
                            (int)usHeader.Size);
                        writer.Write(usData);
                        AlignTo4(writer);
                        streamSize = (uint)(output.Position - streamOffset);
                    }
                    else
                    {
                        // Empty #US heap - write minimal
                        writer.Write((byte)0);
                        AlignTo4(writer);
                        streamSize = (uint)(output.Position - streamOffset);
                    }
                    break;

                default:
                    // Unknown stream - copy from source
                    var srcHeader = _source.PEFile.GetStreamHeader(header.Name);
                    if (srcHeader is {Size: > 0})
                    {
                        var data = _source.PEFile.ReadBytesAt(
                            _source.PEFile.MetadataFileOffset + srcHeader.Offset,
                            (int)srcHeader.Size);
                        writer.Write(data);
                        AlignTo4(writer);
                    }
                    streamSize = (uint)(output.Position - streamOffset);
                    break;
            }

            // Patch this stream's header
            var endPos = output.Position;
            output.Position = headerPositions[i];
            writer.Write(streamOffset);
            writer.Write(streamSize);
            output.Position = endPos;
        }
    }

    private void WriteMetadataRoot(BinaryWriter writer)
    {
        // BSJB signature
        writer.Write(0x424a5342u);

        // Major/Minor version
        writer.Write((ushort)1);
        writer.Write((ushort)1);

        // Reserved
        writer.Write(0u);

        // Version string (aligned to 4 bytes)
        var versionBytes = Encoding.UTF8.GetBytes(_source.PEFile.MetadataVersionString);
        var alignedLen = (versionBytes.Length + 3) & ~3;
        writer.Write(alignedLen);
        writer.Write(versionBytes);
        for (int i = versionBytes.Length; i < alignedLen; i++)
            writer.Write((byte)0);

        // Flags
        writer.Write((ushort)0);

        // Stream count
        writer.Write((ushort)_source.PEFile.StreamHeaders.Length);
    }

    private uint WriteTableHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Write header
        writer.Write(0u); // Reserved
        writer.Write((byte)2); // MajorVersion
        writer.Write((byte)0); // MinorVersion

        // HeapSizes
        byte heapSizes = 0;
        if (_source.StringIndexSize == 4) heapSizes |= 0x01;
        if (_source.GuidIndexSize == 4) heapSizes |= 0x02;
        if (_source.BlobIndexSize == 4) heapSizes |= 0x04;
        writer.Write(heapSizes);

        writer.Write((byte)1); // Reserved

        // Valid and Sorted bitmasks
        var valid = _source.Valid;
        var sorted = _source.Sorted;

        // Update Valid if we're adding rows to new tables
        if (_plan.NewTypeRefs.Count > 0 && _source.GetRowCount(Table.TypeRef) == 0)
            valid |= 1L << (int)Table.TypeRef;
        if (_plan.NewMemberRefs.Count > 0 && _source.GetRowCount(Table.MemberRef) == 0)
            valid |= 1L << (int)Table.MemberRef;
        if (_plan.NewCustomAttributes.Count > 0 && _source.GetRowCount(Table.CustomAttribute) == 0)
            valid |= 1L << (int)Table.CustomAttribute;

        writer.Write(valid);
        writer.Write(sorted);

        // Row counts
        for (int i = 0; i < 58; i++)
        {
            if ((valid & (1L << i)) == 0) continue;

            var table = (Table)i;
            var count = _source.GetRowCount(table);

            // Add new rows
            if (table == Table.TypeRef)
                count += _plan.NewTypeRefs.Count;
            else if (table == Table.MemberRef)
                count += _plan.NewMemberRefs.Count;
            else if (table == Table.CustomAttribute)
                count += _plan.NewCustomAttributes.Count;

            writer.Write((uint)count);
        }

        // Write table data
        for (int i = 0; i < 58; i++)
        {
            if ((valid & (1L << i)) == 0) continue;

            var table = (Table)i;
            WriteTableData(writer, table);
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    private void WriteTableData(BinaryWriter writer, Table table)
    {
        var rowCount = _source.GetRowCount(table);
        var rowSize = _source.GetRowSize(table);

        switch (table)
        {
            case Table.Assembly:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = _plan.GetAssemblyRow(rid);
                    row.Write(writer, _source.BlobIndexSize, _source.StringIndexSize);
                }
                break;

            case Table.AssemblyRef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = _plan.GetAssemblyRefRow(rid);
                    row.Write(writer, _source.BlobIndexSize, _source.StringIndexSize);
                }
                break;

            case Table.TypeDef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = _plan.GetTypeDefRow(rid);
                    row.Write(writer, _source.StringIndexSize,
                        _source.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                        _source.GetTableIndexSize(Table.Field),
                        _source.GetTableIndexSize(Table.Method));
                }
                break;

            case Table.TypeRef:
                // Existing rows
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = _source.ReadTypeRefRow(rid);
                    row.Write(writer,
                        _source.GetCodedIndexSize(CodedIndex.ResolutionScope),
                        _source.StringIndexSize);
                }
                // New rows
                foreach (var row in _plan.NewTypeRefs)
                {
                    row.Write(writer,
                        _source.GetCodedIndexSize(CodedIndex.ResolutionScope),
                        _source.StringIndexSize);
                }
                break;

            case Table.MemberRef:
                // Existing rows
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = _source.ReadMemberRefRow(rid);
                    row.Write(writer,
                        _source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        _source.StringIndexSize,
                        _source.BlobIndexSize);
                }
                // New rows
                foreach (var row in _plan.NewMemberRefs)
                {
                    row.Write(writer,
                        _source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        _source.StringIndexSize,
                        _source.BlobIndexSize);
                }
                break;

            case Table.CustomAttribute:
                WriteCustomAttributeTable(writer, rowCount);
                break;

            default:
                // Copy unchanged table data
                _source.CopyTableData(table, writer.BaseStream);
                break;
        }
    }

    private void WriteCustomAttributeTable(BinaryWriter writer, int existingCount)
    {
        // CustomAttribute table must be sorted by Parent
        var allRows = new List<CustomAttributeRow>();

        // Read existing rows
        for (uint rid = 1; rid <= existingCount; rid++)
        {
            allRows.Add(_source.ReadCustomAttributeRow(rid));
        }

        // Add new rows
        allRows.AddRange(_plan.NewCustomAttributes);

        // Sort by Parent
        allRows.Sort((a, b) => a.ParentIndex.CompareTo(b.ParentIndex));

        // Write sorted rows
        foreach (var row in allRows)
        {
            row.Write(writer,
                _source.GetCodedIndexSize(CodedIndex.HasCustomAttribute),
                _source.GetCodedIndexSize(CodedIndex.CustomAttributeType),
                _source.BlobIndexSize);
        }
    }

    private uint WriteStringHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Copy original string heap
        _source.CopyStringHeap(writer.BaseStream);

        // Append new strings
        foreach (var kvp in _plan.NewStrings)
        {
            var bytes = Encoding.UTF8.GetBytes(kvp.Key);
            writer.Write(bytes);
            writer.Write((byte)0); // null terminator
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    private uint WriteBlobHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Copy original blob heap
        _source.CopyBlobHeap(writer.BaseStream);

        // Append new blobs
        foreach (var (data, _) in _plan.NewBlobs)
        {
            WriteCompressedLength(writer, data.Length);
            writer.Write(data);
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    private uint WriteGuidHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Just copy the original GUID heap (we don't add new GUIDs)
        _source.CopyGuidHeap(writer.BaseStream);

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    private static void WriteCompressedLength(BinaryWriter writer, int length)
    {
        if (length < 0x80)
        {
            writer.Write((byte)length);
        }
        else if (length < 0x4000)
        {
            writer.Write((byte)(0x80 | (length >> 8)));
            writer.Write((byte)(length & 0xFF));
        }
        else
        {
            writer.Write((byte)(0xC0 | (length >> 24)));
            writer.Write((byte)((length >> 16) & 0xFF));
            writer.Write((byte)((length >> 8) & 0xFF));
            writer.Write((byte)(length & 0xFF));
        }
    }

    private static void WriteAlignedString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(bytes);
        writer.Write((byte)0);

        // Align to 4 bytes
        var totalLen = bytes.Length + 1;
        var aligned = (totalLen + 3) & ~3;
        for (int i = totalLen; i < aligned; i++)
            writer.Write((byte)0);
    }

    private static void AlignTo4(BinaryWriter writer)
    {
        var pos = writer.BaseStream.Position;
        var aligned = (pos + 3) & ~3;
        while (writer.BaseStream.Position < aligned)
            writer.Write((byte)0);
    }
}
