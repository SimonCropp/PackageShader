/// <summary>
/// Writes metadata section incrementally to a stream, applying modifications.
/// </summary>
sealed class StreamingMetadataWriter(StreamingMetadataReader source, ModificationPlan plan)
{
    // ECMA-335 II.24.2.6: Index sizes determined by final heap sizes after modifications
    // Index size can only grow, never shrink (use max of source and calculated size)
    readonly int stringIndexSize = Math.Max(source.StringIndexSize, plan.FinalStringHeapSize >= 0x10000 ? 4 : 2);
    readonly int blobIndexSize = Math.Max(source.BlobIndexSize, plan.FinalBlobHeapSize >= 0x10000 ? 4 : 2);
    readonly int guidIndexSize = source.GuidIndexSize; // GUID heap is not modified

    // CRITICAL: If index sizes changed, we CANNOT copy table data byte-for-byte.
    // All table rows must be rewritten with new index sizes.
    bool IndexSizesChanged =>
        source.StringIndexSize != stringIndexSize ||
        source.BlobIndexSize != blobIndexSize;

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
        // ECMA-335 II.24.2.1: Metadata signature is 0x424A5342 (ASCII "BSJB")
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

        // ECMA-335 II.24.2.6: HeapSizes bit 0x01 = large string (>=2^16), 0x02 = large GUID, 0x04 = large blob
        // Use final heap sizes after modifications to determine index sizes
        byte heapSizes = 0;
        if (stringIndexSize == 4)
        {
            heapSizes |= 0x01; // #String heap >= 2^16 bytes
        }

        if (guidIndexSize == 4)
        {
            heapSizes |= 0x02; // #GUID heap >= 2^16 entries (not bytes)
        }

        if (blobIndexSize == 4)
        {
            heapSizes |= 0x04; // #Blob heap >= 2^16 bytes
        }

        writer.Write(heapSizes);

        writer.Write((byte)1); // Reserved, always 1

        // ECMA-335 II.24.2.6: Valid bitmask indicates present tables (bit N = table N present)
        // ECMA-335 II.24.2.6: Sorted bitmask indicates which tables are sorted by their primary key
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
                    row.Write(writer, blobIndexSize, stringIndexSize);
                }
                break;

            case TableIndex.AssemblyRef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = plan.GetAssemblyRefRow(rid);
                    row.Write(writer, blobIndexSize, stringIndexSize);
                }
                break;

            case TableIndex.TypeDef:
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = plan.GetTypeDefRow(rid);
                    row.Write(writer, stringIndexSize,
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
                        stringIndexSize);
                }
                // New rows
                foreach (var row in plan.NewTypeRefs)
                {
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.ResolutionScope),
                        stringIndexSize);
                }
                break;

            case TableIndex.MemberRef:
                // Existing rows
                for (uint rid = 1; rid <= rowCount; rid++)
                {
                    var row = source.ReadMemberRefRow(rid);
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        stringIndexSize,
                        blobIndexSize);
                }
                // New rows
                foreach (var row in plan.NewMemberRefs)
                {
                    row.Write(writer,
                        source.GetCodedIndexSize(CodedIndex.MemberRefParent),
                        stringIndexSize,
                        blobIndexSize);
                }
                break;

            case TableIndex.CustomAttribute:
                WriteCustomAttributeTable(writer, rowCount);
                break;

            default:
                // ECMA-335 II.24.2.6: When heap index sizes change, table rows must be rewritten.
                // We cannot copy table data byte-for-byte because row sizes have changed.
                if (IndexSizesChanged)
                {
                    throw new NotSupportedException(
                        $"Table {table} needs rewriting due to heap index size changes " +
                        $"(String: {source.StringIndexSize} -> {stringIndexSize} bytes, " +
                        $"Blob: {source.BlobIndexSize} -> {blobIndexSize} bytes). " +
                        $"This table type is not yet implemented for index size changes.");
                }
                // Copy unchanged table data (only safe when index sizes haven't changed)
                source.CopyTableData(table, writer.BaseStream);
                break;
        }
    }

    void WriteCustomAttributeTable(BinaryWriter writer, int existingCount)
    {
        // ECMA-335 II.22.10, II.24.2.6: CustomAttribute table must be sorted by Parent
        var allRows = new List<CustomAttributeRow>();

        // Read existing rows
        for (uint rid = 1; rid <= existingCount; rid++)
        {
            allRows.Add(source.ReadCustomAttributeRow(rid));
        }

        // Add new rows
        allRows.AddRange(plan.NewCustomAttributes);

        // ECMA-335 II.24.2.6: Sort by Parent column (required for sorted tables)
        allRows.Sort((_, __) => _.ParentIndex.CompareTo(__.ParentIndex));

        // Write sorted rows
        foreach (var row in allRows)
        {
            row.Write(writer,
                source.GetCodedIndexSize(CodedIndex.HasCustomAttribute),
                source.GetCodedIndexSize(CodedIndex.CustomAttributeType),
                blobIndexSize);
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

        // ECMA-335 II.24.2.4: Blob heap starts with empty blob (0x00) and uses compressed length encoding
        // Copy original blob heap
        source.CopyBlobHeap(writer.BaseStream);

        // Append new blobs with compressed length prefix
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

        // ECMA-335 II.24.2.5: GUID heap is sequence of 128-bit GUIDs
        // GUID indices are 1-based (not byte offsets), so index N refers to GUID at byte offset (N-1)*16
        // Just copy the original GUID heap (we don't add new GUIDs)
        source.CopyGuidHeap(writer.BaseStream);

        AlignTo4(writer);

        return (uint)(writer.BaseStream.Position - startPos);
    }

    /// <summary>
    /// ECMA-335 II.24.2.4: Blob length compression encoding
    /// - 1 byte: 0bbbbbbb (length &lt; 128)
    /// - 2 bytes: 10bbbbbb xxxxxxxx (length &lt; 16384)
    /// - 4 bytes: 110bbbbb xxxxxxxx yyyyyyyy zzzzzzzz (length &gt;= 16384)
    /// </summary>
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

    /// <summary>
    /// ECMA-335 II.24.2.2: Stream sizes shall be multiples of 4 bytes.
    /// Pads the current position to the next 4-byte boundary.
    /// </summary>
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
