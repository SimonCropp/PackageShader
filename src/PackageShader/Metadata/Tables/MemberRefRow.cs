/// <summary>
/// MemberRef table row (0x0A).
/// </summary>
struct MemberRefRow
{
    // MemberRefParent coded index
    public uint ClassIndex;

    // String heap index
    public uint NameIndex;

    // Blob heap index
    public uint SignatureIndex;

    public static MemberRefRow Read(
        byte[] data,
        int memberRefParentSize,
        int stringIndexSize,
        int blobIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            ClassIndex = reader.ReadIndex(memberRefParentSize),
            NameIndex = reader.ReadIndex(stringIndexSize),
            SignatureIndex = reader.ReadIndex(blobIndexSize)
        };
    }

    public readonly void Write(
        BinaryWriter writer,
        int memberRefParentSize,
        int stringIndexSize,
        int blobIndexSize)
    {
        WriteIndex(writer, ClassIndex, memberRefParentSize);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, SignatureIndex, blobIndexSize);
    }

    static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
        {
            writer.Write((ushort) value);
        }
        else
        {
            writer.Write(value);
        }
    }
}