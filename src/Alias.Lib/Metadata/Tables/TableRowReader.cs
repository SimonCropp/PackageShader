namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// Helper for reading values from table rows.
/// </summary>
public class TableRowReader(byte[] data)
{
    int position = 0;

    public ushort ReadUInt16()
    {
        var value = BitConverter.ToUInt16(data, position);
        position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        var value = BitConverter.ToUInt32(data, position);
        position += 4;
        return value;
    }

    public uint ReadIndex(int size)
    {
        if (size == 2)
            return ReadUInt16();
        return ReadUInt32();
    }

    public void Skip(int bytes) =>
        position += bytes;
}