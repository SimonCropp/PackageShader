using Microsoft.CodeAnalysis.Emit;

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
            assembly = CreateAssembly(scenariosDir, name, targetFramework, strongNamed, symbolType);
        }
        catch (Exception)
        {
            // Skip assemblies that fail to build (e.g., net48 on Linux, netstandard without SDK)
            return;
        }

        var result = PerformRoundTrip(assembly, tempDir);

        await Verify(result)
            .UseDirectory("Snapshots");
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
                    yield return [framework, strongNamed, symbolType];
                }
            }
        }
    }

    static TestAssembly CreateAssembly(
        string baseDir,
        string name,
        string targetFramework,
        bool strongNamed,
        SymbolType symbolType)
    {
        var categoryDir = Path.Combine(baseDir, targetFramework);
        Directory.CreateDirectory(categoryDir);

        var finalDir = Path.Combine(categoryDir, "assemblies");
        Directory.CreateDirectory(finalDir);
        var finalPath = Path.Combine(finalDir, $"{name}.dll");

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

        // Compile using Roslyn APIs (much faster than spawning dotnet build)
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: $"{name}.cs");

        var references = GetMetadataReferences(targetFramework);

        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithPlatform(Platform.AnyCpu);

        // Handle strong naming
        if (strongNamed)
        {
            var testKeyPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
            var strongNameProvider = new DesktopStrongNameProvider(
                keyFileSearchPaths: [Path.GetDirectoryName(testKeyPath)!]);

            compilationOptions = compilationOptions
                .WithCryptoKeyFile(testKeyPath)
                .WithStrongNameProvider(strongNameProvider);
        }

        var compilation = CSharpCompilation.Create(
            name,
            [syntaxTree],
            references,
            compilationOptions);

        // Emit assembly with appropriate PDB options
        using var peStream = new FileStream(finalPath, FileMode.Create, FileAccess.ReadWrite);
        Stream? pdbStream = null;

        try
        {
            var emitOptions = new EmitOptions();

            if (symbolType == SymbolType.Embedded)
            {
                emitOptions = emitOptions.WithDebugInformationFormat(DebugInformationFormat.Embedded);
            }
            else if (symbolType == SymbolType.External)
            {
                var pdbPath = Path.Combine(finalDir, $"{name}.pdb");
                pdbStream = new FileStream(pdbPath, FileMode.Create, FileAccess.ReadWrite);
                emitOptions = emitOptions.WithDebugInformationFormat(DebugInformationFormat.PortablePdb);
            }
            else
            {
                emitOptions = emitOptions.WithDebugInformationFormat(DebugInformationFormat.Embedded);
            }

            var result = compilation.Emit(
                peStream: peStream,
                pdbStream: symbolType == SymbolType.External ? pdbStream : null,
                options: emitOptions);

            if (!result.Success)
            {
                var errors = string.Join("\n", result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                throw new($"Compilation failed:\n{errors}");
            }
        }
        finally
        {
            pdbStream?.Dispose();
        }

        return new()
        {
            Name = name,
            Path = finalPath,
            TargetFramework = targetFramework,
            IsStrongNamed = strongNamed,
            SymbolType = symbolType
        };
    }

    static List<MetadataReference> GetMetadataReferences(string targetFramework)
    {
        // Get the reference assemblies path for the target framework
        var refAssembliesPath = targetFramework switch
        {
            "netstandard2.0" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "packs", "NETStandard.Library.Ref", "2.1.0", "ref", "netstandard2.0"),
            "netstandard2.1" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "packs", "NETStandard.Library.Ref", "2.1.0", "ref", "netstandard2.1"),
            "net48" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8"),
            "net8.0" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "packs", "Microsoft.NETCore.App.Ref", "8.0.0", "ref", "net8.0"),
            "net9.0" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "packs", "Microsoft.NETCore.App.Ref", "9.0.0", "ref", "net9.0"),
            "net10.0" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "packs", "Microsoft.NETCore.App.Ref", "10.0.0", "ref", "net10.0"),
            _ => throw new NotSupportedException($"Target framework {targetFramework} not supported")
        };

        if (!Directory.Exists(refAssembliesPath))
        {
            throw new DirectoryNotFoundException($"Reference assemblies not found at {refAssembliesPath}");
        }

        // Load all DLLs from the reference assemblies directory
        var references = new List<MetadataReference>();
        foreach (var dll in Directory.GetFiles(refAssembliesPath, "*.dll"))
        {
            references.Add(MetadataReference.CreateFromFile(dll));
        }

        return references;
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

    static AssemblyRoundTripResult PerformRoundTrip(TestAssembly assembly, string tempDir)
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

        return new()
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

        return new()
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
            loadContext.LoadFromStream(stream);
            return true;
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
