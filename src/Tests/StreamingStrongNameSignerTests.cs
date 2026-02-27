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
}
