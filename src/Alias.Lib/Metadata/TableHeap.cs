using Alias.Lib.Metadata.Tables;

namespace Alias.Lib.Metadata;

/// <summary>
/// Reads and manages the #~ (table) metadata heap.
/// </summary>
public sealed class TableHeap
{
    private readonly byte[] _data;
    private readonly TableInfo[] _tables = new TableInfo[58];
    private readonly int[] _codedIndexSizes = new int[14];

    public long Valid { get; private set; }
    public long Sorted { get; private set; }

    public int StringIndexSize { get; set; } = 2;
    public int GuidIndexSize { get; set; } = 2;
    public int BlobIndexSize { get; set; } = 2;

    /// <summary>
    /// Raw data of this heap.
    /// </summary>
    public byte[] Data => _data;

    /// <summary>
    /// Gets table information by table type.
    /// </summary>
    public TableInfo this[Table table] => _tables[(int)table];

    public TableHeap(byte[] data) =>
        _data = data;

    /// <summary>
    /// Checks if a table exists in this heap.
    /// </summary>
    public bool HasTable(Table table) =>
        (Valid & (1L << (int)table)) != 0;

    /// <summary>
    /// Gets the row count for a table.
    /// </summary>
    public int GetRowCount(Table table) =>
        (int)_tables[(int)table].RowCount;

    /// <summary>
    /// Gets the index size (2 or 4) for a table.
    /// </summary>
    public int GetTableIndexSize(Table table) =>
        GetRowCount(table) < 65536 ? 2 : 4;

    /// <summary>
    /// Gets the index size (2 or 4) for a coded index.
    /// </summary>
    public int GetCodedIndexSize(CodedIndex codedIndex)
    {
        var index = (int)codedIndex;
        if (_codedIndexSizes[index] != 0)
            return _codedIndexSizes[index];

        return _codedIndexSizes[index] = CodedIndexHelper.GetSize(codedIndex, t => GetRowCount(t));
    }

    /// <summary>
    /// Parses the table heap structure from its data.
    /// </summary>
    public void Parse()
    {
        if (_data.Length < 24)
            throw new BadImageFormatException("Table heap too small");

        int position = 0;

        // Skip reserved (4), MajorVersion (1), MinorVersion (1)
        position += 6;

        // HeapSizes
        var heapSizes = _data[position++];
        StringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        GuidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
        BlobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;

        // Skip reserved (1)
        position++;

        // Valid bitmask (8 bytes)
        Valid = BitConverter.ToInt64(_data, position);
        position += 8;

        // Sorted bitmask (8 bytes)
        Sorted = BitConverter.ToInt64(_data, position);
        position += 8;

        // Row counts for each present table
        for (int i = 0; i < 58; i++)
        {
            if ((Valid & (1L << i)) == 0)
                continue;

            _tables[i].RowCount = BitConverter.ToUInt32(_data, position);
            position += 4;
        }

        // Compute table row sizes and offsets
        ComputeTableInfo((uint)position);
    }

    private void ComputeTableInfo(uint startOffset)
    {
        uint offset = startOffset;

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
            // Portable PDB tables
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

    /// <summary>
    /// Reads raw bytes for a specific table row.
    /// </summary>
    public byte[] ReadRow(Table table, uint rid)
    {
        if (rid == 0 || rid > _tables[(int)table].RowCount)
            return [];

        var info = _tables[(int)table];
        var offset = info.Offset + (rid - 1) * info.RowSize;
        var row = new byte[info.RowSize];
        Array.Copy(_data, offset, row, 0, info.RowSize);
        return row;
    }

    /// <summary>
    /// Gets raw bytes for all rows of a table.
    /// </summary>
    public byte[] GetTableData(Table table)
    {
        var info = _tables[(int)table];
        if (info.RowCount == 0)
            return [];

        var size = (int)(info.RowSize * info.RowCount);
        var data = new byte[size];
        Array.Copy(_data, info.Offset, data, 0, size);
        return data;
    }
}

