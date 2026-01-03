public class StrongNameKeyTests
{
    static string keyFilePath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");

    [Fact]
    public void FromFile_ParsesPublicExponent()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Standard RSA exponent is 65537 (0x010001)
        Assert.Equal(65537, ToInt(key.Rsa.ExportParameters(false).Exponent!));
    }

    [Fact]
    public void FromBlob_ParsesPublicExponent_Standard65537()
    {
        // Create a minimal CAPI public key blob with exponent 65537 (0x010001)
        // Format: PUBLICKEYBLOB header + RSA1 magic + bitlen + exponent + modulus
        var blob = CreatePublicKeyBlob(exponent: 65537, modulusBits: 2048);

        var key = StrongNameKey.FromBlob(blob);

        Assert.Equal(65537, ToInt(key.Rsa.ExportParameters(false).Exponent!));
    }

    [Fact]
    public void FromBlob_ParsesPublicExponent_SmallValue3()
    {
        // Exponent 3 is sometimes used (though not recommended)
        var blob = CreatePublicKeyBlob(exponent: 3, modulusBits: 2048);

        var key = StrongNameKey.FromBlob(blob);

        Assert.Equal(3, ToInt(key.Rsa.ExportParameters(false).Exponent!));
    }

    [Fact]
    public void FromBlob_ParsesPublicExponent_LargeValue()
    {
        // Test with a larger exponent that uses all 4 bytes: 0x01000101 = 16777473
        var blob = CreatePublicKeyBlob(exponent: 16777473, modulusBits: 2048);

        var key = StrongNameKey.FromBlob(blob);

        Assert.Equal(16777473, ToInt(key.Rsa.ExportParameters(false).Exponent!));
    }

    [Fact]
    public void FromFile_ProducesValidPublicKeyToken()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Public key token should be 8 bytes
        Assert.Equal(8, key.PublicKeyToken.Length);
    }

    [Fact]
    public void FromFile_ProducesNonEmptyPublicKey()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        Assert.True(key.PublicKey.Length > 0);
    }

    // Private key blob parsing tests (test.snk contains a private key)

    [Fact]
    public void FromFile_PrivateKey_CanExportPrivateParameters()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Should be able to export private parameters since test.snk is a private key
        var parameters = key.Rsa.ExportParameters(includePrivateParameters: true);

        Assert.NotNull(parameters.D);
        Assert.NotNull(parameters.P);
        Assert.NotNull(parameters.Q);
    }

    [Fact]
    public void FromFile_PrivateKey_HasValidKeyPair()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // KeyPair should contain the original blob
        Assert.True(key.KeyPair.Length > 0);
    }

    // Error handling tests

    [Fact]
    public void FromBlob_ThrowsOnEmptyBlob() =>
        Assert.Throws<CryptographicException>(() => StrongNameKey.FromBlob([]));

    [Fact]
    public void FromBlob_ThrowsOnTooShortBlob() =>
        Assert.Throws<CryptographicException>(() => StrongNameKey.FromBlob(new byte[5]));

    [Fact]
    public void FromBlob_ThrowsOnInvalidBlobType()
    {
        // Create a blob with invalid type byte (not 0x06 or 0x07)
        var blob = new byte[20];
        blob[0] = 0x99; // Invalid blob type

        Assert.Throws<CryptographicException>(() => StrongNameKey.FromBlob(blob));
    }

    [Fact]
    public void FromBlob_ThrowsOnInvalidPublicKeyMagic()
    {
        // Create a public key blob with wrong magic
        var blob = CreatePublicKeyBlob(65537, 2048);
        // Corrupt the RSA1 magic (at offset 12+8 = 20 in the full blob)
        blob[20] = 0x00;

        Assert.Throws<CryptographicException>(() => StrongNameKey.FromBlob(blob));
    }

    // Round-trip consistency tests

    [Fact]
    public void FromFile_PublicKeyToken_IsConsistentAcrossLoads()
    {
        var key1 = StrongNameKey.FromFile(keyFilePath);
        var key2 = StrongNameKey.FromFile(keyFilePath);

        Assert.Equal(key1.PublicKeyToken, key2.PublicKeyToken);
    }

    [Fact]
    public void FromFile_PublicKey_IsConsistentAcrossLoads()
    {
        var key1 = StrongNameKey.FromFile(keyFilePath);
        var key2 = StrongNameKey.FromFile(keyFilePath);

        Assert.Equal(key1.PublicKey, key2.PublicKey);
    }

    [Fact]
    public void PublicKeyToken_IsDerivedFromPublicKey()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Manually compute the public key token (SHA1 hash, last 8 bytes reversed)
        var hash = SHA1.HashData(key.PublicKey);
        var expectedToken = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            expectedToken[i] = hash[hash.Length - 1 - i];
        }

        Assert.Equal(expectedToken, key.PublicKeyToken);
    }

    [Fact]
    public void FromBlob_ProducesConsistentPublicKeyToken()
    {
        // Create two keys with same parameters, verify tokens match
        var blob1 = CreatePublicKeyBlob(65537, 2048);
        var key1 = StrongNameKey.FromBlob(blob1);

        // Reload from same blob
        var key2 = StrongNameKey.FromBlob(blob1);

        Assert.Equal(key1.PublicKeyToken, key2.PublicKeyToken);
    }

    // Public key token format verification

    [Fact]
    public void PublicKeyToken_IsValidHexFormat()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Token should be exactly 8 bytes
        Assert.Equal(8, key.PublicKeyToken.Length);

        // All bytes should be valid (no specific constraint, but verify it's populated)
        Assert.False(key.PublicKeyToken.All(b => b == 0), "Token should not be all zeros");
    }

    [Fact]
    public void PublicKey_ContainsExpectedHeader()
    {
        var key = StrongNameKey.FromFile(keyFilePath);

        // Public key should start with the .NET header
        // ALG_ID for signature (CALG_RSA_SIGN = 0x00002400)
        Assert.Equal(0x00, key.PublicKey[0]);
        Assert.Equal(0x24, key.PublicKey[1]);
        Assert.Equal(0x00, key.PublicKey[2]);
        Assert.Equal(0x00, key.PublicKey[3]);

        // ALG_ID for hash (CALG_SHA1 = 0x00008004)
        Assert.Equal(0x04, key.PublicKey[4]);
        Assert.Equal(0x80, key.PublicKey[5]);
        Assert.Equal(0x00, key.PublicKey[6]);
        Assert.Equal(0x00, key.PublicKey[7]);
    }

    static int ToInt(byte[] bytes) =>
        bytes.Aggregate(0, (current, t) => (current << 8) | t);

    static byte[] CreatePublicKeyBlob(int exponent, int modulusBits)
    {
        // Create an RSA key with the specified exponent
        using var rsa = RSA.Create(modulusBits);
        var parameters = rsa.ExportParameters(false);

        // Convert exponent to bytes (big-endian, trimmed)
        var expBytes = new List<byte>();
        var exp = exponent;
        while (exp > 0)
        {
            expBytes.Insert(0, (byte)(exp & 0xFF));
            exp >>= 8;
        }
        parameters.Exponent = expBytes.ToArray();

        rsa.ImportParameters(parameters);

        // Build CAPI public key blob
        var modulus = parameters.Modulus!;
        var keyLength = modulus.Length;

        // CAPI blob structure
        var capiBlob = new byte[20 + keyLength];
        capiBlob[0] = 0x06;  // PUBLICKEYBLOB
        capiBlob[1] = 0x02;  // Version
        capiBlob[5] = 0x24;  // CALG_RSA_SIGN (00 24 00 00)
        capiBlob[8] = 0x52;  // "RSA1" magic
        capiBlob[9] = 0x53;
        capiBlob[10] = 0x41;
        capiBlob[11] = 0x31;

        // Bit length (little-endian)
        var bitLen = keyLength * 8;
        capiBlob[12] = (byte)(bitLen & 0xFF);
        capiBlob[13] = (byte)((bitLen >> 8) & 0xFF);
        capiBlob[14] = (byte)((bitLen >> 16) & 0xFF);
        capiBlob[15] = (byte)((bitLen >> 24) & 0xFF);

        // Public exponent (4 bytes, little-endian)
        var expBytesArray = parameters.Exponent!;
        for (var i = 0; i < expBytesArray.Length && i < 4; i++)
        {
            capiBlob[16 + i] = expBytesArray[expBytesArray.Length - 1 - i];
        }

        // Modulus (little-endian)
        var modulusCopy = (byte[])modulus.Clone();
        Array.Reverse(modulusCopy);
        Array.Copy(modulusCopy, 0, capiBlob, 20, keyLength);

        // Build full public key blob with .NET header
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
}
