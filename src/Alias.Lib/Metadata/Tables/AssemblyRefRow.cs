namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// AssemblyRef table row (0x23).
/// </summary>
public struct AssemblyRefRow
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

    public static AssemblyRefRow Read(byte[] data, int blobIndexSize, int stringIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            MajorVersion = reader.ReadUInt16(),
            MinorVersion = reader.ReadUInt16(),
            BuildNumber = reader.ReadUInt16(),
            RevisionNumber = reader.ReadUInt16(),
            Flags = reader.ReadUInt32(),
            PublicKeyOrTokenIndex = reader.ReadIndex(blobIndexSize),
            NameIndex = reader.ReadIndex(stringIndexSize),
            CultureIndex = reader.ReadIndex(stringIndexSize),
            HashValueIndex = reader.ReadIndex(blobIndexSize)
        };
    }

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