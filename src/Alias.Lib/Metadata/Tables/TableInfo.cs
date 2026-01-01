namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// Information about a metadata table.
/// </summary>
public struct TableInfo
{
    public uint Offset;
    public uint RowCount;
    public uint RowSize;

    public bool IsLarge => RowCount > ushort.MaxValue;
}