/// <summary>
/// CustomAttribute table row (0x0C).
/// </summary>
struct CustomAttributeRow
{
    public uint ParentIndex;    // HasCustomAttribute coded index
    public uint TypeIndex;      // CustomAttributeType coded index
    public uint ValueIndex;     // Blob heap index

    public static CustomAttributeRow Read(byte[] data, int hasCustomAttributeSize,
        int customAttributeTypeSize, int blobIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            ParentIndex = reader.ReadIndex(hasCustomAttributeSize),
            TypeIndex = reader.ReadIndex(customAttributeTypeSize),
            ValueIndex = reader.ReadIndex(blobIndexSize)
        };
    }

    public readonly void Write(BinaryWriter writer, int hasCustomAttributeSize,
        int customAttributeTypeSize, int blobIndexSize)
    {
        WriteIndex(writer, ParentIndex, hasCustomAttributeSize);
        WriteIndex(writer, TypeIndex, customAttributeTypeSize);
        WriteIndex(writer, ValueIndex, blobIndexSize);
    }

    static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
        {
            writer.Write((ushort)value);
        }
        else
        {
            writer.Write(value);
        }
    }
}