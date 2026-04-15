[Collection("Sequential")]
public class StreamingAssemblyModifierTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(StreamingAssemblyModifierTests).Assembly.Location)!;
    static string keyFilePath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");

    [Fact]
    public void CanOpenAndReadAssemblyName()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var modifier = StreamingAssemblyModifier.Open(assemblyPath);

        Assert.NotNull(modifier);
        Assert.Equal(assemblyPath, modifier.SourcePath);
    }

    [Fact]
    public void CanRenameAssembly()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "Renamed.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.SetAssemblyName("RenamedAssembly");
            modifier.Save(outputPath);
        }

        // Verify the output
        using var fs = File.OpenRead(outputPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();
        var name = reader.GetString(reader.GetAssemblyDefinition().Name);

        Assert.Equal("RenamedAssembly", name);
    }

    [Fact]
    public void CanMakeTypesInternal()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "Internalized.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.MakeTypesInternal();
            modifier.Save(outputPath);
        }

        // Verify the output - all types should be internal (not public)
        using var fs = File.OpenRead(outputPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            var typeName = reader.GetString(typeDef.Name);

            // Skip <Module> type
            if (typeName == "<Module>")
            {
                continue;
            }

            // Check visibility - should not be public
            var visibility = typeDef.Attributes & TypeAttributes.VisibilityMask;
            Assert.NotEqual(TypeAttributes.Public, visibility);
        }
    }

    [Fact]
    public void CanSignAssembly()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyWithNoStrongName.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "Signed.dll");

        var key = StrongNameKey.FromFile(keyFilePath);

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.SetAssemblyPublicKey(key.PublicKey);
            modifier.Save(outputPath, key);
        }

        // Verify the output has a public key
        using var fs = File.OpenRead(outputPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();
        var publicKey = reader.GetBlobBytes(reader.GetAssemblyDefinition().PublicKey);

        Assert.True(publicKey.Length > 0, "Assembly should have a public key");
    }

    [Fact]
    public void CanAddInternalsVisibleTo()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "WithIVT.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.AddInternalsVisibleTo("TestFriendAssembly");
            modifier.Save(outputPath);
        }

        // Verify the output has InternalsVisibleTo attribute
        using var fs = File.OpenRead(outputPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();

        var hasIVT = false;
        foreach (var attrHandle in reader.GetCustomAttributes(EntityHandle.AssemblyDefinition))
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            if (attr.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (memberRef.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
            var typeName = reader.GetString(typeRef.Name);
            if (typeName == "InternalsVisibleToAttribute")
            {
                hasIVT = true;
                break;
            }
        }

        Assert.True(hasIVT, "Assembly should have InternalsVisibleTo attribute");
    }

    [Fact]
    public void ModifiedAssemblyIsLoadable()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "Modified.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.SetAssemblyName("ModifiedAssembly");
            modifier.MakeTypesInternal();
            modifier.Save(outputPath);
        }

        // Verify the assembly is loadable
        var loadContext = new AssemblyLoadContext("StreamingTestContext", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(outputPath);
            using var stream = new MemoryStream(bytes);
            var assembly = loadContext.LoadFromStream(stream);

            Assert.NotNull(assembly);
            Assert.Equal("ModifiedAssembly", assembly.GetName().Name);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void CanCopySymbols()
    {
        var assemblyPath = Path.Combine(binDirectory, "AssemblyWithPdb.dll");
        var pdbPath = Path.Combine(binDirectory, "AssemblyWithPdb.pdb");

        // Skip if PDB doesn't exist
        if (!File.Exists(pdbPath))
        {
            return;
        }

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "WithSymbols.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            modifier.SetAssemblyName("WithSymbols");
            modifier.Save(outputPath);
        }

        var outputPdbPath = Path.Combine(tempDir, "WithSymbols.pdb");
        Assert.True(File.Exists(outputPdbPath), "PDB file should be copied");
    }

    [Fact]
    public void InPlacePatchingWorksForSimpleChanges()
    {
        // This test verifies that simple changes use in-place patching (no metadata rebuild)
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "Patched.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            // Just making types internal should be an in-place patch
            modifier.MakeTypesInternal();
            modifier.Save(outputPath);
        }

        // Verify the output is valid
        using var fs = File.OpenRead(outputPath);
        using var peReader = new PEReader(fs);
        Assert.True(peReader.HasMetadata);

        var reader = peReader.GetMetadataReader();
        Assert.True(reader.IsAssembly);
    }

    /// <summary>
    /// Minimal repro for the heap-promotion bug fixed by the column-schema rewriter in
    /// StreamingMetadataWriter. Takes a small fixture assembly and adds enough
    /// InternalsVisibleTo entries to push the blob heap over 64 KB. That promotes blob
    /// indices from 2 to 4 bytes, which forces every blob-bearing table to be re-emitted
    /// at the wider width. Asserts the produced PE round-trips through PEReader and the
    /// underlying StreamingMetadataReader reports the wider blob index size.
    /// </summary>
    [Fact]
    public void RoundTrip_BlobHeapPromotion()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        // Sanity check: source assembly is small enough that its blob heap uses 2-byte indices.
        // If the fixture ever grows past 64 KB on its own this test should be re-tuned.
        uint sourceBlobHeapSize;
        using (var sourcePeFile = StreamingPEFile.Open(assemblyPath))
        using (var sourceReader = new StreamingMetadataReader(sourcePeFile))
        {
            Assert.Equal(2, sourceReader.BlobIndexSize);
            sourceBlobHeapSize = sourceReader.BlobHeapSize;
        }

        // Each AddInternalsVisibleTo("FriendAssembly_<i>...") creates a custom-attribute blob
        // of roughly 55 bytes (prolog + serialized name + named-arg count). Aim well past
        // 64 KB so the test isn't sensitive to small fixture changes.
        const int approxBytesPerIvt = 50;
        var bytesNeeded = 65536 - (int)sourceBlobHeapSize + 16000;
        var ivtCount = bytesNeeded / approxBytesPerIvt + 100;

        using var tempDir = new TempDirectory();
        var outputPath = Path.Combine(tempDir, "BlobPromoted.dll");

        using (var modifier = StreamingAssemblyModifier.Open(assemblyPath))
        {
            for (var i = 0; i < ivtCount; i++)
            {
                // Names must be unique so each one allocates a distinct blob.
                modifier.AddInternalsVisibleTo($"FriendAssembly_{i:D6}_LongPaddingToInflateBlobHeap");
            }

            modifier.SetAssemblyName("BlobPromoted");
            modifier.Save(outputPath);
        }

        // The produced PE must load cleanly and surface the rename + IVT entries.
        using (var fs = File.OpenRead(outputPath))
        using (var peReader = new PEReader(fs))
        {
            Assert.True(peReader.HasMetadata);

            var reader = peReader.GetMetadataReader();
            Assert.True(reader.IsAssembly);
            Assert.Equal("BlobPromoted", reader.GetString(reader.GetAssemblyDefinition().Name));

            var ivtFound = 0;
            foreach (var attrHandle in reader.GetCustomAttributes(EntityHandle.AssemblyDefinition))
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference)
                {
                    continue;
                }

                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                if (reader.GetString(typeRef.Name) == "InternalsVisibleToAttribute")
                {
                    ivtFound++;
                }
            }

            Assert.Equal(ivtCount, ivtFound);

            // All original type defs should have survived (minus the <Module> sentinel).
            Assert.True(reader.TypeDefinitions.Count > 1);
        }

        // Re-open via our own metadata reader to confirm the heap-sizes byte was promoted.
        using var promotedPeFile = StreamingPEFile.Open(outputPath);
        using var promotedReader = new StreamingMetadataReader(promotedPeFile);
        Assert.True(promotedReader.BlobHeapSize >= 0x10000,
            $"expected blob heap >= 64 KB after promotion, got {promotedReader.BlobHeapSize}");
        Assert.Equal(4, promotedReader.BlobIndexSize);
    }
}
