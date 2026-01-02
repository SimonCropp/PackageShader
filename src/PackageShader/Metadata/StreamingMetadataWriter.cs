/// <summary>
/// Writes metadata section incrementally to a stream, applying modifications.
/// </summary>
sealed class StreamingMetadataWriter(StreamingMetadataReader source, ModificationPlan plan)
{
    /// <summary>
    /// Writes the complete metadata section to the output stream.
    /// </summary>
    public void Write(Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // Write metadata root header
        WriteMetadataRoot(writer);

        // Write placeholder stream headers and track their positions
        var streamHeaders = source.PEFile.StreamHeaders;
        var headerPositions = new long[streamHeaders.Length];
        for (var i = 0; i < streamHeaders.Length; i++)
        {
            headerPositions[i] = output.Position;
            writer.Write((uint)0); // Offset placeholder
            writer.Write((uint)0); // Size placeholder
            WriteAlignedString(writer, streamHeaders[i].Name);
        }

        // Write each stream and patch headers immediately
        // Stream offsets are relative to the start of metadata (position 0)
        for (var i = 0; i < streamHeaders.Length; i++)
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
                    var usHeader = source.PEFile.GetStreamHeader("#US");
                    if (usHeader is {Size: > 0})
                    {
                        var usData = source.PEFile.ReadBytesAt(
                            source.PEFile.MetadataFileOffset + usHeader.Offset,
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
                    var srcHeader = source.PEFile.GetStreamHeader(header.Name);
                    if (srcHeader is {Size: > 0})
                    {
                        var data = source.PEFile.ReadBytesAt(
                            source.PEFile.MetadataFileOffset + srcHeader.Offset,
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

    void WriteMetadataRoot(BinaryWriter writer)
    {
        // BSJB signature
        writer.Write(0x424a5342u);

        // Major/Minor version
        writer.Write((ushort)1);
        writer.Write((ushort)1);

        // Reserved
        writer.Write(0u);

        // Version string (aligned to 4 bytes)
        var versionBytes = Encoding.UTF8.GetBytes(source.PEFile.MetadataVersionString);
        var alignedLen = (versionBytes.Length + 3) & ~3;
        writer.Write(alignedLen);
        writer.Write(versionBytes);
        for (var i = versionBytes.Length; i < alignedLen; i++)
        {
            writer.Write((byte)0);
        }

        // Flags
        writer.Write((ushort)0);

        // Stream count
        writer.Write((ushort)source.PEFile.StreamHeaders.Length);
    }

    uint WriteTableHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Write header
        writer.Write(0u); // Reserved
        writer.Write((byte)2); // MajorVersion
        writer.Write((byte)0); // MinorVersion

        // HeapSizes
        byte heapSizes = 0;
        if (source.StringIndexSize == 4)
        {
            heapSizes |= 0x01;
        }

        if (source.GuidIndexSize == 4)
        {
            heapSizes |= 0x02;
        }

        if (source.BlobIndexSize == 4)
        {
            heapSizes |= 0x04;
        }

        writer.Write(heapSizes);

        writer.Write((byte)1); // Reserved

        // Valid and Sorted bitmasks
        var valid = source.Valid;
        var sorted = source.Sorted;

        // Update Valid if we're adding rows to new tables
        if (plan.NewTypeRefs.Count > 0 && source.GetRowCount(TableIndex.TypeRef) == 0)
        {
            valid |= 1L << (int)TableIndex.TypeRef;
        }

        if (plan.NewMemberRefs.Count > 0 && source.GetRowCount(TableIndex.MemberRef) == 0)
        {
            valid |= 1L << (int)TableIndex.MemberRef;
        }

        if (plan.NewCustomAttributes.Count > 0 && source.GetRowCount(TableIndex.CustomAttribute) == 0)
        {
            valid |= 1L << (int)TableIndex.CustomAttribute;
        }

        writer.Write(valid);
        writer.Write(sorted);

        // Row counts
        for (var i = 0; i < 58; i++)
        {
            if ((valid & (1L << i)) == 0) continue;

            var table = (TableIndex)i;
            var count = source.GetRowCount(table);

            // Add new rows
            if (table == TableIndex.TypeRef)
                count += plan.NewTypeRefs.Count;
            else if (table == TableIndex.MemberRef)
                count += plan.NewMemberRefs.Count;
            else if (table == TableIndex.CustomAttribute)
                count += plan.NewCustomAttributes.Count;

            writer.Write((uint)count);
        }

        // Write table data
        for (var i = 0; i < 58; i++)
        {
            if ((valid & (1L << i)) == 0) continue;

            var table = (TableIndex)i;
            WriteTableData(writer, table);
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    void WriteTableData(BinaryWriter writer, TableIndex table)
    {
        var rowCount = source.GetRowCount(table);

        switch (table)
        {
            case TableIndex.Assembly:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = plan.GetAssemblyRow(rid);
                    row.Write(writer, source.BlobIndexSize, source.StringIndexSize);
                }
                break;

            case TableIndex.AssemblyRef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = plan.GetAssemblyRefRow(rid);
                    row.Write(writer, source.BlobIndexSize, source.StringIndexSize);
                }
                break;

            case TableIndex.TypeDef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = plan.GetTypeDefRow(rid);
                    row.Write(writer, source.StringIndexSize,
                        source.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                        source.GetTableIndexSize(TableIndex.Field),
                        source.GetTableIndexSize(TableIndex.MethodDef));
                }
                break;

            case TableIndex.TypeRef:
                // Existing rows
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = source.ReadTypeRefRow(rid);
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.ResolutionScope),
                        source.StringIndexSize);
                }
                // New rows
                foreach (var row in plan.NewTypeRefs)
                {
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.ResolutionScope),
                        source.StringIndexSize);
                }
                break;

            case TableIndex.MemberRef:
                // Existing rows
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = source.ReadMemberRefRow(rid);
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        source.StringIndexSize,
                        source.BlobIndexSize);
                }
                // New rows
                foreach (var row in plan.NewMemberRefs)
                {
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        source.StringIndexSize,
                        source.BlobIndexSize);
                }
                break;

            case TableIndex.CustomAttribute:
                WriteCustomAttributeTable(writer, rowCount);
                break;

            default:
                // Copy unchanged table data
                source.CopyTableData(table, writer.BaseStream);
                break;
        }
    }

    void WriteCustomAttributeTable(BinaryWriter writer, int existingCount)
    {
        // CustomAttribute table must be sorted by Parent
        var allRows = new List<CustomAttributeRow>();

        // Read existing rows
        for (uint rid = 1; rid <= existingCount; rid++)
        {
            allRows.Add(source.ReadCustomAttributeRow(rid));
        }

        // Add new rows
        allRows.AddRange(plan.NewCustomAttributes);

        // Sort by Parent
        allRows.Sort((a, b) => a.ParentIndex.CompareTo(b.ParentIndex));

        // Write sorted rows
        foreach (var row in allRows)
        {
            row.Write(writer,
                source.GetCodedIndexSize(CodedIndex.HasCustomAttribute),
                source.GetCodedIndexSize(CodedIndex.CustomAttributeType),
                source.BlobIndexSize);
        }
    }

    uint WriteStringHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Copy original string heap
        source.CopyStringHeap(writer.BaseStream);

        // Append new strings using pooled buffer
        byte[]? rentedBuffer = null;
        try
        {
            foreach (var kvp in plan.NewStrings)
            {
                var str = kvp.Key;
                var byteCount = Encoding.UTF8.GetByteCount(str);
                if (rentedBuffer == null || rentedBuffer.Length < byteCount)
                {
                    if (rentedBuffer != null)
                        System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
                    rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(byteCount, 256));
                }
                Encoding.UTF8.GetBytes(str, 0, str.Length, rentedBuffer, 0);
                writer.Write(rentedBuffer, 0, byteCount);
                writer.Write((byte)0); // null terminator
            }
        }
        finally
        {
            if (rentedBuffer != null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    uint WriteBlobHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Copy original blob heap
        source.CopyBlobHeap(writer.BaseStream);

        // Append new blobs
        foreach (var (data, _) in plan.NewBlobs)
        {
            WriteCompressedLength(writer, data.Length);
            writer.Write(data);
        }

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    uint WriteGuidHeap(BinaryWriter writer)
    {
        var startPos = writer.BaseStream.Position;

        // Just copy the original GUID heap (we don't add new GUIDs)
        source.CopyGuidHeap(writer.BaseStream);

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    static void WriteCompressedLength(BinaryWriter writer, int length)
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

    static void WriteAlignedString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(bytes);
        writer.Write((byte)0);

        // Align to 4 bytes
        var totalLen = bytes.Length + 1;
        var aligned = (totalLen + 3) & ~3;
        for (var i = totalLen; i < aligned; i++)
        {
            writer.Write((byte)0);
        }
    }

    static void AlignTo4(BinaryWriter writer)
    {
        var pos = writer.BaseStream.Position;
        var aligned = (pos + 3) & ~3;
        while (writer.BaseStream.Position < aligned)
        {
            writer.Write((byte)0);
        }
    }
}
