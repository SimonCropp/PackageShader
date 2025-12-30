namespace Alias.Lib.Signing;

/// <summary>
/// Signs assemblies with a strong name.
/// </summary>
public static class StrongNameSigner
{
    /// <summary>
    /// Signs a PE file in place.
    /// </summary>
    public static void Sign(string path, StrongNameKey key)
    {
        var data = File.ReadAllBytes(path);
        Sign(data, key);
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Signs PE data in place.
    /// </summary>
    /// <returns>True if signing was successful, false if assembly has no signature placeholder.</returns>
    public static bool Sign(byte[] data, StrongNameKey key)
    {
        // Find strong name signature location
        var (signatureOffset, signatureSize) = FindStrongNameSignature(data);

        // Assembly doesn't have a strong name signature placeholder - skip signing
        if (signatureOffset == 0 || signatureSize == 0)
            return false;

        // Zero out the signature area
        for (int i = 0; i < signatureSize; i++)
            data[signatureOffset + i] = 0;

        // Compute hash
        var hash = ComputeStrongNameHash(data, signatureOffset, signatureSize);

        // Sign with RSA
        using var sha1 = SHA1.Create();
        var signature = key.Rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        // Reverse for little-endian
        Array.Reverse(signature);

        // Write signature
        Array.Copy(signature, 0, data, signatureOffset, Math.Min(signature.Length, signatureSize));

        return true;
    }

    private static (int offset, int size) FindStrongNameSignature(byte[] data)
    {
        // Read PE header offset
        if (data.Length < 64)
            return (0, 0);

        var peOffset = BitConverter.ToInt32(data, 60);
        if (peOffset + 24 > data.Length)
            return (0, 0);

        // Skip to optional header
        var optionalHeaderOffset = peOffset + 24;
        var magic = BitConverter.ToUInt16(data, optionalHeaderOffset);
        var isPE64 = magic == 0x20b;

        // Find CLI header data directory (index 14)
        // Data directories start at offset 96 (PE32) or 112 (PE64) from optional header
        var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);
        var cliHeaderDirOffset = dataDirectoriesOffset + 14 * 8;

        if (cliHeaderDirOffset + 8 > data.Length)
            return (0, 0);

        var cliHeaderRva = BitConverter.ToUInt32(data, cliHeaderDirOffset);
        if (cliHeaderRva == 0)
            return (0, 0);

        // Resolve CLI header file offset
        var cliHeaderOffset = ResolveRva(data, peOffset, cliHeaderRva);
        if (cliHeaderOffset == 0 || cliHeaderOffset + 48 > data.Length)
            return (0, 0);

        // Strong name signature is at CLI header offset 32
        var snRva = BitConverter.ToUInt32(data, (int)cliHeaderOffset + 32);
        var snSize = BitConverter.ToUInt32(data, (int)cliHeaderOffset + 36);

        if (snRva == 0 || snSize == 0)
            return (0, 0);

        var snOffset = ResolveRva(data, peOffset, snRva);
        return ((int)snOffset, (int)snSize);
    }

    private static uint ResolveRva(byte[] data, int peOffset, uint rva)
    {
        // Read number of sections
        var numberOfSections = BitConverter.ToUInt16(data, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(data, peOffset + 20);
        var sectionHeadersOffset = peOffset + 24 + optionalHeaderSize;

        for (int i = 0; i < numberOfSections; i++)
        {
            var sectionOffset = sectionHeadersOffset + i * 40;
            if (sectionOffset + 40 > data.Length)
                break;

            var virtualAddress = BitConverter.ToUInt32(data, sectionOffset + 12);
            var sizeOfRawData = BitConverter.ToUInt32(data, sectionOffset + 16);
            var pointerToRawData = BitConverter.ToUInt32(data, sectionOffset + 20);

            if (rva >= virtualAddress && rva < virtualAddress + sizeOfRawData)
            {
                return rva - virtualAddress + pointerToRawData;
            }
        }

        return 0;
    }

    private static byte[] ComputeStrongNameHash(byte[] data, int signatureOffset, int signatureSize)
    {
        using var sha1 = SHA1.Create();

        // Find checksum location in PE header
        var peOffset = BitConverter.ToInt32(data, 60);
        var checksumOffset = peOffset + 88; // Checksum is at offset 64 in optional header

        // Hash in parts, skipping checksum and signature
        // Part 1: Start to checksum
        sha1.TransformBlock(data, 0, checksumOffset, null, 0);

        // Part 2: Skip checksum (4 bytes), continue to signature
        var afterChecksum = checksumOffset + 4;
        var beforeSignature = signatureOffset;
        if (beforeSignature > afterChecksum)
        {
            sha1.TransformBlock(data, afterChecksum, beforeSignature - afterChecksum, null, 0);
        }

        // Part 3: Skip signature, hash remainder
        var afterSignature = signatureOffset + signatureSize;
        if (afterSignature < data.Length)
        {
            sha1.TransformFinalBlock(data, afterSignature, data.Length - afterSignature);
        }
        else
        {
            sha1.TransformFinalBlock([], 0, 0);
        }

        return sha1.Hash!;
    }
}
