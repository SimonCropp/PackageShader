namespace Alias.Lib.Signing;

/// <summary>
/// Signs assemblies with a strong name using streaming to avoid loading the entire file into memory.
/// </summary>
public static class StreamingStrongNameSigner
{
    private const int BufferSize = 81920; // 80KB buffer

    /// <summary>
    /// Signs a PE file in place using streaming hash computation.
    /// </summary>
    /// <returns>True if signing was successful, false if assembly has no signature placeholder.</returns>
    public static bool SignFile(string filePath, StrongNameKey key)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return SignStream(stream, key);
    }

    /// <summary>
    /// Signs a PE stream in place using streaming hash computation.
    /// </summary>
    /// <returns>True if signing was successful, false if assembly has no signature placeholder.</returns>
    public static bool SignStream(FileStream stream, StrongNameKey key)
    {
        // Find strong name signature location by reading header
        var (signatureOffset, signatureSize, checksumOffset) = FindStrongNameSignature(stream);

        // Assembly doesn't have a strong name signature placeholder - skip signing
        if (signatureOffset == 0 || signatureSize == 0)
            return false;

        // Zero out the signature area
        stream.Position = signatureOffset;
        var zeros = new byte[signatureSize];
        stream.Write(zeros, 0, zeros.Length);
        stream.Flush();

        // Compute hash using streaming
        var hash = ComputeStrongNameHashStreaming(stream, checksumOffset, signatureOffset, signatureSize);

        // Sign with RSA
        var signature = key.Rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        // Reverse for little-endian
        Array.Reverse(signature);

        // Write signature
        stream.Position = signatureOffset;
        stream.Write(signature, 0, Math.Min(signature.Length, signatureSize));
        stream.Flush();

        return true;
    }

    private static (long signatureOffset, int signatureSize, int checksumOffset) FindStrongNameSignature(FileStream stream)
    {
        var buffer = new byte[512]; // Enough for headers

        // Read DOS header
        stream.Position = 0;
        if (stream.Read(buffer, 0, 64) < 64)
            return (0, 0, 0);

        var peOffset = BitConverter.ToInt32(buffer, 60);

        // Read PE header
        stream.Position = peOffset;
        if (stream.Read(buffer, 0, 256) < 256)
            return (0, 0, 0);

        // Check PE signature
        if (BitConverter.ToUInt32(buffer, 0) != 0x00004550)
            return (0, 0, 0);

        var optionalHeaderOffset = 24;
        var magic = BitConverter.ToUInt16(buffer, optionalHeaderOffset);
        var isPE64 = magic == 0x20b;

        // Checksum offset in file
        var checksumOffset = peOffset + optionalHeaderOffset + 64;

        // Find CLI header data directory (index 14)
        var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);
        var cliHeaderDirOffset = dataDirectoriesOffset + 14 * 8;

        if (cliHeaderDirOffset + 8 > buffer.Length)
            return (0, 0, 0);

        var cliHeaderRva = BitConverter.ToUInt32(buffer, cliHeaderDirOffset);
        if (cliHeaderRva == 0)
            return (0, 0, 0);

        // Read section headers to resolve RVA
        var numberOfSections = BitConverter.ToUInt16(buffer, 6);
        var optionalHeaderSize = BitConverter.ToUInt16(buffer, 20);
        var sectionHeadersOffset = peOffset + 24 + optionalHeaderSize;

        // Read section headers
        stream.Position = sectionHeadersOffset;
        var sectionData = new byte[numberOfSections * 40];
        if (stream.Read(sectionData, 0, sectionData.Length) < sectionData.Length)
            return (0, 0, 0);

        // Resolve CLI header RVA to file offset
        var cliHeaderOffset = ResolveRvaFromSections(sectionData, numberOfSections, cliHeaderRva);
        if (cliHeaderOffset == 0)
            return (0, 0, 0);

        // Read CLI header (just the parts we need)
        stream.Position = cliHeaderOffset;
        var cliHeader = new byte[48];
        if (stream.Read(cliHeader, 0, 48) < 48)
            return (0, 0, 0);

        // Strong name signature is at offset 32 in CLI header
        var snRva = BitConverter.ToUInt32(cliHeader, 32);
        var snSize = BitConverter.ToUInt32(cliHeader, 36);

        if (snRva == 0 || snSize == 0)
            return (0, 0, 0);

        var snOffset = ResolveRvaFromSections(sectionData, numberOfSections, snRva);
        return (snOffset, (int)snSize, checksumOffset);
    }

    private static long ResolveRvaFromSections(byte[] sectionData, int numberOfSections, uint rva)
    {
        for (int i = 0; i < numberOfSections; i++)
        {
            var offset = i * 40;
            var virtualAddress = BitConverter.ToUInt32(sectionData, offset + 12);
            var sizeOfRawData = BitConverter.ToUInt32(sectionData, offset + 16);
            var pointerToRawData = BitConverter.ToUInt32(sectionData, offset + 20);

            if (rva >= virtualAddress && rva < virtualAddress + sizeOfRawData)
            {
                return rva - virtualAddress + pointerToRawData;
            }
        }
        return 0;
    }

    private static byte[] ComputeStrongNameHashStreaming(FileStream stream, int checksumOffset, long signatureOffset, int signatureSize)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[BufferSize];

        stream.Position = 0;
        long position = 0;
        var fileLength = stream.Length;

        // Define regions to skip
        var skipRegions = new[]
        {
            (start: checksumOffset, end: checksumOffset + 4),
            (start: signatureOffset, end: signatureOffset + signatureSize)
        };

        // Sort by start position
        Array.Sort(skipRegions, (a, b) => a.start.CompareTo(b.start));

        int skipIndex = 0;

        while (position < fileLength)
        {
            // Find next skip region
            long nextSkipStart = skipIndex < skipRegions.Length ? skipRegions[skipIndex].start : long.MaxValue;
            long nextSkipEnd = skipIndex < skipRegions.Length ? skipRegions[skipIndex].end : long.MaxValue;

            if (position >= nextSkipStart && position < nextSkipEnd)
            {
                // We're in a skip region, move past it
                position = nextSkipEnd;
                stream.Position = position;
                skipIndex++;
                continue;
            }

            // Calculate how much to read (stop at next skip region or EOF)
            long bytesToRead = Math.Min(BufferSize, fileLength - position);
            if (nextSkipStart < long.MaxValue && position + bytesToRead > nextSkipStart)
            {
                bytesToRead = nextSkipStart - position;
            }

            if (bytesToRead <= 0)
            {
                if (skipIndex < skipRegions.Length)
                {
                    position = skipRegions[skipIndex].end;
                    stream.Position = position;
                    skipIndex++;
                    continue;
                }
                break;
            }

            // Read and hash
            var read = stream.Read(buffer, 0, (int)bytesToRead);
            if (read == 0) break;

            hash.AppendData(buffer, 0, read);
            position += read;
        }

        return hash.GetHashAndReset();
    }
}
