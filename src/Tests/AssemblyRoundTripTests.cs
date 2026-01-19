using Argon;

[Collection("Sequential")]
public class AssemblyRoundTripTests
{
    [Theory]
    [MemberData(nameof(GetAssemblyScenarios))]
    public async Task RoundTripAssembly(string targetFramework, bool strongNamed, SymbolType symbolType)
    {
        using var tempDir = new TempDirectory();
        var scenariosDir = Path.Combine(tempDir, "Scenarios");
        Directory.CreateDirectory(scenariosDir);

        var name = $"{targetFramework.Replace(".", "")}_{(strongNamed ? "StrongNamed" : "NoStrongName")}_{symbolType}";

        TestAssembly assembly;
        try
        {
            assembly = await CreateAssembly(scenariosDir, name, targetFramework, strongNamed, symbolType);
        }
        catch (Exception)
        {
            // Skip assemblies that fail to build (e.g., net48 on Linux, netstandard without SDK)
            return;
        }

        var result = await PerformRoundTrip(assembly, tempDir);

        // Verify the result
        await Verify(result)
            .UseDirectory("Snapshots")
            .UseParameters(targetFramework, strongNamed, symbolType);
    }

    public static IEnumerable<object[]> GetAssemblyScenarios()
    {
        var frameworks = new[] { "net8.0", "net9.0", "net10.0", "net48", "netstandard2.0", "netstandard2.1" };
        var strongNameOptions = new[] { true, false };
        var symbolTypes = new[] { SymbolType.Embedded, SymbolType.External, SymbolType.None };

        foreach (var framework in frameworks)
        {
            foreach (var strongNamed in strongNameOptions)
            {
                foreach (var symbolType in symbolTypes)
                {
                    yield return new object[] { framework, strongNamed, symbolType };
                }
            }
        }
    }

    static async Task<TestAssembly> CreateAssembly(
        string baseDir,
        string name,
        string targetFramework,
        bool strongNamed,
        SymbolType symbolType)
    {
        var categoryDir = Path.Combine(baseDir, targetFramework);
        Directory.CreateDirectory(categoryDir);

        var projectDir = Path.Combine(categoryDir, name);
        Directory.CreateDirectory(projectDir);

        // Create minimal C# source
        var sourceCode = $$"""
namespace {{name}};

public class TestClass
{
    public string GetMessage() => "Hello from {{name}}";

    public int Add(int a, int b) => a + b;

    public void ThrowException()
    {
        throw new System.InvalidOperationException("Test exception");
    }
}

internal class InternalClass
{
    internal string InternalMethod() => "Internal";
}
""";

        await File.WriteAllTextAsync(Path.Combine(projectDir, "Class.cs"), sourceCode);

        // Create key file if strong named
        string? keyFile = null;
        if (strongNamed)
        {
            keyFile = Path.Combine(projectDir, "key.snk");
            await CreateStrongNameKey(keyFile);
        }

        // Determine debug type
        var debugType = symbolType switch
        {
            SymbolType.Embedded => "embedded",
            SymbolType.External => "portable",
            SymbolType.None => "none",
            _ => "portable"
        };

        // Create project file
        var projectContent = $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{{targetFramework}}</TargetFramework>
    <DebugType>{{debugType}}</DebugType>
    <DebugSymbols>{{(symbolType != SymbolType.None ? "true" : "false")}}</DebugSymbols>
{{(strongNamed ? "    <SignAssembly>true</SignAssembly>\n    <AssemblyOriginatorKeyFile>key.snk</AssemblyOriginatorKeyFile>" : "")}}
  </PropertyGroup>
</Project>
""";

        var projectPath = Path.Combine(projectDir, $"{name}.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent);

        // Build the project
        var result = await Cli.Wrap("dotnet")
            .WithArguments(["build", projectPath, "-c", "Release", "--nologo", "-v", "quiet"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to build {name}: {result.StandardError}");
        }

        var outputPath = Path.Combine(projectDir, "bin", "Release", targetFramework, $"{name}.dll");
        if (!File.Exists(outputPath))
        {
            throw new Exception($"Assembly not found at {outputPath}");
        }

        // Copy assembly to a cleaner location
        var finalDir = Path.Combine(categoryDir, "assemblies");
        Directory.CreateDirectory(finalDir);
        var finalPath = Path.Combine(finalDir, $"{name}.dll");
        File.Copy(outputPath, finalPath, true);

        // Copy PDB if external
        if (symbolType == SymbolType.External)
        {
            var pdbPath = Path.Combine(projectDir, "bin", "Release", targetFramework, $"{name}.pdb");
            if (File.Exists(pdbPath))
            {
                File.Copy(pdbPath, Path.Combine(finalDir, $"{name}.pdb"), true);
            }
        }

        // Delete the project directory to clean up
        Directory.Delete(projectDir, true);

        return new TestAssembly
        {
            Name = name,
            Path = finalPath,
            TargetFramework = targetFramework,
            IsStrongNamed = strongNamed,
            SymbolType = symbolType
        };
    }

    static Task CreateStrongNameKey(string keyPath)
    {
        // Copy from existing test key
        var testKeyPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
        if (!File.Exists(testKeyPath))
        {
            throw new FileNotFoundException($"Test key file not found at {testKeyPath}");
        }

        File.Copy(testKeyPath, keyPath, true);
        return Task.CompletedTask;
    }

    static async Task<AssemblyRoundTripResult> PerformRoundTrip(TestAssembly assembly, string tempDir)
    {
        var roundTripDir = Path.Combine(tempDir, "RoundTrip", assembly.Name);
        Directory.CreateDirectory(roundTripDir);

        var outputPath = Path.Combine(roundTripDir, Path.GetFileName(assembly.Path));

        // Read original metadata
        var originalMetadata = ReadAssemblyMetadata(assembly.Path);

        // Get test key if needed for strong naming
        StrongNameKey? key = null;
        if (assembly.IsStrongNamed)
        {
            var keyPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
            key = StrongNameKey.FromFile(keyPath);
        }

        // IMPORTANT: Actually modify the assembly to trigger metadata growth
        // This tests the full metadata rebuild path which is where bugs happen
        using (var modifier = StreamingAssemblyModifier.Open(assembly.Path))
        {
            // Add InternalsVisibleTo attributes to force metadata growth
            // This will trigger the metadata rebuild path instead of simple patching
            modifier.AddInternalsVisibleTo("TestFriend1", key?.PublicKey);
            modifier.AddInternalsVisibleTo("TestFriend2", key?.PublicKey);
            modifier.AddInternalsVisibleTo("TestFriend3", key?.PublicKey);

            // Internalize types (this is a common real-world scenario)
            modifier.MakeTypesInternal();

            modifier.Save(outputPath, key);
        }

        // Read modified metadata
        var roundTrippedMetadata = ReadAssemblyMetadata(outputPath);

        // Validate the assembly can still be loaded - THIS IS THE CRITICAL TEST
        // If MethodDef RVAs aren't patched, this will fail with "Bad IL format"
        var isLoadable = TryLoadAssembly(outputPath);

        return new AssemblyRoundTripResult
        {
            Name = assembly.Name,
            TargetFramework = assembly.TargetFramework,
            IsStrongNamed = assembly.IsStrongNamed,
            SymbolType = assembly.SymbolType,
            OriginalMetadata = originalMetadata,
            RoundTrippedMetadata = roundTrippedMetadata,
            IsLoadable = isLoadable,
            ValidationErrors = ValidateMetadata(originalMetadata, roundTrippedMetadata)
        };
    }

    static AssemblyMetadataInfo ReadAssemblyMetadata(string assemblyPath)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();

        var assemblyDef = reader.GetAssemblyDefinition();
        var publicKey = reader.GetBlobBytes(assemblyDef.PublicKey);

        return new AssemblyMetadataInfo
        {
            Name = reader.GetString(assemblyDef.Name),
            Version = assemblyDef.Version.ToString(),
            HasPublicKey = publicKey.Length > 0,
            PublicKeyToken = publicKey.Length > 0 ? MetadataHelper.FormatPublicKeyToken(publicKey) : "null",
            TypeCount = reader.TypeDefinitions.Count,
            MethodCount = reader.MethodDefinitions.Count,
            AssemblyRefCount = reader.AssemblyReferences.Count,
            HasDebugInfo = peReader.ReadDebugDirectory().Any(),
            HasEmbeddedPdb = peReader.ReadDebugDirectory().Any(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb),
            StringHeapSize = reader.GetHeapSize(HeapIndex.String),
            BlobHeapSize = reader.GetHeapSize(HeapIndex.Blob),
            GuidHeapSize = reader.GetHeapSize(HeapIndex.Guid),
            UserStringHeapSize = reader.GetHeapSize(HeapIndex.UserString)
        };
    }

    static bool TryLoadAssembly(string path)
    {
        var loadContext = new AssemblyLoadContext($"RoundTripTest_{Guid.NewGuid()}", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var assembly = loadContext.LoadFromStream(stream);
            return assembly != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    static List<string> ValidateMetadata(AssemblyMetadataInfo original, AssemblyMetadataInfo roundTripped)
    {
        var errors = new List<string>();

        // We're intentionally modifying assemblies now (adding IVT, internalizing types)
        // So we only validate that core structure is preserved, not exact equality

        if (original.Name != roundTripped.Name)
            errors.Add($"Name mismatch: {original.Name} != {roundTripped.Name}");

        if (original.Version != roundTripped.Version)
            errors.Add($"Version mismatch: {original.Version} != {roundTripped.Version}");

        // TypeCount and MethodCount should stay the same (we're not adding/removing types)
        if (original.TypeCount != roundTripped.TypeCount)
            errors.Add($"TypeCount mismatch: {original.TypeCount} != {roundTripped.TypeCount}");

        if (original.MethodCount != roundTripped.MethodCount)
            errors.Add($"MethodCount mismatch: {original.MethodCount} != {roundTripped.MethodCount}");

        // Note: PublicKey, AssemblyRefCount, and heap sizes will differ because we're modifying the assembly

        return errors;
    }

    record TestAssembly
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string TargetFramework { get; init; }
        public required bool IsStrongNamed { get; init; }
        public required SymbolType SymbolType { get; init; }
    }

    record AssemblyRoundTripResult
    {
        public required string Name { get; init; }
        public required string TargetFramework { get; init; }
        public required bool IsStrongNamed { get; init; }
        public required SymbolType SymbolType { get; init; }
        public required AssemblyMetadataInfo OriginalMetadata { get; init; }
        public required AssemblyMetadataInfo RoundTrippedMetadata { get; init; }
        public required bool IsLoadable { get; init; }
        public required List<string> ValidationErrors { get; init; }
    }

    record AssemblyMetadataInfo
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required bool HasPublicKey { get; init; }
        public required string PublicKeyToken { get; init; }
        public required int TypeCount { get; init; }
        public required int MethodCount { get; init; }
        public required int AssemblyRefCount { get; init; }
        public required bool HasDebugInfo { get; init; }
        public required bool HasEmbeddedPdb { get; init; }
        public required int StringHeapSize { get; init; }
        public required int BlobHeapSize { get; init; }
        public required int GuidHeapSize { get; init; }
        public required int UserStringHeapSize { get; init; }
    }
}

public enum SymbolType
{
    None,
    Embedded,
    External
}
