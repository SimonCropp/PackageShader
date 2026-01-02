/// <summary>
/// Signs assemblies with a strong name using streaming to avoid loading the entire file into memory.
/// Uses System.Reflection.PortableExecutable for header parsing.
/// </summary>
static class StreamingStrongNameSigner
{
    const int BufferSize = 81920; // 80KB buffer
    const int ChecksumOffsetInOptionalHeader = 64;

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
        // Use PEReader to get header information
        stream.Position = 0;
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = peReader.PEHeaders;

        // Check if assembly has strong name signature
        if (headers.CorHeader == null)
        {
            return false;
        }

        var directory = headers.CorHeader.StrongNameSignatureDirectory;
        if (directory.RelativeVirtualAddress == 0 ||
            directory.Size == 0)
        {
            return false;
        }

        // Calculate offsets
        var checksumOffset = headers.PEHeaderStartOffset + ChecksumOffsetInOptionalHeader;
        var signatureOffset = ResolveRvaToFileOffset(headers, directory.RelativeVirtualAddress);
        var signatureSize = directory.Size;

        if (signatureOffset == 0)
        {
            return false;
        }

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

    static long ResolveRvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            if (rva >= section.VirtualAddress &&
                rva < section.VirtualAddress + section.SizeOfRawData)
            {
                return rva - section.VirtualAddress + section.PointerToRawData;
            }
        }

        return 0;
    }

    static byte[] ComputeStrongNameHashStreaming(FileStream stream, int checksumOffset, long signatureOffset, int signatureSize)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[BufferSize];

        stream.Position = 0;
        long position = 0;
        var fileLength = stream.Length;

        // Define regions to skip (checksum and signature)
        var skipRegions = new[]
        {
            (start: checksumOffset, end: checksumOffset + 4),
            (start: signatureOffset, end: signatureOffset + signatureSize)
        };

        // Sort by start position
        Array.Sort(skipRegions, (a, b) => a.start.CompareTo(b.start));

        var skipIndex = 0;

        while (position < fileLength)
        {
            // Find next skip region
            var nextSkipStart = skipIndex < skipRegions.Length ? skipRegions[skipIndex].start : long.MaxValue;
            var nextSkipEnd = skipIndex < skipRegions.Length ? skipRegions[skipIndex].end : long.MaxValue;

            if (position >= nextSkipStart && position < nextSkipEnd)
            {
                // We're in a skip region, move past it
                position = nextSkipEnd;
                stream.Position = position;
                skipIndex++;
                continue;
            }

            // Calculate how much to read (stop at next skip region or EOF)
            var bytesToRead = Math.Min(BufferSize, fileLength - position);
            if (nextSkipStart < long.MaxValue &&
                position + bytesToRead > nextSkipStart)
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
            var read = stream.Read(buffer, 0, (int) bytesToRead);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            position += read;
        }

        return hash.GetHashAndReset();
    }
}