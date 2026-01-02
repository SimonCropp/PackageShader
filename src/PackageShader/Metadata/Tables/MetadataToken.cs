/// <summary>
/// A metadata token combining table type and row id.
/// </summary>
readonly struct MetadataToken(TableIndex table, uint rid)
{
    public readonly uint Value = ((uint)table << 24) | rid;

    public uint RID => Value & 0x00ffffff;
    public TableIndex TableIndex => (TableIndex)(Value >> 24);

    public override string ToString() => $"0x{Value:X8}";
}