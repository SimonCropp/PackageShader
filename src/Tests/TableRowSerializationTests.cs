/// <summary>
/// Round-trip serialization tests for AssemblyRefRow, TypeRefRow, MemberRefRow, and CustomAttributeRow.
/// </summary>
public class TableRowSerializationTests
{
    // =========================================================================
    // AssemblyRefRow
    // =========================================================================

    [Fact]
    public void AssemblyRefRow_Write_2ByteIndices_CorrectSize()
    {
        var row = MakeAssemblyRefRow();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        // 2+2+2+2 (versions) + 4 (flags) + 2+2+2+2 (indices) = 20 bytes
        Assert.Equal(20, ms.Length);
    }

    [Fact]
    public void AssemblyRefRow_Write_4ByteIndices_CorrectSize()
    {
        var row = MakeAssemblyRefRow();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 4, stringIndexSize: 4);

        // 2+2+2+2 (versions) + 4 (flags) + 4+4+4+4 (indices) = 28 bytes
        Assert.Equal(28, ms.Length);
    }

    [Fact]
    public void AssemblyRefRow_Write_2ByteIndices_VersionFieldsCorrect()
    {
        var row = new AssemblyRefRow
        {
            MajorVersion = 1,
            MinorVersion = 2,
            BuildNumber = 3,
            RevisionNumber = 4,
            Flags = 0,
            PublicKeyOrTokenIndex = 10,
            NameIndex = 20,
            CultureIndex = 0,
            HashValueIndex = 0
        };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        Assert.Equal(1, reader.ReadUInt16()); // MajorVersion
        Assert.Equal(2, reader.ReadUInt16()); // MinorVersion
        Assert.Equal(3, reader.ReadUInt16()); // BuildNumber
        Assert.Equal(4, reader.ReadUInt16()); // RevisionNumber
    }

    [Fact]
    public void AssemblyRefRow_Write_2ByteIndices_IndicesCorrect()
    {
        var row = new AssemblyRefRow
        {
            MajorVersion = 1, MinorVersion = 0, BuildNumber = 0, RevisionNumber = 0,
            Flags = 0,
            PublicKeyOrTokenIndex = 50,
            NameIndex = 100,
            CultureIndex = 0,
            HashValueIndex = 0
        };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        reader.ReadUInt16(); // MajorVersion
        reader.ReadUInt16(); // MinorVersion
        reader.ReadUInt16(); // BuildNumber
        reader.ReadUInt16(); // RevisionNumber
        reader.ReadUInt32(); // Flags
        Assert.Equal(50, reader.ReadUInt16());  // PublicKeyOrTokenIndex (blob)
        Assert.Equal(100, reader.ReadUInt16()); // NameIndex (string)
        Assert.Equal(0, reader.ReadUInt16());   // CultureIndex (string)
        Assert.Equal(0, reader.ReadUInt16());   // HashValueIndex (blob)
    }

    [Fact]
    public void AssemblyRefRow_Write_4ByteIndices_IndicesCorrect()
    {
        var row = new AssemblyRefRow
        {
            MajorVersion = 2, MinorVersion = 0, BuildNumber = 0, RevisionNumber = 0,
            Flags = 0,
            PublicKeyOrTokenIndex = 70000,
            NameIndex = 80000,
            CultureIndex = 0,
            HashValueIndex = 0
        };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 4, stringIndexSize: 4);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        reader.ReadUInt16(); // MajorVersion
        reader.ReadUInt16(); // MinorVersion
        reader.ReadUInt16(); // BuildNumber
        reader.ReadUInt16(); // RevisionNumber
        reader.ReadUInt32(); // Flags
        Assert.Equal(70000u, reader.ReadUInt32()); // PublicKeyOrTokenIndex
        Assert.Equal(80000u, reader.ReadUInt32()); // NameIndex
        Assert.Equal(0u, reader.ReadUInt32());      // CultureIndex
        Assert.Equal(0u, reader.ReadUInt32());      // HashValueIndex
    }

    [Fact]
    public void AssemblyRefRow_Write_FlagsPreserved()
    {
        var row = new AssemblyRefRow { Flags = 0x0001, MajorVersion = 0 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        ms.Position = 8; // Skip 4 version ushorts
        using var reader = new BinaryReader(ms);
        Assert.Equal(0x0001u, reader.ReadUInt32());
    }

    // =========================================================================
    // TypeRefRow
    // =========================================================================

    [Fact]
    public void TypeRefRow_Write_2ByteIndices_CorrectSize()
    {
        var row = new TypeRefRow { ResolutionScopeIndex = 5, NameIndex = 10, NamespaceIndex = 0 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, resolutionScopeSize: 2, stringIndexSize: 2);

        // 2 + 2 + 2 = 6 bytes
        Assert.Equal(6, ms.Length);
    }

    [Fact]
    public void TypeRefRow_Write_4ByteIndices_CorrectSize()
    {
        var row = new TypeRefRow { ResolutionScopeIndex = 5, NameIndex = 10, NamespaceIndex = 0 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, resolutionScopeSize: 4, stringIndexSize: 4);

        // 4 + 4 + 4 = 12 bytes
        Assert.Equal(12, ms.Length);
    }

    [Fact]
    public void TypeRefRow_RoundTrip_2ByteIndices()
    {
        var original = new TypeRefRow
        {
            ResolutionScopeIndex = 22,
            NameIndex = 100,
            NamespaceIndex = 200
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, resolutionScopeSize: 2, stringIndexSize: 2);

        var data = ms.ToArray();
        var read = TypeRefRow.Read(data, resolutionScopeSize: 2, stringIndexSize: 2);

        Assert.Equal(original.ResolutionScopeIndex, read.ResolutionScopeIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.NamespaceIndex, read.NamespaceIndex);
    }

    [Fact]
    public void TypeRefRow_RoundTrip_4ByteIndices()
    {
        var original = new TypeRefRow
        {
            ResolutionScopeIndex = 70000,
            NameIndex = 80000,
            NamespaceIndex = 90000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, resolutionScopeSize: 4, stringIndexSize: 4);

        var data = ms.ToArray();
        var read = TypeRefRow.Read(data, resolutionScopeSize: 4, stringIndexSize: 4);

        Assert.Equal(original.ResolutionScopeIndex, read.ResolutionScopeIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.NamespaceIndex, read.NamespaceIndex);
    }

    [Fact]
    public void TypeRefRow_RoundTrip_MixedIndexSizes()
    {
        // resolutionScope=2 bytes, string=4 bytes
        var original = new TypeRefRow
        {
            ResolutionScopeIndex = 22,
            NameIndex = 80000,
            NamespaceIndex = 90000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, resolutionScopeSize: 2, stringIndexSize: 4);

        // 2 + 4 + 4 = 10 bytes
        Assert.Equal(10, ms.Length);

        var data = ms.ToArray();
        var read = TypeRefRow.Read(data, resolutionScopeSize: 2, stringIndexSize: 4);

        Assert.Equal(original.ResolutionScopeIndex, read.ResolutionScopeIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.NamespaceIndex, read.NamespaceIndex);
    }

    // =========================================================================
    // MemberRefRow
    // =========================================================================

    [Fact]
    public void MemberRefRow_Write_2ByteIndices_CorrectSize()
    {
        var row = new MemberRefRow { ClassIndex = 5, NameIndex = 10, SignatureIndex = 20 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, memberRefParentSize: 2, stringIndexSize: 2, blobIndexSize: 2);

        // 2 + 2 + 2 = 6 bytes
        Assert.Equal(6, ms.Length);
    }

    [Fact]
    public void MemberRefRow_Write_4ByteIndices_CorrectSize()
    {
        var row = new MemberRefRow { ClassIndex = 5, NameIndex = 10, SignatureIndex = 20 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, memberRefParentSize: 4, stringIndexSize: 4, blobIndexSize: 4);

        // 4 + 4 + 4 = 12 bytes
        Assert.Equal(12, ms.Length);
    }

    [Fact]
    public void MemberRefRow_RoundTrip_2ByteIndices()
    {
        var original = new MemberRefRow
        {
            ClassIndex = 10,
            NameIndex = 20,
            SignatureIndex = 30
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, memberRefParentSize: 2, stringIndexSize: 2, blobIndexSize: 2);

        var data = ms.ToArray();
        var read = MemberRefRow.Read(data, memberRefParentSize: 2, stringIndexSize: 2, blobIndexSize: 2);

        Assert.Equal(original.ClassIndex, read.ClassIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.SignatureIndex, read.SignatureIndex);
    }

    [Fact]
    public void MemberRefRow_RoundTrip_4ByteIndices()
    {
        var original = new MemberRefRow
        {
            ClassIndex = 70000,
            NameIndex = 80000,
            SignatureIndex = 90000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, memberRefParentSize: 4, stringIndexSize: 4, blobIndexSize: 4);

        var data = ms.ToArray();
        var read = MemberRefRow.Read(data, memberRefParentSize: 4, stringIndexSize: 4, blobIndexSize: 4);

        Assert.Equal(original.ClassIndex, read.ClassIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.SignatureIndex, read.SignatureIndex);
    }

    [Fact]
    public void MemberRefRow_RoundTrip_MixedIndexSizes()
    {
        var original = new MemberRefRow
        {
            ClassIndex = 5000,
            NameIndex = 80000,
            SignatureIndex = 90000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, memberRefParentSize: 2, stringIndexSize: 4, blobIndexSize: 4);

        // 2 + 4 + 4 = 10 bytes
        Assert.Equal(10, ms.Length);

        var data = ms.ToArray();
        var read = MemberRefRow.Read(data, memberRefParentSize: 2, stringIndexSize: 4, blobIndexSize: 4);

        Assert.Equal(original.ClassIndex, read.ClassIndex);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.SignatureIndex, read.SignatureIndex);
    }

    // =========================================================================
    // CustomAttributeRow
    // =========================================================================

    [Fact]
    public void CustomAttributeRow_Write_2ByteIndices_CorrectSize()
    {
        var row = new CustomAttributeRow { ParentIndex = 5, TypeIndex = 10, ValueIndex = 20 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, hasCustomAttributeSize: 2, customAttributeTypeSize: 2, blobIndexSize: 2);

        // 2 + 2 + 2 = 6 bytes
        Assert.Equal(6, ms.Length);
    }

    [Fact]
    public void CustomAttributeRow_Write_4ByteIndices_CorrectSize()
    {
        var row = new CustomAttributeRow { ParentIndex = 5, TypeIndex = 10, ValueIndex = 20 };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, hasCustomAttributeSize: 4, customAttributeTypeSize: 4, blobIndexSize: 4);

        // 4 + 4 + 4 = 12 bytes
        Assert.Equal(12, ms.Length);
    }

    [Fact]
    public void CustomAttributeRow_RoundTrip_2ByteIndices()
    {
        var original = new CustomAttributeRow
        {
            ParentIndex = 10,
            TypeIndex = 20,
            ValueIndex = 30
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, hasCustomAttributeSize: 2, customAttributeTypeSize: 2, blobIndexSize: 2);

        var data = ms.ToArray();
        var read = CustomAttributeRow.Read(data,
            hasCustomAttributeSize: 2, customAttributeTypeSize: 2, blobIndexSize: 2);

        Assert.Equal(original.ParentIndex, read.ParentIndex);
        Assert.Equal(original.TypeIndex, read.TypeIndex);
        Assert.Equal(original.ValueIndex, read.ValueIndex);
    }

    [Fact]
    public void CustomAttributeRow_RoundTrip_4ByteIndices()
    {
        var original = new CustomAttributeRow
        {
            ParentIndex = 70000,
            TypeIndex = 80000,
            ValueIndex = 90000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, hasCustomAttributeSize: 4, customAttributeTypeSize: 4, blobIndexSize: 4);

        var data = ms.ToArray();
        var read = CustomAttributeRow.Read(data,
            hasCustomAttributeSize: 4, customAttributeTypeSize: 4, blobIndexSize: 4);

        Assert.Equal(original.ParentIndex, read.ParentIndex);
        Assert.Equal(original.TypeIndex, read.TypeIndex);
        Assert.Equal(original.ValueIndex, read.ValueIndex);
    }

    [Fact]
    public void CustomAttributeRow_RoundTrip_MixedIndexSizes()
    {
        var original = new CustomAttributeRow
        {
            ParentIndex = 5000,
            TypeIndex = 80000,
            ValueIndex = 200
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Write(writer, hasCustomAttributeSize: 2, customAttributeTypeSize: 4, blobIndexSize: 2);

        // 2 + 4 + 2 = 8 bytes
        Assert.Equal(8, ms.Length);

        var data = ms.ToArray();
        var read = CustomAttributeRow.Read(data,
            hasCustomAttributeSize: 2, customAttributeTypeSize: 4, blobIndexSize: 2);

        Assert.Equal(original.ParentIndex, read.ParentIndex);
        Assert.Equal(original.TypeIndex, read.TypeIndex);
        Assert.Equal(original.ValueIndex, read.ValueIndex);
    }

    // =========================================================================
    // AssemblyRefRow — explicit value verification (little-endian)
    // =========================================================================

    [Fact]
    public void AssemblyRefRow_Write_Values_LittleEndian()
    {
        var row = new AssemblyRefRow
        {
            MajorVersion = 0x0102,
            MinorVersion = 0,
            BuildNumber = 0,
            RevisionNumber = 0,
            Flags = 0,
            PublicKeyOrTokenIndex = 0,
            NameIndex = 0,
            CultureIndex = 0,
            HashValueIndex = 0
        };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        var bytes = ms.ToArray();
        // MajorVersion 0x0102 in little-endian: 0x02, 0x01
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    static AssemblyRefRow MakeAssemblyRefRow() => new()
    {
        MajorVersion = 1,
        MinorVersion = 2,
        BuildNumber = 3,
        RevisionNumber = 4,
        Flags = 0,
        PublicKeyOrTokenIndex = 10,
        NameIndex = 20,
        CultureIndex = 0,
        HashValueIndex = 0
    };
}
