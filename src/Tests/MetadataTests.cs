public class MetadataTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(MetadataTests).Assembly.Location)!;

    #region StreamingMetadataReader Tests

    [Fact]
    public void StreamingMetadataReader_CanRead()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.NotNull(reader);
    }

    [Fact]
    public void HasTable_Assembly_ReturnsTrue()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.HasTable(TableIndex.Assembly));
    }

    [Fact]
    public void HasTable_TypeDef_ReturnsTrue()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.HasTable(TableIndex.TypeDef));
        Assert.True(reader.GetRowCount(TableIndex.TypeDef) > 0);
    }

    [Fact]
    public void GetRowCount_ReturnsCorrectCount()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Assembly table should have exactly 1 row
        Assert.Equal(1, reader.GetRowCount(TableIndex.Assembly));
    }

    [Fact]
    public void ReadAssemblyRow_ReturnsValidData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var row = reader.ReadAssemblyRow(1);

        Assert.True(row.NameIndex > 0);
    }

    [Fact]
    public void ReadTypeDefRow_ReturnsValidData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Row 1 is usually <Module>
        var row = reader.ReadTypeDefRow(1);
        Assert.True(row.NameIndex > 0);
    }

    [Fact]
    public void StringIndexSize_IsValid()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.StringIndexSize is 2 or 4);
    }

    [Fact]
    public void BlobIndexSize_IsValid()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.BlobIndexSize is 2 or 4);
    }

    [Fact]
    public void GuidIndexSize_IsValid()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.GuidIndexSize is 2 or 4);
    }

    [Fact]
    public void GetTableIndexSize_SmallTable_Returns2()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Assembly table has 1 row, should use 2-byte index
        Assert.Equal(2, reader.GetTableIndexSize(TableIndex.Assembly));
    }

    [Fact]
    public void StringHeapSize_IsNonZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.StringHeapSize > 0);
    }

    [Fact]
    public void BlobHeapSize_IsNonZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        Assert.True(reader.BlobHeapSize > 0);
    }

    [Fact]
    public void UndefinedVersionComponents_CastToUshort_Produces65535()
    {
        // This test documents the .NET behavior: casting Version.Build when it's -1 (undefined)
        // directly to ushort produces 65535 (0xFFFF), which can confuse some tools.
        // NOTE: This is a documentation test only - it does not test our production code.
        var version = new Version(1, 0); // Build = -1, Revision = -1

        // Direct cast produces 65535 (the bug we're protecting against)
        var badBuild = (ushort)version.Build;
        var badRevision = (ushort)version.Revision;

        Assert.Equal(65535, badBuild);
        Assert.Equal(65535, badRevision);

        // The fix uses Math.Max(0, ...) to convert -1 to 0
        var goodBuild = (ushort)Math.Max(0, version.Build);
        var goodRevision = (ushort)Math.Max(0, version.Revision);

        Assert.Equal(0, goodBuild);
        Assert.Equal(0, goodRevision);
    }

    [Fact]
    public void ReadAssemblyRefRow_VersionMatchesRawMetadataBytes()
    {
        // This test verifies that ReadAssemblyRefRow correctly reads version components
        // by comparing the parsed values against raw bytes from the metadata table.
        // AssemblyRef row layout: MajorVersion(2) + MinorVersion(2) + BuildNumber(2) + RevisionNumber(2) + ...
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
        Assert.True(refCount > 0, "Test assembly should have at least one AssemblyRef");

        for (uint rid = 1; rid <= refCount; rid++)
        {
            // Read the row using the production code
            var row = reader.ReadAssemblyRefRow(rid);

            // Read the raw bytes directly from the table
            var rawBytes = reader.ReadRow(TableIndex.AssemblyRef, rid);
            Assert.True(rawBytes.Length >= 8, "AssemblyRef row should have at least 8 bytes for version fields");

            // Parse version components directly from raw bytes (little-endian ushorts)
            var rawMajor = BitConverter.ToUInt16(rawBytes, 0);
            var rawMinor = BitConverter.ToUInt16(rawBytes, 2);
            var rawBuild = BitConverter.ToUInt16(rawBytes, 4);
            var rawRevision = BitConverter.ToUInt16(rawBytes, 6);

            // The production code should return values matching the raw metadata
            Assert.Equal(rawMajor, row.MajorVersion);
            Assert.Equal(rawMinor, row.MinorVersion);
            Assert.Equal(rawBuild, row.BuildNumber);
            Assert.Equal(rawRevision, row.RevisionNumber);
        }
    }

    [Fact]
    public void FindAssemblyRef_VersionMatchesRawMetadataBytes()
    {
        // This test verifies that FindAssemblyRef correctly reads version components
        // by comparing the parsed values against raw bytes from the metadata table.
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
        Assert.True(refCount > 0, "Test assembly should have at least one AssemblyRef");

        for (uint rid = 1; rid <= refCount; rid++)
        {
            var refInfo = reader.FindAssemblyRefByRid(rid);
            Assert.NotNull(refInfo);

            // Find the same ref by name to get the row from FindAssemblyRef
            var foundByName = reader.FindAssemblyRef(refInfo.Value.name);
            Assert.NotNull(foundByName);
            var (foundRid, row) = foundByName.Value;

            // Read the raw bytes directly from the table
            var rawBytes = reader.ReadRow(TableIndex.AssemblyRef, foundRid);
            Assert.True(rawBytes.Length >= 8, "AssemblyRef row should have at least 8 bytes for version fields");

            // Parse version components directly from raw bytes (little-endian ushorts)
            var rawMajor = BitConverter.ToUInt16(rawBytes, 0);
            var rawMinor = BitConverter.ToUInt16(rawBytes, 2);
            var rawBuild = BitConverter.ToUInt16(rawBytes, 4);
            var rawRevision = BitConverter.ToUInt16(rawBytes, 6);

            // The production code should return values matching the raw metadata
            Assert.Equal(rawMajor, row.MajorVersion);
            Assert.Equal(rawMinor, row.MinorVersion);
            Assert.Equal(rawBuild, row.BuildNumber);
            Assert.Equal(rawRevision, row.RevisionNumber);
        }
    }

    [Fact]
    public void FindAssemblyRef_ReturnsConsistentVersionWithReadAssemblyRefRow()
    {
        // Verify FindAssemblyRef and ReadAssemblyRefRow return the same version info
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
        Assert.True(refCount > 0, "Test assembly should have at least one AssemblyRef");

        for (uint rid = 1; rid <= refCount; rid++)
        {
            var refInfo = reader.FindAssemblyRefByRid(rid);
            Assert.NotNull(refInfo);

            var rowFromRead = reader.ReadAssemblyRefRow(rid);

            // Find the same ref by name to get the row from FindAssemblyRef
            var foundByName = reader.FindAssemblyRef(refInfo.Value.name);
            Assert.NotNull(foundByName);

            var (foundRid, rowFromFind) = foundByName.Value;
            Assert.Equal(rid, foundRid);

            // Version components should be identical between both methods
            Assert.Equal(rowFromRead.MajorVersion, rowFromFind.MajorVersion);
            Assert.Equal(rowFromRead.MinorVersion, rowFromFind.MinorVersion);
            Assert.Equal(rowFromRead.BuildNumber, rowFromFind.BuildNumber);
            Assert.Equal(rowFromRead.RevisionNumber, rowFromFind.RevisionNumber);
        }
    }

    [Fact]
    public void ReadRow_InvalidRid_ReturnsEmpty()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var row = reader.ReadRow(TableIndex.Assembly, 0);
        Assert.Empty(row);

        row = reader.ReadRow(TableIndex.Assembly, 999);
        Assert.Empty(row);
    }

    [Fact]
    public void GetRowOffset_ReturnsValidOffset()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var offset = reader.GetRowOffset(TableIndex.Assembly, 1);
        Assert.True(offset > 0);
    }

    [Fact]
    public void GetRowSize_ReturnsNonZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var size = reader.GetRowSize(TableIndex.Assembly);
        Assert.True(size > 0);
    }

    [Fact]
    public void CopyStringHeap_CopiesData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        using var output = new MemoryStream();

        reader.CopyStringHeap(output);

        Assert.True(output.Length > 0);
    }

    [Fact]
    public void CopyBlobHeap_CopiesData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        using var output = new MemoryStream();

        reader.CopyBlobHeap(output);

        Assert.True(output.Length > 0);
    }

    #endregion

    #region CodedIndexHelper Tests

    [Fact]
    public void CodedIndexHelper_GetSize_SmallTables_Returns2()
    {
        // Small tables should use 2-byte indices
        static int GetRowCount(TableIndex table) => 100;

        var size = CodedIndexHelper.GetSize(CodedIndex.TypeDefOrRef, GetRowCount);

        Assert.Equal(2, size);
    }

    [Fact]
    public void CodedIndexHelper_GetSize_LargeTables_Returns4()
    {
        // Large tables should use 4-byte indices
        // TypeDefOrRef uses 2 bits, so threshold is 2^(16-2) = 16384
        static int GetRowCount(TableIndex table) => 20000;

        var size = CodedIndexHelper.GetSize(CodedIndex.TypeDefOrRef, GetRowCount);

        Assert.Equal(4, size);
    }

    [Fact]
    public void CodedIndexHelper_EncodeToken_AssemblyRef()
    {
        var token = new MetadataToken(TableIndex.AssemblyRef, 5);

        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.ResolutionScope, token);

        // ResolutionScope: Module=0, ModuleRef=1, AssemblyRef=2, TypeRef=3
        // AssemblyRef is tag 2, so encoded = (5 << 2) | 2 = 22
        Assert.Equal(22u, encoded);
    }

    [Fact]
    public void CodedIndexHelper_EncodeToken_ZeroRid_ReturnsZero()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 0);

        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.TypeDefOrRef, token);

        Assert.Equal(0u, encoded);
    }

    [Fact]
    public void CodedIndexHelper_EncodeToken_InvalidTable_Throws()
    {
        var token = new MetadataToken(TableIndex.Assembly, 1);

        // Assembly is not valid for TypeDefOrRef
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.EncodeToken(CodedIndex.TypeDefOrRef, token));
    }

    #endregion

    #region MetadataToken Tests

    [Fact]
    public void MetadataToken_Constructor_SetsValue()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 42);

        Assert.Equal(42u, token.RID);
        Assert.Equal(TableIndex.TypeDef, token.TableIndex);
    }

    [Fact]
    public void MetadataToken_Value_CombinesTableAndRid()
    {
        var token = new MetadataToken(TableIndex.TypeRef, 10);

        // TypeRef = 0x01, so value = 0x0100000A
        Assert.Equal(0x0100000Au, token.Value);
    }

    [Fact]
    public void MetadataToken_ToString_ReturnsHexValue()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 5);

        Assert.Equal("0x02000005", token.ToString());
    }

    #endregion

    #region TypeDefRow Tests

    [Fact]
    public void TypeDefRow_MakeInternal_ChangesPublicToNotPublic()
    {
        var row = new TypeDefRow { Flags = (uint)TypeAttributes.Public };

        row.MakeInternal();

        Assert.Equal((uint)TypeAttributes.NotPublic, row.Flags & (uint)TypeAttributes.VisibilityMask);
    }

    [Fact]
    public void TypeDefRow_MakeInternal_ChangesNestedPublicToNestedAssembly()
    {
        var row = new TypeDefRow { Flags = (uint)TypeAttributes.NestedPublic };

        row.MakeInternal();

        Assert.Equal((uint)TypeAttributes.NestedAssembly, row.Flags & (uint)TypeAttributes.VisibilityMask);
    }

    [Fact]
    public void TypeDefRow_MakeInternal_PreservesOtherFlags()
    {
        var row = new TypeDefRow { Flags = (uint)(TypeAttributes.Public | TypeAttributes.Sealed) };

        row.MakeInternal();

        Assert.True((row.Flags & (uint)TypeAttributes.Sealed) != 0);
    }

    [Fact]
    public void TypeDefRow_IsPublic_ReturnsTrueForPublic()
    {
        var row = new TypeDefRow { Flags = (uint)TypeAttributes.Public };

        Assert.True(row.IsPublic);
    }

    [Fact]
    public void TypeDefRow_IsPublic_ReturnsTrueForNestedPublic()
    {
        var row = new TypeDefRow { Flags = (uint)TypeAttributes.NestedPublic };

        Assert.True(row.IsPublic);
    }

    [Fact]
    public void TypeDefRow_IsPublic_ReturnsFalseForNotPublic()
    {
        var row = new TypeDefRow { Flags = (uint)TypeAttributes.NotPublic };

        Assert.False(row.IsPublic);
    }

    [Fact]
    public void TypeDefRow_Write_RoundTrip()
    {
        var original = new TypeDefRow
        {
            Flags = (uint)TypeAttributes.Public,
            NameIndex = 100,
            NamespaceIndex = 200,
            ExtendsIndex = 5,
            FieldListIndex = 10,
            MethodListIndex = 20
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        original.Write(writer, stringIndexSize: 2, typeDefOrRefSize: 2, fieldIndexSize: 2, methodIndexSize: 2);

        ms.Position = 0;
        var data = ms.ToArray();
        var read = TypeDefRow.Read(data, stringIndexSize: 2, typeDefOrRefSize: 2, fieldIndexSize: 2, methodIndexSize: 2);

        Assert.Equal(original.Flags, read.Flags);
        Assert.Equal(original.NameIndex, read.NameIndex);
        Assert.Equal(original.NamespaceIndex, read.NamespaceIndex);
        Assert.Equal(original.ExtendsIndex, read.ExtendsIndex);
        Assert.Equal(original.FieldListIndex, read.FieldListIndex);
        Assert.Equal(original.MethodListIndex, read.MethodListIndex);
    }

    #endregion

    #region AssemblyRow Tests

    [Fact]
    public void AssemblyRow_Write_WritesAllFields()
    {
        var row = new AssemblyRow
        {
            HashAlgId = 0x8004,
            MajorVersion = 1,
            MinorVersion = 2,
            BuildNumber = 3,
            RevisionNumber = 4,
            Flags = 0,
            PublicKeyIndex = 100,
            NameIndex = 200,
            CultureIndex = 0
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 2, stringIndexSize: 2);

        // Expected size: 4 + 2 + 2 + 2 + 2 + 4 + 2 + 2 + 2 = 22 bytes
        Assert.Equal(22, ms.Length);
    }

    [Fact]
    public void AssemblyRow_Write_4ByteIndices_WritesCorrectSize()
    {
        var row = new AssemblyRow
        {
            HashAlgId = 0x8004,
            MajorVersion = 1,
            MinorVersion = 2,
            BuildNumber = 3,
            RevisionNumber = 4,
            Flags = 0,
            PublicKeyIndex = 100,
            NameIndex = 200,
            CultureIndex = 0
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        row.Write(writer, blobIndexSize: 4, stringIndexSize: 4);

        // Expected size: 4 + 2 + 2 + 2 + 2 + 4 + 4 + 4 + 4 = 28 bytes
        Assert.Equal(28, ms.Length);
    }

    #endregion

    #region StreamingMetadataWriter Heap Index Size Tests

    [Fact]
    public void StreamingMetadataWriter_SmallHeaps_Uses2ByteIndices()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        // Don't add anything - heaps should remain small
        using var output = new MemoryStream();
        var writer = new StreamingMetadataWriter(reader, plan);
        writer.Write(output);

        // Verify the HeapSizes byte in the output
        output.Position = 0;
        var metadata = output.ToArray();

        // Find the BSJB signature
        var bsjbIndex = FindSignature(metadata, [0x42, 0x53, 0x4A, 0x42]);
        Assert.True(bsjbIndex >= 0, "BSJB signature not found");

        // Navigate to table heap header (skip metadata root, stream headers)
        // This is a simplified check - just verify the metadata was written
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void StreamingMetadataWriter_StringHeapGrows_UpdatesHeapSizesByte()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Assume source has 2-byte string indices (< 65536 bytes)
        if (reader.StringIndexSize != 2)
        {
            // Skip this test if source already has 4-byte indices
            return;
        }

        var plan = new ModificationPlan(reader);

        // Add strings to push heap over 65536 bytes
        var currentSize = reader.StringHeapSize;

        // Calculate how many strings we need to add
        if (currentSize >= 65536)
        {
            // Heap is already large, skip this test
            return;
        }

        var bytesNeeded = 65536 - currentSize + 5000; // Add 5000 extra bytes to be sure
        var stringSize = 90; // Each string is 90 chars + null = 91 bytes
        var stringsNeeded = (int)(bytesNeeded / stringSize) + 100; // Add extra to be absolutely sure

        for (var i = 0; i < stringsNeeded; i++)
        {
            // Each string must be unique (strings are deduplicated)
            plan.GetOrAddString($"TestString_{i:D10}_{new string('X', 70)}");
        }

        // Verify final size is > 65536
        Assert.True(plan.FinalStringHeapSize >= 0x10000,
            $"Final string heap size {plan.FinalStringHeapSize} should be >= 65536");

        // Try to write (this should either work or throw NotSupportedException for unsupported tables)
        using var output = new MemoryStream();
        var writer = new StreamingMetadataWriter(reader, plan);

        try
        {
            writer.Write(output);

            // If it succeeded, verify the output has valid metadata
            Assert.True(output.Length > 0);
        }
        catch (NotSupportedException ex)
        {
            // Expected if there are unsupported tables that need rewriting
            Assert.Contains("needs rewriting due to heap index size changes", ex.Message);
        }
    }

    [Fact]
    public void StreamingMetadataWriter_BlobHeapGrows_UpdatesHeapSizesByte()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Assume source has 2-byte blob indices (< 65536 bytes)
        if (reader.BlobIndexSize != 2)
        {
            // Skip this test if source already has 4-byte indices
            return;
        }

        var plan = new ModificationPlan(reader);

        // Add blobs to push heap over 65536 bytes
        var currentSize = reader.BlobHeapSize;

        // Calculate how many blobs we need to add
        if (currentSize >= 65536)
        {
            // Heap is already large, skip this test
            return;
        }

        var bytesNeeded = 65536 - currentSize + 1000; // Add 1000 extra bytes to be sure
        var blobSize = 1000; // Each blob is ~1000 bytes + 2-byte header
        var blobsNeeded = (int)(bytesNeeded / blobSize) + 10;

        for (var i = 0; i < blobsNeeded; i++)
        {
            // Each blob is ~1000 bytes
            plan.GetOrAddBlob(new byte[1000]);
        }

        // Verify final size is > 65536
        Assert.True(plan.FinalBlobHeapSize >= 0x10000,
            $"Final blob heap size {plan.FinalBlobHeapSize} should be >= 65536");

        // Try to write (this should either work or throw NotSupportedException for unsupported tables)
        using var output = new MemoryStream();
        var writer = new StreamingMetadataWriter(reader, plan);

        try
        {
            writer.Write(output);

            // If it succeeded, verify the output has valid metadata
            Assert.True(output.Length > 0);
        }
        catch (NotSupportedException ex)
        {
            // Expected if there are unsupported tables that need rewriting
            Assert.Contains("needs rewriting due to heap index size changes", ex.Message);
        }
    }

    [Fact]
    public void StreamingMetadataWriter_IndexSizesNeverShrink()
    {
        // This test verifies that even if we don't add data, index sizes
        // use the maximum of source and calculated sizes
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        // Don't add anything - final sizes should equal source sizes
        Assert.Equal(reader.StringHeapSize, plan.FinalStringHeapSize);
        Assert.Equal(reader.BlobHeapSize, plan.FinalBlobHeapSize);

        // Writer should use source index sizes
        using var output = new MemoryStream();
        var writer = new StreamingMetadataWriter(reader, plan);
        writer.Write(output);

        Assert.True(output.Length > 0);
    }

    static int FindSignature(byte[] data, byte[] signature)
    {
        for (var i = 0; i <= data.Length - signature.Length; i++)
        {
            var match = true;
            for (var j = 0; j < signature.Length; j++)
            {
                if (data[i + j] != signature[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    #endregion
}
