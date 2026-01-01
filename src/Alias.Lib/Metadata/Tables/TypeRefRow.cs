namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// TypeRef table row (0x01).
/// </summary>
public struct TypeRefRow
{
    public uint ResolutionScopeIndex;  // ResolutionScope coded index
    public uint NameIndex;             // String heap index
    public uint NamespaceIndex;        // String heap index

    public static TypeRefRow Read(byte[] data, int resolutionScopeSize, int stringIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            ResolutionScopeIndex = reader.ReadIndex(resolutionScopeSize),
            NameIndex = reader.ReadIndex(stringIndexSize),
            NamespaceIndex = reader.ReadIndex(stringIndexSize)
        };
    }

    public readonly void Write(BinaryWriter writer, int resolutionScopeSize, int stringIndexSize)
    {
        WriteIndex(writer, ResolutionScopeIndex, resolutionScopeSize);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, NamespaceIndex, stringIndexSize);
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