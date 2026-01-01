namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// Assembly table row (0x20).
/// </summary>
public struct AssemblyRow
{
    public uint HashAlgId;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ushort BuildNumber;
    public ushort RevisionNumber;
    public uint Flags;
    public uint PublicKeyIndex;   // Blob heap index
    public uint NameIndex;        // String heap index
    public uint CultureIndex;     // String heap index

    public static AssemblyRow Read(byte[] data, int blobIndexSize, int stringIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            HashAlgId = reader.ReadUInt32(),
            MajorVersion = reader.ReadUInt16(),
            MinorVersion = reader.ReadUInt16(),
            BuildNumber = reader.ReadUInt16(),
            RevisionNumber = reader.ReadUInt16(),
            Flags = reader.ReadUInt32(),
            PublicKeyIndex = reader.ReadIndex(blobIndexSize),
            NameIndex = reader.ReadIndex(stringIndexSize),
            CultureIndex = reader.ReadIndex(stringIndexSize)
        };
    }

    public readonly void Write(BinaryWriter writer, int blobIndexSize, int stringIndexSize)
    {
        writer.Write(HashAlgId);
        writer.Write(MajorVersion);
        writer.Write(MinorVersion);
        writer.Write(BuildNumber);
        writer.Write(RevisionNumber);
        writer.Write(Flags);
        WriteIndex(writer, PublicKeyIndex, blobIndexSize);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, CultureIndex, stringIndexSize);
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