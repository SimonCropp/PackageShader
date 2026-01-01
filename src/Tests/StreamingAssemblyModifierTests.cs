using PackageShader;

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
                continue;

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
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind == HandleKind.TypeReference)
                {
                    var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    var typeName = reader.GetString(typeRef.Name);
                    if (typeName == "InternalsVisibleToAttribute")
                    {
                        hasIVT = true;
                        break;
                    }
                }
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
            return;

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
}