using System.Reflection;

namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// TypeDef table row (0x02).
/// </summary>
public struct TypeDefRow
{
    public uint Flags;
    public uint NameIndex;        // String heap index
    public uint NamespaceIndex;   // String heap index
    public uint ExtendsIndex;     // TypeDefOrRef coded index
    public uint FieldListIndex;   // Field table index
    public uint MethodListIndex;  // Method table index

    public static TypeDefRow Read(byte[] data, int stringIndexSize, int typeDefOrRefSize,
        int fieldIndexSize, int methodIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            Flags = reader.ReadUInt32(),
            NameIndex = reader.ReadIndex(stringIndexSize),
            NamespaceIndex = reader.ReadIndex(stringIndexSize),
            ExtendsIndex = reader.ReadIndex(typeDefOrRefSize),
            FieldListIndex = reader.ReadIndex(fieldIndexSize),
            MethodListIndex = reader.ReadIndex(methodIndexSize)
        };
    }

    public readonly void Write(BinaryWriter writer, int stringIndexSize, int typeDefOrRefSize,
        int fieldIndexSize, int methodIndexSize)
    {
        writer.Write(Flags);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, NamespaceIndex, stringIndexSize);
        WriteIndex(writer, ExtendsIndex, typeDefOrRefSize);
        WriteIndex(writer, FieldListIndex, fieldIndexSize);
        WriteIndex(writer, MethodListIndex, methodIndexSize);
    }

    /// <summary>
    /// Makes this type internal (non-public).
    /// </summary>
    public void MakeInternal()
    {
        const uint visibilityMask = (uint) TypeAttributes.VisibilityMask;
        var visibility = (TypeAttributes) (Flags & visibilityMask);

        Flags = visibility switch
        {
            TypeAttributes.Public => (Flags & ~visibilityMask) | (uint) TypeAttributes.NotPublic,
            TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem
                => (Flags & ~visibilityMask) | (uint) TypeAttributes.NestedAssembly,
            _ => Flags
        };
    }

    /// <summary>
    /// Returns true if this type is public.
    /// </summary>
    public readonly bool IsPublic
    {
        get
        {
            var visibility = (TypeAttributes)(Flags & (uint)TypeAttributes.VisibilityMask);
            return visibility is TypeAttributes.Public or TypeAttributes.NestedPublic;
        }
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