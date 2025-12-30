using System.Security.Cryptography;

namespace Alias.Lib.Signing;

/// <summary>
/// Represents a strong name key loaded from an SNK file.
/// </summary>
public sealed class StrongNameKey
{
    /// <summary>
    /// The RSA key for signing.
    /// </summary>
    public RSA Rsa { get; }

    /// <summary>
    /// The public key blob (for assembly identity).
    /// </summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// The public key token (8-byte hash of public key).
    /// </summary>
    public byte[] PublicKeyToken { get; }

    private StrongNameKey(RSA rsa, byte[] publicKey, byte[] publicKeyToken)
    {
        Rsa = rsa;
        PublicKey = publicKey;
        PublicKeyToken = publicKeyToken;
    }

    /// <summary>
    /// Loads a strong name key from an SNK file.
    /// </summary>
    public static StrongNameKey FromFile(string path)
    {
        var blob = File.ReadAllBytes(path);
        return FromBlob(blob);
    }

    /// <summary>
    /// Loads a strong name key from a key blob.
    /// </summary>
    public static StrongNameKey FromBlob(byte[] blob)
    {
        var rsa = FromCapiKeyBlob(blob);
        var publicKey = BuildPublicKeyBlob(rsa);
        var publicKeyToken = ComputePublicKeyToken(publicKey);
        return new StrongNameKey(rsa, publicKey, publicKeyToken);
    }

    /// <summary>
    /// Gets the public key as a hex string for InternalsVisibleTo attributes.
    /// </summary>
    public string PublicKeyString => Convert.ToHexString(PublicKey);

    private static RSA FromCapiKeyBlob(byte[] blob)
    {
        if (blob == null || blob.Length < 12)
            throw new CryptographicException("Invalid key blob");

        // Check blob type
        return blob[0] switch
        {
            0x00 when blob.Length > 12 && blob[12] == 0x06 => FromCapiPublicKeyBlob(blob, 12),
            0x06 => FromCapiPublicKeyBlob(blob, 0),
            0x07 => FromCapiPrivateKeyBlob(blob, 0),
            _ => throw new CryptographicException("Unknown blob format")
        };
    }

    private static RSA FromCapiPrivateKeyBlob(byte[] blob, int offset)
    {
        // Validate header: PRIVATEKEYBLOB (0x07), Version (0x02), Reserved (0x0000)
        if (blob[offset] != 0x07 || blob[offset + 1] != 0x02)
            throw new CryptographicException("Invalid private key blob header");

        // Check magic: RSA2
        var magic = BitConverter.ToUInt32(blob, offset + 8);
        if (magic != 0x32415352) // "RSA2" in little-endian
            throw new CryptographicException("Invalid RSA2 magic");

        // Read bit length
        var bitLen = BitConverter.ToInt32(blob, offset + 12);
        var byteLen = bitLen / 8;
        var halfLen = byteLen / 2;

        var parameters = new RSAParameters();

        // Public exponent (4 bytes, little-endian)
        var exp = new byte[4];
        Array.Copy(blob, offset + 16, exp, 0, 4);
        Array.Reverse(exp);
        parameters.Exponent = TrimLeadingZeros(exp);

        var pos = offset + 20;

        // Modulus
        parameters.Modulus = ReadReversed(blob, ref pos, byteLen);

        // P (prime1)
        parameters.P = ReadReversed(blob, ref pos, halfLen);

        // Q (prime2)
        parameters.Q = ReadReversed(blob, ref pos, halfLen);

        // DP (exponent1)
        parameters.DP = ReadReversed(blob, ref pos, halfLen);

        // DQ (exponent2)
        parameters.DQ = ReadReversed(blob, ref pos, halfLen);

        // InverseQ (coefficient)
        parameters.InverseQ = ReadReversed(blob, ref pos, halfLen);

        // D (private exponent)
        if (pos + byteLen <= blob.Length)
        {
            parameters.D = ReadReversed(blob, ref pos, byteLen);
        }

        var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa;
    }

    private static RSA FromCapiPublicKeyBlob(byte[] blob, int offset)
    {
        // Validate header: PUBLICKEYBLOB (0x06), Version (0x02)
        if (blob[offset] != 0x06 || blob[offset + 1] != 0x02)
            throw new CryptographicException("Invalid public key blob header");

        // Check magic: RSA1
        var magic = BitConverter.ToUInt32(blob, offset + 8);
        if (magic != 0x31415352) // "RSA1" in little-endian
            throw new CryptographicException("Invalid RSA1 magic");

        // Read bit length
        var bitLen = BitConverter.ToInt32(blob, offset + 12);
        var byteLen = bitLen / 8;

        var parameters = new RSAParameters();

        // Public exponent (stored as 4 bytes, little-endian)
        parameters.Exponent = new byte[3];
        parameters.Exponent[0] = blob[offset + 18];
        parameters.Exponent[1] = blob[offset + 17];
        parameters.Exponent[2] = blob[offset + 16];
        parameters.Exponent = TrimLeadingZeros(parameters.Exponent);

        var pos = offset + 20;

        // Modulus
        parameters.Modulus = ReadReversed(blob, ref pos, byteLen);

        var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa;
    }

    private static byte[] ReadReversed(byte[] data, ref int pos, int length)
    {
        var result = new byte[length];
        Array.Copy(data, pos, result, 0, length);
        Array.Reverse(result);
        pos += length;
        return result;
    }

    private static byte[] TrimLeadingZeros(byte[] data)
    {
        int start = 0;
        while (start < data.Length - 1 && data[start] == 0)
            start++;

        if (start == 0)
            return data;

        var result = new byte[data.Length - start];
        Array.Copy(data, start, result, 0, result.Length);
        return result;
    }

    private static byte[] BuildPublicKeyBlob(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        var keyLength = parameters.Modulus!.Length;

        // Public key blob format:
        // [0-3]   ALG_ID for signature (CALG_RSA_SIGN = 0x00002400)
        // [4-7]   ALG_ID for hash (CALG_SHA1 = 0x00008004)
        // [8-11]  Length of public key data
        // [12+]   CAPI public key blob

        var capiBlob = new byte[20 + keyLength];
        capiBlob[0] = 0x06;  // PUBLICKEYBLOB
        capiBlob[1] = 0x02;  // Version
        capiBlob[5] = 0x24;  // CALG_RSA_SIGN (00 24 00 00)
        capiBlob[8] = 0x52;  // "RSA1" magic
        capiBlob[9] = 0x53;
        capiBlob[10] = 0x41;
        capiBlob[11] = 0x31;

        // Bit length
        var bitLen = keyLength * 8;
        capiBlob[12] = (byte)(bitLen & 0xFF);
        capiBlob[13] = (byte)((bitLen >> 8) & 0xFF);
        capiBlob[14] = (byte)((bitLen >> 16) & 0xFF);
        capiBlob[15] = (byte)((bitLen >> 24) & 0xFF);

        // Public exponent (little-endian)
        var exp = parameters.Exponent!;
        for (int i = 0; i < exp.Length && i < 4; i++)
            capiBlob[16 + i] = exp[exp.Length - 1 - i];

        // Modulus (little-endian)
        var modulus = (byte[])parameters.Modulus.Clone();
        Array.Reverse(modulus);
        Array.Copy(modulus, 0, capiBlob, 20, keyLength);

        // Build full public key blob with header
        var publicKey = new byte[12 + capiBlob.Length];

        // ALG_ID for signature (CALG_RSA_SIGN)
        publicKey[0] = 0x00;
        publicKey[1] = 0x24;
        publicKey[2] = 0x00;
        publicKey[3] = 0x00;

        // ALG_ID for hash (CALG_SHA1)
        publicKey[4] = 0x04;
        publicKey[5] = 0x80;
        publicKey[6] = 0x00;
        publicKey[7] = 0x00;

        // Length of public key blob
        var blobLen = capiBlob.Length;
        publicKey[8] = (byte)(blobLen & 0xFF);
        publicKey[9] = (byte)((blobLen >> 8) & 0xFF);
        publicKey[10] = (byte)((blobLen >> 16) & 0xFF);
        publicKey[11] = (byte)((blobLen >> 24) & 0xFF);

        // Copy CAPI blob
        Array.Copy(capiBlob, 0, publicKey, 12, capiBlob.Length);

        return publicKey;
    }

    private static byte[] ComputePublicKeyToken(byte[] publicKey)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);

        // Take last 8 bytes, reversed
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];

        return token;
    }
}
