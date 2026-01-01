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

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

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

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

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

    // Type attribute flags
    public const uint VisibilityMask = 0x00000007;
    public const uint NotPublic = 0x00000000;
    public const uint Public = 0x00000001;
    public const uint NestedPublic = 0x00000002;
    public const uint NestedPrivate = 0x00000003;
    public const uint NestedFamily = 0x00000004;
    public const uint NestedAssembly = 0x00000005;
    public const uint NestedFamANDAssem = 0x00000006;
    public const uint NestedFamORAssem = 0x00000007;

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
        var visibility = Flags & VisibilityMask;

        // Only change if currently public (not nested)
        if (visibility == Public)
        {
            Flags = (Flags & ~VisibilityMask) | NotPublic;
        }
        else if (visibility == NestedPublic)
        {
            Flags = (Flags & ~VisibilityMask) | NestedAssembly;
        }
        else if (visibility == NestedFamily)
        {
            Flags = (Flags & ~VisibilityMask) | NestedAssembly;
        }
        else if (visibility == NestedFamORAssem)
        {
            Flags = (Flags & ~VisibilityMask) | NestedAssembly;
        }
    }

    /// <summary>
    /// Returns true if this type is public.
    /// </summary>
    public readonly bool IsPublic
    {
        get
        {
            var visibility = Flags & VisibilityMask;
            return visibility == Public || visibility == NestedPublic;
        }
    }

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

/// <summary>
/// CustomAttribute table row (0x0C).
/// </summary>
public struct CustomAttributeRow
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

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

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

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

/// <summary>
/// MemberRef table row (0x0A).
/// </summary>
public struct MemberRefRow
{
    public uint ClassIndex;      // MemberRefParent coded index
    public uint NameIndex;       // String heap index
    public uint SignatureIndex;  // Blob heap index

    public static MemberRefRow Read(byte[] data, int memberRefParentSize,
        int stringIndexSize, int blobIndexSize)
    {
        var reader = new TableRowReader(data);
        return new()
        {
            ClassIndex = reader.ReadIndex(memberRefParentSize),
            NameIndex = reader.ReadIndex(stringIndexSize),
            SignatureIndex = reader.ReadIndex(blobIndexSize)
        };
    }

    public readonly void Write(BinaryWriter writer, int memberRefParentSize,
        int stringIndexSize, int blobIndexSize)
    {
        WriteIndex(writer, ClassIndex, memberRefParentSize);
        WriteIndex(writer, NameIndex, stringIndexSize);
        WriteIndex(writer, SignatureIndex, blobIndexSize);
    }

    private static void WriteIndex(BinaryWriter writer, uint value, int size)
    {
        if (size == 2)
            writer.Write((ushort)value);
        else
            writer.Write(value);
    }
}

/// <summary>
/// Helper for reading values from table rows.
/// </summary>
public class TableRowReader(byte[] data)
{
    private int position = 0;

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
