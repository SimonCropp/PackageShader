[Collection("Sequential")]
public class AssemblyRoundTripTests
{
    [Theory]
    [MemberData(nameof(GetAssemblyScenarios))]
    public async Task RoundTripAssembly(string targetFramework, bool strongNamed, SymbolType symbolType, CompilationMethod compilationMethod)
    {
        using var tempDir = new TempDirectory();
        var scenariosDir = Path.Combine(tempDir, "Scenarios");
        Directory.CreateDirectory(scenariosDir);

        var name = $"{targetFramework.Replace(".", "")}_{(strongNamed ? "StrongNamed" : "NoStrongName")}_{symbolType}_{compilationMethod}";

        var assembly = await CreateAssembly(scenariosDir, name, targetFramework, strongNamed, symbolType, compilationMethod);
        var result = PerformRoundTrip(assembly, tempDir);

        await Verify(result)
            .UseDirectory("Snapshots")
            .UseParameters(targetFramework, strongNamed, symbolType, compilationMethod);
    }

    public static IEnumerable<object[]> GetAssemblyScenarios()
    {
        var frameworks = new[] {"net8.0", "net9.0", "net10.0", "net48", "netstandard2.0", "netstandard2.1"};
        var strongNameOptions = new[] {true, false};
        var symbolTypes = new[] {SymbolType.Embedded, SymbolType.External, SymbolType.None};
        var compilationMethods = new[] {CompilationMethod.DotNetBuild, CompilationMethod.Roslyn};

        foreach (var framework in frameworks)
        {
            foreach (var strongNamed in strongNameOptions)
            {
                foreach (var symbolType in symbolTypes)
                {
                    foreach (var compilationMethod in compilationMethods)
                    {
                        yield return [framework, strongNamed, symbolType, compilationMethod];
                    }
                }
            }
        }
    }

    static async Task<TestAssembly> CreateAssembly(
        string baseDir,
        string name,
        string targetFramework,
        bool strongNamed,
        SymbolType symbolType,
        CompilationMethod compilationMethod) =>
        compilationMethod == CompilationMethod.Roslyn
            ? CreateAssemblyWithRoslyn(baseDir, name, targetFramework, strongNamed, symbolType)
            : await CreateAssemblyWithDotNetBuild(baseDir, name, targetFramework, strongNamed, symbolType);

    static TestAssembly CreateAssemblyWithRoslyn(
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
        var sourceCode = GetTestSourceCode(name);

        // Compile using Roslyn APIs (much faster than spawning dotnet build)
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceCode,
            path: $"{name}.cs",
            encoding: Encoding.UTF8);

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

        // Set assembly version to 1.0.0.0
        var assemblyVersion = new Version(1, 0, 0, 0);
        var assemblyInfo = CSharpSyntaxTree.ParseText(
            $"""

             using System.Reflection;
             [assembly: AssemblyVersion("{assemblyVersion}")]

             """, encoding: Encoding.UTF8);

        var compilation = CSharpCompilation.Create(
            name,
            [syntaxTree, assemblyInfo],
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
            SymbolType = symbolType,
            CompilationMethod = CompilationMethod.Roslyn
        };
    }

    static async Task<TestAssembly> CreateAssemblyWithDotNetBuild(
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
        var sourceCode = GetTestSourceCode(name);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Class.cs"), sourceCode);

        // Create key file if strong named
        if (strongNamed)
        {
            var keyFile = Path.Combine(projectDir, "key.snk");
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
        var projectContent = $"""
                              <Project Sdk="Microsoft.NET.Sdk">
                                <PropertyGroup>
                                  <TargetFramework>{targetFramework}</TargetFramework>
                                  <DebugType>{debugType}</DebugType>
                                  <DebugSymbols>{(symbolType != SymbolType.None ? "true" : "false")}</DebugSymbols>
                              {(strongNamed ? "    <SignAssembly>true</SignAssembly>\n    <AssemblyOriginatorKeyFile>key.snk</AssemblyOriginatorKeyFile>" : "")}
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
            SymbolType = symbolType,
            CompilationMethod = CompilationMethod.DotNetBuild
        };
    }

    static string GetTestSourceCode(string name) => $$"""
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

    static List<MetadataReference> GetMetadataReferences(string targetFramework)
    {
        // Get the reference assemblies path for the target framework
        var refAssembliesPath = targetFramework switch
        {
            "netstandard2.0" => FindReferenceAssemblies("NETStandard.Library.Ref", "netstandard2.0"),
            "netstandard2.1" => FindReferenceAssemblies("NETStandard.Library.Ref", "netstandard2.1"),
            "net48" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8"),
            "net8.0" => FindReferenceAssemblies("Microsoft.NETCore.App.Ref", "net8.0"),
            "net9.0" => FindReferenceAssemblies("Microsoft.NETCore.App.Ref", "net9.0"),
            "net10.0" => FindReferenceAssemblies("Microsoft.NETCore.App.Ref", "net10.0"),
            _ => throw new NotSupportedException($"Target framework {targetFramework} not supported")
        };

        if (!Directory.Exists(refAssembliesPath))
        {
            throw new DirectoryNotFoundException($"Reference assemblies not found at {refAssembliesPath}");
        }

        // Load all DLLs from the reference assemblies directory
        // Some directories (like .NET Framework 4.8) contain non-managed DLLs, so skip those
        var references = new List<MetadataReference>();
        foreach (var dll in Directory.GetFiles(refAssembliesPath, "*.dll"))
        {
            if (IsManagedAssembly(dll))
            {
                references.Add(MetadataReference.CreateFromFile(dll));
            }
        }

        return references;
    }

    static bool IsManagedAssembly(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var peReader = new PEReader(fs);

            if (!peReader.HasMetadata)
            {
                return false;
            }

            // Some files have HasMetadata = true but aren't actually usable as references
            // (e.g., System.EnterpriseServices.Wrapper.dll in net48)
            // Try to read the metadata to verify it's actually valid
            var reader = peReader.GetMetadataReader();
            _ = reader.GetAssemblyDefinition(); // This will throw if metadata is invalid

            return true;
        }
        catch
        {
            return false;
        }
    }

    static string FindReferenceAssemblies(string packName, string targetFramework)
    {
        var packsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "packs", packName);

        if (!Directory.Exists(packsDir))
        {
            throw new DirectoryNotFoundException($"Pack directory not found: {packsDir}");
        }

        // Find the highest installed version
        var versions = Directory.GetDirectories(packsDir)
            .Select(Path.GetFileName)
            .Where(v => v != null)
            .OrderByDescending(v => v)
            .ToList();

        foreach (var version in versions)
        {
            var refPath = Path.Combine(packsDir, version!, "ref", targetFramework);
            if (Directory.Exists(refPath))
            {
                return refPath;
            }
        }

        throw new DirectoryNotFoundException($"No reference assemblies found for {targetFramework} in {packsDir}");
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

        // Save original for debugging if it's a Roslyn strong-named embedded assembly
        if (assembly.Name.Contains("net80_StrongNamed_Embedded_Roslyn"))
        {
            var origPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "original_Roslyn_assembly.dll");
            File.Copy(assembly.Path, origPath, true);
            Console.WriteLine($"Copied ORIGINAL assembly to: {origPath}");
        }

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

        // Copy both working and failing assemblies for comparison
        if (assembly.Name.Contains("net80_StrongNamed_Embedded"))
        {
            var suffix = isLoadable ? "working" : "failing";
            var debugPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, $"{suffix}_{assembly.CompilationMethod}_assembly.dll");
            File.Copy(outputPath, debugPath, true);
            Console.WriteLine($"Copied {suffix} assembly ({assembly.CompilationMethod}) to: {debugPath}");
        }

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
        catch (Exception ex)
        {
            if (path.Contains("StrongNamed") && path.Contains("Roslyn") && (path.Contains("Embedded") || path.Contains("None")))
            {
                Console.WriteLine($"LOAD ERROR for {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            }

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
        public required CompilationMethod CompilationMethod { get; init; }
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

public enum CompilationMethod
{
    DotNetBuild,
    Roslyn
}
