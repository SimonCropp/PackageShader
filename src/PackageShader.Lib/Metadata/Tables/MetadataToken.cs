/// <summary>
/// A metadata token combining table type and row id.
/// </summary>
readonly struct MetadataToken
{
    public readonly uint Value;

    public MetadataToken(uint value) => Value = value;

    public MetadataToken(TableIndex table, uint rid) => Value = ((uint)table << 24) | rid;

    public uint RID => Value & 0x00ffffff;
    public TableIndex TableIndex => (TableIndex)(Value >> 24);

    public static MetadataToken Zero => new(0);

    public override string ToString() => $"0x{Value:X8}";
}