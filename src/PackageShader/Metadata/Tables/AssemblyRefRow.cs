/// <summary>
/// AssemblyRef table row (0x23).
/// </summary>
struct AssemblyRefRow
{
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ushort BuildNumber;
    public ushort RevisionNumber;
    public uint Flags;
    public uint PublicKeyOrTokenIndex;  // Blob heap index
    public uint NameIndex;              // String heap index
    public uint CultureIndex;           // String heap index
    public uint HashValueIndex;         // Blob heap index

    public readonly void Write(BinaryWriter writer, int blobIndexSize, int stringIndexSize)
    {
        writer.Write(MajorVersion);
        writer.Write(MinorVersion);
        writer.Write(BuildNumber);
        writer.Write(RevisionNumber);
        writer.Write(Flags);
        WriteIndex(writer, PublicKeyOrTokenIndex, blobIndexSize);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, CultureIndex, stringIndexSize);
        WriteIndex(writer, HashValueIndex, blobIndexSize);
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