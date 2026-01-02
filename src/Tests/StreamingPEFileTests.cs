public class StreamingPEFileTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(StreamingPEFileTests).Assembly.Location)!;

    [Fact]
    public void Open_ValidAssembly_ReturnsInstance()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.NotNull(peFile);
        Assert.Equal(assemblyPath, peFile.FilePath);
    }

    [Fact]
    public void Open_NonExistentFile_Throws()
    {
        var path = Path.Combine(binDirectory, "NonExistent.dll");

        Assert.Throws<FileNotFoundException>(() => StreamingPEFile.Open(path));
    }

    [Fact]
    public void Open_InvalidPE_Throws()
    {
        using var tempDir = new TempDirectory();
        var invalidPath = Path.Combine(tempDir, "invalid.dll");
        File.WriteAllText(invalidPath, "This is not a PE file");

        Assert.ThrowsAny<BadImageFormatException>(() => StreamingPEFile.Open(invalidPath));
    }

    [Fact]
    public void Sections_ContainsExpectedSections()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.NotEmpty(peFile.Sections);
    }

    [Fact]
    public void MetadataRva_IsNonZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.True(peFile.MetadataRva > 0);
        Assert.True(peFile.MetadataSize > 0);
        Assert.True(peFile.MetadataFileOffset > 0);
    }

    [Fact]
    public void StreamHeaders_ContainsExpectedStreams()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.True(peFile.StreamHeaders.Length > 0);

        // Should have at least #~ (or #-) and #Strings
        var streamNames = peFile.StreamHeaders.Select(h => h.Name).ToList();
        Assert.True(streamNames.Contains("#~") || streamNames.Contains("#-"));
        Assert.Contains("#Strings", streamNames);
    }

    [Fact]
    public void GetStreamHeader_ExistingStream_ReturnsHeader()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        var stringsHeader = peFile.GetStreamHeader("#Strings");

        Assert.NotNull(stringsHeader);
        Assert.True(stringsHeader.Size > 0);
    }

    [Fact]
    public void GetStreamHeader_NonExistentStream_ReturnsNull()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        var header = peFile.GetStreamHeader("#NonExistent");

        Assert.Null(header);
    }

    [Fact]
    public void ResolveRva_ValidRva_ReturnsFileOffset()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        var offset = peFile.ResolveRva(peFile.MetadataRva);

        Assert.Equal(peFile.MetadataFileOffset, offset);
    }

    [Fact]
    public void ResolveRva_InvalidRva_Throws()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.Throws<BadImageFormatException>(() => peFile.ResolveRva(0xFFFFFFFF));
    }

    [Fact]
    public void GetSectionAtRva_ValidRva_ReturnsSection()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        var section = peFile.GetSectionAtRva(peFile.MetadataRva);

        Assert.NotNull(section);
        Assert.True(peFile.MetadataRva >= section.VirtualAddress);
    }

    [Fact]
    public void GetSectionAtRva_InvalidRva_ReturnsNull()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        var section = peFile.GetSectionAtRva(0xFFFFFFFF);

        Assert.Null(section);
    }

    [Fact]
    public void ReadBytesAt_ReturnsCorrectData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        // Read the metadata signature (BSJB)
        var data = peFile.ReadBytesAt(peFile.MetadataFileOffset, 4);

        Assert.Equal(4, data.Length);
        Assert.Equal(0x42, data[0]); // 'B'
        Assert.Equal(0x53, data[1]); // 'S'
        Assert.Equal(0x4A, data[2]); // 'J'
        Assert.Equal(0x42, data[3]); // 'B'
    }

    [Fact]
    public void CopyRegion_CopiesData()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var output = new MemoryStream();

        peFile.CopyRegion(peFile.MetadataFileOffset, 100, output);

        Assert.Equal(100, output.Length);

        // Verify BSJB signature
        output.Position = 0;
        var reader = new BinaryReader(output);
        Assert.Equal(0x424A5342u, reader.ReadUInt32());
    }

    [Fact]
    public void MetadataVersionString_IsNotEmpty()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.False(string.IsNullOrEmpty(peFile.MetadataVersionString));
    }

    [Fact]
    public void FileLength_MatchesActualFileSize()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");
        var actualLength = new FileInfo(assemblyPath).Length;

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.Equal(actualLength, peFile.FileLength);
    }

    [Fact]
    public void PEReader_IsAccessible()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.NotNull(peFile.PEReader);
        Assert.True(peFile.PEReader.HasMetadata);
    }

    [Fact]
    public void StrongNamedAssembly_HasStrongNameInfo()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.True(peFile.StrongNameRva > 0);
        Assert.True(peFile.StrongNameSize > 0);
        Assert.True(peFile.StrongNameFileOffset > 0);
    }

    [Fact]
    public void NonStrongNamedAssembly_HasZeroStrongNameInfo()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyWithNoStrongName.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        Assert.Equal(0u, peFile.StrongNameRva);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        var peFile = StreamingPEFile.Open(assemblyPath);
        peFile.Dispose();
        peFile.Dispose(); // Should not throw
    }
}
