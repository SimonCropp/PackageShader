[Collection("Sequential")]
public class StreamingStrongNameSignerTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(StreamingStrongNameSignerTests).Assembly.Location)!;
    static string keyFilePath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");

    // -------------------------------------------------------------------------
    // SignFile — basic happy path
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_AssemblyWithStrongName_ReturnsTrue()
    {
        // Use an assembly that already has a strong-name signature placeholder
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        var result = StreamingStrongNameSigner.SignFile(targetPath, key);

        Assert.True(result, "SignFile should return true when a signature placeholder exists");
    }

    [Fact]
    public void SignFile_ProducesReadableAssembly()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        StreamingStrongNameSigner.SignFile(targetPath, key);

        // Verify the signed output is still a valid PE
        using var fs = File.OpenRead(targetPath);
        using var peReader = new PEReader(fs);
        Assert.True(peReader.HasMetadata);
        Assert.True(peReader.GetMetadataReader().IsAssembly);
    }

    [Fact]
    public void SignFile_SignatureAreaNonZero_AfterSigning()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        StreamingStrongNameSigner.SignFile(targetPath, key);

        // After signing, the strong-name signature directory should be non-zero bytes
        using var stream = File.OpenRead(targetPath);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = peReader.PEHeaders;
        Assert.NotNull(headers.CorHeader);

        var dir = headers.CorHeader.StrongNameSignatureDirectory;
        Assert.True(dir.Size > 0, "Signature placeholder should have a size");

        // Resolve signature offset and read bytes
        var rva = dir.RelativeVirtualAddress;
        foreach (var section in headers.SectionHeaders)
        {
            if (rva >= section.VirtualAddress &&
                rva < section.VirtualAddress + section.SizeOfRawData)
            {
                var offset = rva - section.VirtualAddress + section.PointerToRawData;
                stream.Position = offset;
                var sigBytes = new byte[Math.Min(dir.Size, 16)];
                stream.ReadExactly(sigBytes, 0, sigBytes.Length);
                Assert.True(sigBytes.Any(_ => _ != 0), "Signature bytes should be non-zero after signing");
                return;
            }
        }

        Assert.Fail("Could not locate signature section");
    }

    // -------------------------------------------------------------------------
    // SignFile — assembly without a placeholder returns false
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_AssemblyWithNoStrongName_ReturnsFalse()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithNoStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Unsigned.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        var result = StreamingStrongNameSigner.SignFile(targetPath, key);

        Assert.False(result, "SignFile should return false when there is no signature placeholder");
    }

    // -------------------------------------------------------------------------
    // SignFile — file is not modified when there is no placeholder
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_NoPlaceholder_FileUnchanged()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithNoStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Unsigned.dll");
        File.Copy(sourcePath, targetPath);

        var beforeBytes = File.ReadAllBytes(targetPath);
        var key = StrongNameKey.FromFile(keyFilePath);
        StreamingStrongNameSigner.SignFile(targetPath, key);
        var afterBytes = File.ReadAllBytes(targetPath);

        Assert.Equal(beforeBytes, afterBytes);
    }

    // -------------------------------------------------------------------------
    // SignFile — file size is preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_FileSize_Preserved()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var sizeBefore = new FileInfo(targetPath).Length;
        var key = StrongNameKey.FromFile(keyFilePath);
        StreamingStrongNameSigner.SignFile(targetPath, key);
        var sizeAfter = new FileInfo(targetPath).Length;

        Assert.Equal(sizeBefore, sizeAfter);
    }

    // -------------------------------------------------------------------------
    // Idempotency — signing the same assembly twice should not corrupt it
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_SignedTwice_StillReadable()
    {
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        StreamingStrongNameSigner.SignFile(targetPath, key);
        StreamingStrongNameSigner.SignFile(targetPath, key);

        using var fs = File.OpenRead(targetPath);
        using var peReader = new PEReader(fs);
        Assert.True(peReader.HasMetadata);
    }

    // -------------------------------------------------------------------------
    // SignStream — works on MemoryStream copy
    // -------------------------------------------------------------------------

    [Fact]
    public void SignStream_MemoryBacked_ReturnsTrue()
    {
        // Create a temporary file copy so we can open it as a FileStream
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithStrongName.dll");
        using var tempDir = new TempDirectory();
        var targetPath = Path.Combine(tempDir, "Signed.dll");
        File.Copy(sourcePath, targetPath);

        var key = StrongNameKey.FromFile(keyFilePath);
        using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = StreamingStrongNameSigner.SignStream(stream, key);

        Assert.True(result);
    }

    // -------------------------------------------------------------------------
    // Integration — sign an assembly produced by StreamingAssemblyModifier
    // -------------------------------------------------------------------------

    [Fact]
    public void SignFile_AfterModifier_AssemblyIsValid()
    {
        // Use modifier to add public key (metadata rebuild path), then sign
        var sourcePath = Path.Combine(binDirectory, "AssemblyWithNoStrongName.dll");
        using var tempDir = new TempDirectory();
        var modifiedPath = Path.Combine(tempDir, "Modified.dll");

        var key = StrongNameKey.FromFile(keyFilePath);

        using (var modifier = StreamingAssemblyModifier.Open(sourcePath))
        {
            modifier.SetAssemblyPublicKey(key.PublicKey);
            modifier.Save(modifiedPath, key);
        }

        // Should have been signed by the modifier already; verify it's readable
        using var fs = File.OpenRead(modifiedPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();
        var publicKeyBytes = reader.GetBlobBytes(reader.GetAssemblyDefinition().PublicKey);
        Assert.True(publicKeyBytes.Length > 0, "Assembly should have a public key embedded");
    }

    // -------------------------------------------------------------------------
    // Direct tests of ResolveRvaToFileOffset
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveRvaToFileOffset_RvaInFirstSection_ReturnsFileOffset()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var headers = peReader.PEHeaders;

        var firstSection = headers.SectionHeaders[0];
        var rva = firstSection.VirtualAddress;

        var offset = StreamingStrongNameSigner.ResolveRvaToFileOffset(headers, rva);

        Assert.Equal(firstSection.PointerToRawData, offset);
    }

    [Fact]
    public void ResolveRvaToFileOffset_RvaMidSection_ReturnsCorrectOffset()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var headers = peReader.PEHeaders;

        var section = headers.SectionHeaders[0];
        // Pick an RVA 16 bytes into the section
        var rva = section.VirtualAddress + 16;

        var offset = StreamingStrongNameSigner.ResolveRvaToFileOffset(headers, rva);

        Assert.Equal(section.PointerToRawData + 16, offset);
    }

    [Fact]
    public void ResolveRvaToFileOffset_RvaBelowAllSections_ReturnsZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var headers = peReader.PEHeaders;

        // RVA 1 is below any section (sections start at 0x2000 or higher in real PEs)
        var offset = StreamingStrongNameSigner.ResolveRvaToFileOffset(headers, 1);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ResolveRvaToFileOffset_RvaAboveAllSections_ReturnsZero()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var headers = peReader.PEHeaders;

        // Use an RVA that's way beyond the end of the last section
        var lastSection = headers.SectionHeaders[^1];
        var rva = lastSection.VirtualAddress + lastSection.SizeOfRawData + 0x1000;

        var offset = StreamingStrongNameSigner.ResolveRvaToFileOffset(headers, rva);

        Assert.Equal(0, offset);
    }

    // -------------------------------------------------------------------------
    // Direct tests of ComputeStrongNameHashStreaming
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeStrongNameHashStreaming_ExcludesSkipRegions()
    {
        // Build a 200-byte file with known content
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir, "data.bin");
        var data = new byte[200];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        File.WriteAllBytes(path, data);

        const int checksumOffset = 10;
        const int checksumSize = 4;
        const long signatureOffset = 50;
        const int signatureSize = 16;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var actual = StreamingStrongNameSigner.ComputeStrongNameHashStreaming(
            stream, checksumOffset, signatureOffset, signatureSize);

        // Build the expected data by excluding both skip regions, then SHA1
        var expectedData = new List<byte>();
        expectedData.AddRange(data[..checksumOffset]);
        expectedData.AddRange(data[(checksumOffset + checksumSize)..(int)signatureOffset]);
        expectedData.AddRange(data[((int)signatureOffset + signatureSize)..]);
        var expected = SHA1.HashData(expectedData.ToArray());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeStrongNameHashStreaming_SignatureBeforeChecksum_StillSorts()
    {
        // Regions are given in order (checksum, signature) but signature offset < checksum offset
        // The method sorts regions internally, so this should still work
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir, "data.bin");
        var data = new byte[200];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        File.WriteAllBytes(path, data);

        const int checksumOffset = 100;
        const int checksumSize = 4;
        const long signatureOffset = 10;
        const int signatureSize = 16;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var actual = StreamingStrongNameSigner.ComputeStrongNameHashStreaming(
            stream, checksumOffset, signatureOffset, signatureSize);

        var expectedData = new List<byte>();
        expectedData.AddRange(data[..(int)signatureOffset]);
        expectedData.AddRange(data[((int)signatureOffset + signatureSize)..checksumOffset]);
        expectedData.AddRange(data[(checksumOffset + checksumSize)..]);
        var expected = SHA1.HashData(expectedData.ToArray());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeStrongNameHashStreaming_SkipAtStartOfFile()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir, "data.bin");
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        File.WriteAllBytes(path, data);

        // Checksum region at offset 0 — skip first 4 bytes
        // Signature at offset 90 — skip last 10 bytes
        const int checksumOffset = 0;
        const long signatureOffset = 90;
        const int signatureSize = 10;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var actual = StreamingStrongNameSigner.ComputeStrongNameHashStreaming(
            stream, checksumOffset, signatureOffset, signatureSize);

        var expectedData = new List<byte>();
        expectedData.AddRange(data[4..90]);
        var expected = SHA1.HashData(expectedData.ToArray());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeStrongNameHashStreaming_SkipAtEndOfFile()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir, "data.bin");
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        File.WriteAllBytes(path, data);

        const int checksumOffset = 10;
        const int checksumSize = 4;
        // Signature region ends exactly at EOF
        const long signatureOffset = 90;
        const int signatureSize = 10;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var actual = StreamingStrongNameSigner.ComputeStrongNameHashStreaming(
            stream, checksumOffset, signatureOffset, signatureSize);

        var expectedData = new List<byte>();
        expectedData.AddRange(data[..checksumOffset]);
        expectedData.AddRange(data[(checksumOffset + checksumSize)..90]);
        var expected = SHA1.HashData(expectedData.ToArray());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeStrongNameHashStreaming_LargeFile_HandlesBufferBoundaries()
    {
        // File larger than buffer (81920) to exercise the read-in-chunks path
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir, "data.bin");
        var data = new byte[200_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 31 & 0xFF);
        }
        File.WriteAllBytes(path, data);

        // Skip regions that straddle the 81920 buffer boundary
        const int checksumOffset = 81918;
        const int checksumSize = 4;
        const long signatureOffset = 150_000;
        const int signatureSize = 128;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var actual = StreamingStrongNameSigner.ComputeStrongNameHashStreaming(
            stream, checksumOffset, signatureOffset, signatureSize);

        var expectedData = new List<byte>();
        expectedData.AddRange(data[..checksumOffset]);
        expectedData.AddRange(data[(checksumOffset + checksumSize)..(int)signatureOffset]);
        expectedData.AddRange(data[((int)signatureOffset + signatureSize)..]);
        var expected = SHA1.HashData(expectedData.ToArray());

        Assert.Equal(expected, actual);
    }
}
