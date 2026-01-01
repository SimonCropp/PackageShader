using System.Text;
using Alias.Lib.Metadata.Tables;
using Alias.Lib.PE;

namespace Alias.Lib.Metadata;

/// <summary>
/// Reads metadata on-demand without loading entire heaps into memory.
/// Uses lazy access with small LRU caching for frequently accessed data.
/// </summary>
public sealed class StreamingMetadataReader : IDisposable
{
    private readonly StreamingPEFile _peFile;
    private readonly long _metadataBaseOffset;
    private bool _disposed;

    // Heap locations (file offsets)
    private long _stringHeapOffset;
    private uint _stringHeapSize;
    private long _blobHeapOffset;
    private uint _blobHeapSize;
    private long _guidHeapOffset;
    private uint _guidHeapSize;
    private long _tableHeapOffset;
    private uint _tableHeapSize;

    // Table heap info (parsed on construction - small)
    private readonly TableInfo[] _tables = new TableInfo[58];
    private readonly int[] _codedIndexSizes = new int[14];
    private long _tableDataOffset;

    // Index sizes
    public int StringIndexSize { get; private set; } = 2;
    public int GuidIndexSize { get; private set; } = 2;
    public int BlobIndexSize { get; private set; } = 2;

    // Table heap info
    public long Valid { get; private set; }
    public long Sorted { get; private set; }

    // Small LRU caches
    private readonly Dictionary<uint, string> _stringCache = new();
    private readonly Dictionary<uint, byte[]> _blobCache = new();
    private const int MaxCacheSize = 128;

    public StreamingMetadataReader(StreamingPEFile peFile)
    {
        _peFile = peFile;
        _metadataBaseOffset = peFile.MetadataFileOffset;

        // Parse stream headers to find heap locations
        ParseStreamLocations();

        // Parse table heap header (required to know row sizes)
        ParseTableHeapHeader();
    }

    private void ParseStreamLocations()
    {
        foreach (var header in _peFile.StreamHeaders)
        {
            var offset = _metadataBaseOffset + header.Offset;
            switch (header.Name)
            {
                case "#Strings":
                    _stringHeapOffset = offset;
                    _stringHeapSize = header.Size;
                    break;
                case "#Blob":
                    _blobHeapOffset = offset;
                    _blobHeapSize = header.Size;
                    break;
                case "#GUID":
                    _guidHeapOffset = offset;
                    _guidHeapSize = header.Size;
                    break;
                case "#~":
                case "#-":
                    _tableHeapOffset = offset;
                    _tableHeapSize = header.Size;
                    break;
            }
        }
    }

    private void ParseTableHeapHeader()
    {
        if (_tableHeapSize < 24)
            throw new BadImageFormatException("Table heap too small");

        var header = _peFile.ReadBytesAt(_tableHeapOffset, 24);
        int position = 6; // Skip reserved (4), MajorVersion (1), MinorVersion (1)

        // HeapSizes
        var heapSizes = header[position++];
        StringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        GuidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
        BlobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;

        position++; // Skip reserved

        // Valid bitmask (8 bytes)
        Valid = BitConverter.ToInt64(header, position);
        position += 8;

        // Sorted bitmask (8 bytes)
        Sorted = BitConverter.ToInt64(header, position);

        // Count present tables to read row counts
        int presentTableCount = 0;
        for (int i = 0; i < 58; i++)
        {
            if ((Valid & (1L << i)) != 0)
                presentTableCount++;
        }

        // Read row counts
        var rowCountsData = _peFile.ReadBytesAt(_tableHeapOffset + 24, presentTableCount * 4);
        int rowCountPos = 0;
        for (int i = 0; i < 58; i++)
        {
            if ((Valid & (1L << i)) == 0)
                continue;
            _tables[i].RowCount = BitConverter.ToUInt32(rowCountsData, rowCountPos);
            rowCountPos += 4;
        }

        // Compute table row sizes and offsets
        _tableDataOffset = _tableHeapOffset + 24 + presentTableCount * 4;
        ComputeTableInfo();
    }

    private void ComputeTableInfo()
    {
        uint offset = 0;

        for (int i = 0; i < 58; i++)
        {
            var table = (Table)i;
            if (!HasTable(table))
                continue;

            int rowSize = ComputeRowSize(table);
            _tables[i].RowSize = (uint)rowSize;
            _tables[i].Offset = offset;
            offset += (uint)(rowSize * _tables[i].RowCount);
        }
    }

    private int ComputeRowSize(Table table) =>
        table switch
        {
            Table.Module => 2 + StringIndexSize + GuidIndexSize * 3,
            Table.TypeRef => GetCodedIndexSize(CodedIndex.ResolutionScope) + StringIndexSize * 2,
            Table.TypeDef => 4 + StringIndexSize * 2 + GetCodedIndexSize(CodedIndex.TypeDefOrRef)
                             + GetTableIndexSize(Table.Field) + GetTableIndexSize(Table.Method),
            Table.FieldPtr => GetTableIndexSize(Table.Field),
            Table.Field => 2 + StringIndexSize + BlobIndexSize,
            Table.MethodPtr => GetTableIndexSize(Table.Method),
            Table.Method => 8 + StringIndexSize + BlobIndexSize + GetTableIndexSize(Table.Param),
            Table.ParamPtr => GetTableIndexSize(Table.Param),
            Table.Param => 4 + StringIndexSize,
            Table.InterfaceImpl => GetTableIndexSize(Table.TypeDef) + GetCodedIndexSize(CodedIndex.TypeDefOrRef),
            Table.MemberRef => GetCodedIndexSize(CodedIndex.MemberRefParent) + StringIndexSize + BlobIndexSize,
            Table.Constant => 2 + GetCodedIndexSize(CodedIndex.HasConstant) + BlobIndexSize,
            Table.CustomAttribute => GetCodedIndexSize(CodedIndex.HasCustomAttribute)
                                     + GetCodedIndexSize(CodedIndex.CustomAttributeType) + BlobIndexSize,
            Table.FieldMarshal => GetCodedIndexSize(CodedIndex.HasFieldMarshal) + BlobIndexSize,
            Table.DeclSecurity => 2 + GetCodedIndexSize(CodedIndex.HasDeclSecurity) + BlobIndexSize,
            Table.ClassLayout => 6 + GetTableIndexSize(Table.TypeDef),
            Table.FieldLayout => 4 + GetTableIndexSize(Table.Field),
            Table.StandAloneSig => BlobIndexSize,
            Table.EventMap => GetTableIndexSize(Table.TypeDef) + GetTableIndexSize(Table.Event),
            Table.EventPtr => GetTableIndexSize(Table.Event),
            Table.Event => 2 + StringIndexSize + GetCodedIndexSize(CodedIndex.TypeDefOrRef),
            Table.PropertyMap => GetTableIndexSize(Table.TypeDef) + GetTableIndexSize(Table.Property),
            Table.PropertyPtr => GetTableIndexSize(Table.Property),
            Table.Property => 2 + StringIndexSize + BlobIndexSize,
            Table.MethodSemantics => 2 + GetTableIndexSize(Table.Method) + GetCodedIndexSize(CodedIndex.HasSemantics),
            Table.MethodImpl => GetTableIndexSize(Table.TypeDef)
                                + GetCodedIndexSize(CodedIndex.MethodDefOrRef) + GetCodedIndexSize(CodedIndex.MethodDefOrRef),
            Table.ModuleRef => StringIndexSize,
            Table.TypeSpec => BlobIndexSize,
            Table.ImplMap => 2 + GetCodedIndexSize(CodedIndex.MemberForwarded)
                               + StringIndexSize + GetTableIndexSize(Table.ModuleRef),
            Table.FieldRVA => 4 + GetTableIndexSize(Table.Field),
            Table.EncLog => 8,
            Table.EncMap => 4,
            Table.Assembly => 16 + BlobIndexSize + StringIndexSize * 2,
            Table.AssemblyProcessor => 4,
            Table.AssemblyOS => 12,
            Table.AssemblyRef => 12 + BlobIndexSize * 2 + StringIndexSize * 2,
            Table.AssemblyRefProcessor => 4 + GetTableIndexSize(Table.AssemblyRef),
            Table.AssemblyRefOS => 12 + GetTableIndexSize(Table.AssemblyRef),
            Table.File => 4 + StringIndexSize + BlobIndexSize,
            Table.ExportedType => 8 + StringIndexSize * 2 + GetCodedIndexSize(CodedIndex.Implementation),
            Table.ManifestResource => 8 + StringIndexSize + GetCodedIndexSize(CodedIndex.Implementation),
            Table.NestedClass => GetTableIndexSize(Table.TypeDef) * 2,
            Table.GenericParam => 4 + GetCodedIndexSize(CodedIndex.TypeOrMethodDef) + StringIndexSize,
            Table.MethodSpec => GetCodedIndexSize(CodedIndex.MethodDefOrRef) + BlobIndexSize,
            Table.GenericParamConstraint => GetTableIndexSize(Table.GenericParam) + GetCodedIndexSize(CodedIndex.TypeDefOrRef),
            Table.Document => BlobIndexSize + GuidIndexSize + BlobIndexSize + GuidIndexSize,
            Table.MethodDebugInformation => GetTableIndexSize(Table.Document) + BlobIndexSize,
            Table.LocalScope => GetTableIndexSize(Table.Method) + GetTableIndexSize(Table.ImportScope)
                                + GetTableIndexSize(Table.LocalVariable) + GetTableIndexSize(Table.LocalConstant) + 8,
            Table.LocalVariable => 4 + StringIndexSize,
            Table.LocalConstant => StringIndexSize + BlobIndexSize,
            Table.ImportScope => GetTableIndexSize(Table.ImportScope) + BlobIndexSize,
            Table.StateMachineMethod => GetTableIndexSize(Table.Method) * 2,
            Table.CustomDebugInformation => GetCodedIndexSize(CodedIndex.HasCustomDebugInformation)
                                            + GuidIndexSize + BlobIndexSize,
            _ => throw new NotSupportedException($"Unknown table: {table}")
        };

    #region Table Access

    public bool HasTable(Table table) =>
        (Valid & (1L << (int)table)) != 0;

    public int GetRowCount(Table table) =>
        (int)_tables[(int)table].RowCount;

    public int GetTableIndexSize(Table table) =>
        GetRowCount(table) < 65536 ? 2 : 4;

    public int GetCodedIndexSize(CodedIndex codedIndex)
    {
        var index = (int)codedIndex;
        if (_codedIndexSizes[index] != 0)
            return _codedIndexSizes[index];

        return _codedIndexSizes[index] = CodedIndexHelper.GetSize(codedIndex, t => GetRowCount(t));
    }

    /// <summary>
    /// Reads raw bytes for a specific table row.
    /// </summary>
    public byte[] ReadRow(Table table, uint rid)
    {
        if (rid == 0 || rid > _tables[(int)table].RowCount)
            return Array.Empty<byte>();

        var info = _tables[(int)table];
        var offset = _tableDataOffset + info.Offset + (rid - 1) * info.RowSize;
        return _peFile.ReadBytesAt(offset, (int)info.RowSize);
    }

    /// <summary>
    /// Gets the file offset for a specific table row.
    /// </summary>
    public long GetRowOffset(Table table, uint rid)
    {
        var info = _tables[(int)table];
        return _tableDataOffset + info.Offset + (rid - 1) * info.RowSize;
    }

    /// <summary>
    /// Gets the row size for a table.
    /// </summary>
    public int GetRowSize(Table table) => (int)_tables[(int)table].RowSize;

    #endregion

    #region String Heap Access

    /// <summary>
    /// Reads a string at the given index.
    /// </summary>
    public string ReadString(uint index)
    {
        if (index == 0)
            return string.Empty;

        if (_stringCache.TryGetValue(index, out var cached))
            return cached;

        if (index >= _stringHeapSize)
            return string.Empty;

        // Read the string from file
        var offset = _stringHeapOffset + index;

        // Read in chunks to find null terminator
        var buffer = new byte[256];
        var sb = new StringBuilder();
        long currentOffset = offset;

        while (currentOffset < _stringHeapOffset + _stringHeapSize)
        {
            var toRead = (int)Math.Min(buffer.Length, _stringHeapOffset + _stringHeapSize - currentOffset);
            var read = _peFile.ReadAt(currentOffset, buffer, 0, toRead);
            if (read == 0) break;

            // Find null terminator
            var nullIndex = Array.IndexOf(buffer, (byte)0, 0, read);
            if (nullIndex >= 0)
            {
                sb.Append(Encoding.UTF8.GetString(buffer, 0, nullIndex));
                break;
            }
            else
            {
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                currentOffset += read;
            }
        }

        var str = sb.ToString();

        // Add to cache (with eviction if too large)
        if (_stringCache.Count >= MaxCacheSize)
            _stringCache.Clear();
        _stringCache[index] = str;

        return str;
    }

    /// <summary>
    /// Gets the string heap size.
    /// </summary>
    public uint StringHeapSize => _stringHeapSize;

    #endregion

    #region Blob Heap Access

    /// <summary>
    /// Gets the blob heap size.
    /// </summary>
    public uint BlobHeapSize => _blobHeapSize;

    #endregion

    #region High-Level API

    /// <summary>
    /// Reads an assembly row.
    /// </summary>
    public AssemblyRow ReadAssemblyRow(uint rid)
    {
        if (!HasTable(Table.Assembly) || rid == 0 || rid > GetRowCount(Table.Assembly))
            throw new InvalidOperationException($"No Assembly row found at rid {rid}. HasTable={HasTable(Table.Assembly)}, RowCount={GetRowCount(Table.Assembly)}");

        var data = ReadRow(Table.Assembly, rid);
        return AssemblyRow.Read(data, BlobIndexSize, StringIndexSize);
    }

    /// <summary>
    /// Reads an assembly reference row.
    /// </summary>
    public AssemblyRefRow ReadAssemblyRefRow(uint rid)
    {
        var data = ReadRow(Table.AssemblyRef, rid);
        return AssemblyRefRow.Read(data, BlobIndexSize, StringIndexSize);
    }

    /// <summary>
    /// Reads a type definition row.
    /// </summary>
    public TypeDefRow ReadTypeDefRow(uint rid)
    {
        var data = ReadRow(Table.TypeDef, rid);
        return TypeDefRow.Read(data,
            StringIndexSize,
            GetCodedIndexSize(CodedIndex.TypeDefOrRef),
            GetTableIndexSize(Table.Field),
            GetTableIndexSize(Table.Method));
    }

    /// <summary>
    /// Reads a type reference row.
    /// </summary>
    public TypeRefRow ReadTypeRefRow(uint rid)
    {
        var data = ReadRow(Table.TypeRef, rid);
        return TypeRefRow.Read(data,
            GetCodedIndexSize(CodedIndex.ResolutionScope),
            StringIndexSize);
    }

    /// <summary>
    /// Reads a custom attribute row.
    /// </summary>
    public CustomAttributeRow ReadCustomAttributeRow(uint rid)
    {
        var data = ReadRow(Table.CustomAttribute, rid);
        return CustomAttributeRow.Read(data,
            GetCodedIndexSize(CodedIndex.HasCustomAttribute),
            GetCodedIndexSize(CodedIndex.CustomAttributeType),
            BlobIndexSize);
    }

    /// <summary>
    /// Reads a member reference row.
    /// </summary>
    public MemberRefRow ReadMemberRefRow(uint rid)
    {
        var data = ReadRow(Table.MemberRef, rid);
        return MemberRefRow.Read(data,
            GetCodedIndexSize(CodedIndex.MemberRefParent),
            StringIndexSize,
            BlobIndexSize);
    }

    /// <summary>
    /// Finds an assembly reference by name.
    /// </summary>
    public (uint rid, AssemblyRefRow row)? FindAssemblyRef(string name)
    {
        var count = GetRowCount(Table.AssemblyRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadAssemblyRefRow(i);
            var refName = ReadString(row.NameIndex);
            if (string.Equals(refName, name, StringComparison.OrdinalIgnoreCase))
                return (i, row);
        }
        return null;
    }

    /// <summary>
    /// Finds a type reference by name and namespace.
    /// </summary>
    public uint? FindTypeRef(string name, string @namespace)
    {
        var count = GetRowCount(Table.TypeRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadTypeRefRow(i);
            var typeName = ReadString(row.NameIndex);
            var typeNamespace = ReadString(row.NamespaceIndex);
            if (string.Equals(typeName, name, StringComparison.Ordinal) &&
                string.Equals(typeNamespace, @namespace, StringComparison.Ordinal))
                return i;
        }
        return null;
    }

    /// <summary>
    /// Finds a member reference by parent TypeRef RID and name.
    /// </summary>
    public uint? FindMemberRef(uint typeRefRid, string name)
    {
        // Encode the TypeRef token for comparison
        var parentToken = new MetadataToken(TokenType.TypeRef, typeRefRid);
        var expectedParent = CodedIndexHelper.EncodeToken(CodedIndex.MemberRefParent, parentToken);

        var count = GetRowCount(Table.MemberRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadMemberRefRow(i);
            if (row.ClassIndex == expectedParent)
            {
                var memberName = ReadString(row.NameIndex);
                if (string.Equals(memberName, name, StringComparison.Ordinal))
                    return i;
            }
        }
        return null;
    }

    #endregion

    #region Streaming Helpers

    /// <summary>
    /// Copies the string heap to an output stream.
    /// </summary>
    public void CopyStringHeap(Stream destination) =>
        _peFile.CopyRegion(_stringHeapOffset, _stringHeapSize, destination);

    /// <summary>
    /// Copies the blob heap to an output stream.
    /// </summary>
    public void CopyBlobHeap(Stream destination) =>
        _peFile.CopyRegion(_blobHeapOffset, _blobHeapSize, destination);

    /// <summary>
    /// Copies the GUID heap to an output stream.
    /// </summary>
    public void CopyGuidHeap(Stream destination) =>
        _peFile.CopyRegion(_guidHeapOffset, _guidHeapSize, destination);

    /// <summary>
    /// Copies a table's data to an output stream.
    /// </summary>
    public void CopyTableData(Table table, Stream destination)
    {
        var info = _tables[(int)table];
        if (info.RowCount == 0) return;

        var size = info.RowSize * info.RowCount;
        var offset = _tableDataOffset + info.Offset;
        _peFile.CopyRegion(offset, size, destination);
    }

    /// <summary>
    /// Gets the underlying PE file.
    /// </summary>
    public StreamingPEFile PEFile => _peFile;

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Don't dispose _peFile - caller owns it
    }
}
