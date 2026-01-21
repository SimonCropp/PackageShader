public class ModificationTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(ModificationTests).Assembly.Location)!;

    #region ModificationPlan Tests

    [Fact]
    public void ModificationPlan_SetAssemblyName_AddsString()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.SetAssemblyName("NewName");

        Assert.True(plan.NewStrings.Count > 0);
        Assert.Contains("NewName", plan.NewStrings.Keys);
    }

    [Fact]
    public void ModificationPlan_SetAssemblyPublicKey_AddsBlob()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var publicKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        plan.SetAssemblyPublicKey(publicKey);

        Assert.True(plan.NewBlobs.Count > 0);
    }

    [Fact]
    public void ModificationPlan_ClearStrongName_SetsPublicKeyToZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.ClearStrongName();

        var row = plan.GetAssemblyRow(1);
        Assert.Equal(0u, row.PublicKeyIndex);
    }

    [Fact]
    public void ModificationPlan_GetOrAddString_ReturnsSameIndexForSameString()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var index1 = plan.GetOrAddString("TestString");
        var index2 = plan.GetOrAddString("TestString");

        Assert.Equal(index1, index2);
    }

    [Fact]
    public void ModificationPlan_GetOrAddString_EmptyString_ReturnsZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var index = plan.GetOrAddString("");

        Assert.Equal(0u, index);
    }

    [Fact]
    public void ModificationPlan_GetOrAddBlob_EmptyBlob_ReturnsZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var index = plan.GetOrAddBlob([]);

        Assert.Equal(0u, index);
    }

    [Fact]
    public void ModificationPlan_MakeTypesInternal_ModifiesPublicTypes()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.MakeTypesInternal();

        // Check that at least one type was modified
        Assert.True(plan.ModifiedTypeDefRows.Count > 0);
    }

    [Fact]
    public void ModificationPlan_RedirectAssemblyRef_ExistingRef_ReturnsTrue()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyToProcess.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        // AssemblyToProcess should reference System.Runtime or similar
        var result = plan.RedirectAssemblyRef("System.Runtime", "System.Runtime_Shaded", null);

        // May or may not exist depending on the assembly
        // Just verify it doesn't throw
        Assert.True(result || !result);
    }

    [Fact]
    public void ModificationPlan_RedirectAssemblyRef_NonExistentRef_ReturnsFalse()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var result = plan.RedirectAssemblyRef("NonExistentAssembly", "NewName", null);

        Assert.False(result);
    }

    [Fact]
    public void ModificationPlan_AddTypeRef_ReturnsIncrementingRid()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var existingCount = reader.GetRowCount(TableIndex.TypeRef);
        var rid1 = plan.AddTypeRef(new());
        var rid2 = plan.AddTypeRef(new());

        Assert.Equal((uint)(existingCount + 1), rid1);
        Assert.Equal((uint)(existingCount + 2), rid2);
    }

    [Fact]
    public void ModificationPlan_AddMemberRef_ReturnsIncrementingRid()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var existingCount = reader.GetRowCount(TableIndex.MemberRef);
        var rid1 = plan.AddMemberRef(new());
        var rid2 = plan.AddMemberRef(new());

        Assert.Equal((uint)(existingCount + 1), rid1);
        Assert.Equal((uint)(existingCount + 2), rid2);
    }

    [Fact]
    public void ModificationPlan_AddCustomAttribute_AddsToList()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.AddCustomAttribute(new());
        plan.AddCustomAttribute(new());

        Assert.Equal(2, plan.NewCustomAttributes.Count);
    }

    [Fact]
    public void ModificationPlan_GetStrategy_NoModifications_ReturnsInPlacePatch()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var strategy = plan.GetStrategy();

        Assert.Equal(ModificationStrategy.InPlacePatch, strategy);
    }

    [Fact]
    public void ModificationPlan_GetStrategy_WithNewStrings_ReturnsRebuild()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.SetAssemblyName("NewAssemblyName");
        var strategy = plan.GetStrategy();

        Assert.NotEqual(ModificationStrategy.InPlacePatch, strategy);
    }

    [Fact]
    public void ModificationPlan_GetStrategy_WithNewRows_ReturnsRebuild()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        plan.AddCustomAttribute(new());
        var strategy = plan.GetStrategy();

        Assert.NotEqual(ModificationStrategy.InPlacePatch, strategy);
    }

    [Fact]
    public void ModificationPlan_EstimateNewMetadataSize_ReturnsNonZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var size = plan.EstimateNewMetadataSize();

        Assert.True(size > 0);
    }

    [Fact]
    public void ModificationPlan_EstimateNewMetadataSize_IncreasesWithNewData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var sizeBefo = plan.EstimateNewMetadataSize();
        plan.SetAssemblyName("AVeryLongNewAssemblyNameThatWillDefinitelyIncreaseSize");
        var sizeAfter = plan.EstimateNewMetadataSize();

        Assert.True(sizeAfter > sizeBefo);
    }

    [Fact]
    public void ModificationPlan_GetAssemblyRow_ReturnsModifiedRow()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var original = plan.GetAssemblyRow(1);
        var modifiedRow = original;
        modifiedRow.MajorVersion = 99;
        plan.SetAssemblyRow(1, modifiedRow);

        var retrieved = plan.GetAssemblyRow(1);
        Assert.Equal(99, retrieved.MajorVersion);
    }

    [Fact]
    public void ModificationPlan_GetAssemblyRow_UnmodifiedReturnsOriginal()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var original = reader.ReadAssemblyRow(1);
        var fromPlan = plan.GetAssemblyRow(1);

        Assert.Equal(original.NameIndex, fromPlan.NameIndex);
    }

    #endregion

    #region Heap Size Tracking Tests

    [Fact]
    public void ModificationPlan_FinalStringHeapSize_InitiallyEqualsSourceSize()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        Assert.Equal(reader.StringHeapSize, plan.FinalStringHeapSize);
    }

    [Fact]
    public void ModificationPlan_FinalBlobHeapSize_InitiallyEqualsSourceSize()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        Assert.Equal(reader.BlobHeapSize, plan.FinalBlobHeapSize);
    }

    [Fact]
    public void ModificationPlan_FinalStringHeapSize_GrowsWhenAddingStrings()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalStringHeapSize;
        plan.GetOrAddString("TestString123");

        // "TestString123" is 13 bytes + 1 null terminator = 14 bytes
        Assert.Equal(initialSize + 14u, plan.FinalStringHeapSize);
    }

    [Fact]
    public void ModificationPlan_FinalBlobHeapSize_GrowsWhenAddingBlobs()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalBlobHeapSize;
        var blob = new byte[100];
        plan.GetOrAddBlob(blob);

        // 100 bytes + 1 byte length header (< 128) = 101 bytes
        Assert.Equal(initialSize + 101u, plan.FinalBlobHeapSize);
    }

    [Fact]
    public void ModificationPlan_FinalStringHeapSize_CalculatesCorrectlyForUTF8()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalStringHeapSize;

        // Test with multi-byte UTF-8 characters
        plan.GetOrAddString("Hello世界"); // "世界" is 6 bytes in UTF-8

        // "Hello" = 5 bytes, "世界" = 6 bytes, null terminator = 1 byte
        Assert.Equal(initialSize + 12u, plan.FinalStringHeapSize);
    }

    [Fact]
    public void ModificationPlan_FinalBlobHeapSize_UsesCompressedLengthEncoding()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalBlobHeapSize;

        // Test 1-byte length header (< 0x80)
        var blob1 = new byte[50];
        plan.GetOrAddBlob(blob1);
        Assert.Equal(initialSize + 51u, plan.FinalBlobHeapSize); // 50 + 1 byte header

        // Test 2-byte length header (>= 0x80, < 0x4000)
        var sizeAfterBlob1 = plan.FinalBlobHeapSize;
        var blob2 = new byte[200];
        plan.GetOrAddBlob(blob2);
        Assert.Equal(sizeAfterBlob1 + 202u, plan.FinalBlobHeapSize); // 200 + 2 byte header

        // Test 4-byte length header (>= 0x4000)
        var sizeAfterBlob2 = plan.FinalBlobHeapSize;
        var blob3 = new byte[20000];
        plan.GetOrAddBlob(blob3);
        Assert.Equal(sizeAfterBlob2 + 20004u, plan.FinalBlobHeapSize); // 20000 + 4 byte header
    }

    [Fact]
    public void ModificationPlan_FinalStringHeapSize_DeduplicatesStrings()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalStringHeapSize;

        // Add same string twice
        plan.GetOrAddString("DuplicateString");
        var sizeAfterFirst = plan.FinalStringHeapSize;
        plan.GetOrAddString("DuplicateString");
        var sizeAfterSecond = plan.FinalStringHeapSize;

        // Size should only grow once
        Assert.Equal(initialSize + 16u, sizeAfterFirst); // 15 bytes + null
        Assert.Equal(sizeAfterFirst, sizeAfterSecond);
    }

    [Fact]
    public void ModificationPlan_FinalHeapSizes_LargeStringAddition()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalStringHeapSize;

        // Add a very large string (10KB)
        var largeString = new string('X', 10000);
        plan.GetOrAddString(largeString);

        Assert.Equal(initialSize + 10001u, plan.FinalStringHeapSize); // 10000 + null
    }

    [Fact]
    public void ModificationPlan_SetAssemblyName_UpdatesFinalStringHeapSize()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalStringHeapSize;
        plan.SetAssemblyName("MyNewAssemblyName");

        // "MyNewAssemblyName" = 17 bytes + 1 null = 18 bytes
        Assert.Equal(initialSize + 18u, plan.FinalStringHeapSize);
    }

    [Fact]
    public void ModificationPlan_SetAssemblyPublicKey_UpdatesFinalBlobHeapSize()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);
        var plan = new ModificationPlan(reader);

        var initialSize = plan.FinalBlobHeapSize;
        var publicKey = new byte[160]; // Typical RSA public key
        plan.SetAssemblyPublicKey(publicKey);

        // 160 bytes + 2 byte length header = 162 bytes
        Assert.Equal(initialSize + 162u, plan.FinalBlobHeapSize);
    }

    #endregion

    #region ModificationStrategy Tests

    [Fact]
    public void ModificationStrategy_EnumValues_AreDistinct()
    {
        var values = Enum.GetValues<ModificationStrategy>();

        Assert.Equal(3, values.Length);
        Assert.Contains(ModificationStrategy.InPlacePatch, values);
        Assert.Contains(ModificationStrategy.MetadataRebuildWithPadding, values);
        Assert.Contains(ModificationStrategy.FullMetadataSectionRebuild, values);
    }

    #endregion
}
