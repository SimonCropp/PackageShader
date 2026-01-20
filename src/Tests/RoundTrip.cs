[Collection("Sequential")]
public class RoundTrip
{
    [Theory]
    [MemberData(nameof(GetAssemblyScenarios))]
    public async Task Test(string framework, bool signed, Symbol symbol, Compilation compilation)
    {
        using var tempDir = new TempDirectory();
        var scenariosDir = Path.Combine(tempDir, "Scenarios");
        Directory.CreateDirectory(scenariosDir);

        var name = $"{framework.Replace(".", "")}_{(signed ? "StrongNamed" : "NoStrongName")}_{symbol}_{compilation}";

        var assembly = await CreateAssembly(scenariosDir, name, framework, signed, symbol, compilation);
        var result = PerformRoundTrip(assembly, tempDir);

        await Verify(result)
            .AppendFile(assembly.Path, "before")
            .AppendFile(result.Path, "after")
            .UseDirectory("Snapshots")
            .IgnoreMember("Path");

        ValidateNoNewErrors(assembly.Path, result.Path, framework);
    }

    public static IEnumerable<object[]> GetAssemblyScenarios()
    {
        var frameworks = new[] {"net8.0", "net9.0", "net10.0", "net48", "netstandard2.0", "netstandard2.1"};
        var strongNameOptions = new[] {true, false};
        var symbolTypes = new[] {Symbol.Embedded, Symbol.External, Symbol.None};
        var compilationMethods = new[] {Compilation.DotNetBuild, Compilation.Roslyn};

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
        Symbol symbol,
        Compilation compilation)
    {
        if (compilation == Compilation.Roslyn)
        {
            return CreateAssemblyWithRoslyn(baseDir, name, targetFramework, strongNamed, symbol);
        }

        return await CreateAssemblyWithDotNetBuild(baseDir, name, targetFramework, strongNamed, symbol);
    }

    static TestAssembly CreateAssemblyWithRoslyn(
        string baseDir,
        string name,
        string targetFramework,
        bool strongNamed,
        Symbol symbol)
    {
        var categoryDir = Path.Combine(baseDir, targetFramework);
        Directory.CreateDirectory(categoryDir);

        var finalDir = Path.Combine(categoryDir, "assemblies");
        Directory.CreateDirectory(finalDir);
        var finalPath = Path.Combine(finalDir, $"{name}.dll");

        // Create minimal C# source
        var sourceCode = GetTestSourceCode(name);

        // Compile using Roslyn APIs (much faster than spawning dotnet build)
        // Use a fixed deterministic path for the source file (no temp directory paths)
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceCode,
            path: $"/_/{name}.cs",
            encoding: Encoding.UTF8);

        var references = GetMetadataReferences(targetFramework);

        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithPlatform(Platform.AnyCpu)
            .WithDeterministic(true);

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

             """,
            path: "/_/AssemblyInfo.cs",
            encoding: Encoding.UTF8);

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

            if (symbol == Symbol.Embedded)
            {
                emitOptions = emitOptions.WithDebugInformationFormat(DebugInformationFormat.Embedded);
            }
            else if (symbol == Symbol.External)
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
                pdbStream: symbol == Symbol.External ? pdbStream : null,
                options: emitOptions);

            if (!result.Success)
            {
                var errors = string.Join(
                    '\n',
                    result.Diagnostics
                        .Where(_ => _.Severity == DiagnosticSeverity.Error)
                        .Select(_ => _.ToString()));
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
            Symbol = symbol,
            Compilation = Compilation.Roslyn
        };
    }

    static async Task<TestAssembly> CreateAssemblyWithDotNetBuild(
        string baseDir,
        string name,
        string targetFramework,
        bool strongNamed,
        Symbol symbol)
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
        var debugType = symbol switch
        {
            Symbol.Embedded => "embedded",
            Symbol.External => "portable",
            Symbol.None => "none",
            _ => "portable"
        };

        // Create project file
        var projectContent = $"""
                              <Project Sdk="Microsoft.NET.Sdk">
                                <PropertyGroup>
                                  <TargetFramework>{targetFramework}</TargetFramework>
                                  <LangVersion>latest</LangVersion>
                                  <DebugType>{debugType}</DebugType>
                                  <DebugSymbols>{(symbol != Symbol.None ? "true" : "false")}</DebugSymbols>
                                  <Deterministic>true</Deterministic>
                                  <Version>1.0.0.0</Version>
                                  <PathMap>$(MSBuildProjectDirectory)=/_/</PathMap>
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
            var errorOutput = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new($"Failed to build {name}:\n{errorOutput}");
        }

        var outputPath = Path.Combine(projectDir, "bin", "Release", targetFramework, $"{name}.dll");
        if (!File.Exists(outputPath))
        {
            throw new($"Assembly not found at {outputPath}");
        }

        // Copy assembly to a cleaner location
        var finalDir = Path.Combine(categoryDir, "assemblies");
        Directory.CreateDirectory(finalDir);
        var finalPath = Path.Combine(finalDir, $"{name}.dll");
        File.Copy(outputPath, finalPath, true);

        // Copy PDB if external
        if (symbol == Symbol.External)
        {
            var pdbPath = Path.Combine(projectDir, "bin", "Release", targetFramework, $"{name}.pdb");
            if (File.Exists(pdbPath))
            {
                File.Copy(pdbPath, Path.Combine(finalDir, $"{name}.pdb"), true);
            }
        }

        // Delete the project directory to clean up
        Directory.Delete(projectDir, true);

        return new()
        {
            Name = name,
            Path = finalPath,
            TargetFramework = targetFramework,
            IsStrongNamed = strongNamed,
            Symbol = symbol,
            Compilation = Compilation.DotNetBuild
        };
    }

    static string GetTestSourceCode(string name) =>
        $$"""
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
            "netstandard2.0" => FindNetStandardReferenceAssemblies("netstandard2.0"),
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

        // Extract major.minor from target framework
        // "net8.0" -> "8.0", "netstandard2.1" -> "2.1"
        var tfmVersion = targetFramework
            .Replace("netstandard", "")
            .Replace("net", "");

        // Find versions matching the target framework (e.g., for net8.0, use 8.0.x not 10.0.x)
        // Use the lowest matching version for consistency across machines
        var versions = Directory.GetDirectories(packsDir)
            .Select(Path.GetFileName)
            .Where(v => v != null && v.StartsWith(tfmVersion + "."))
            .OrderBy(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .ToList();

        foreach (var version in versions)
        {
            var refPath = Path.Combine(packsDir, version!, "ref", targetFramework);
            if (Directory.Exists(refPath))
            {
                return refPath;
            }
        }

        throw new DirectoryNotFoundException($"No reference assemblies found for {targetFramework} (version {tfmVersion}.x) in {packsDir}");
    }

    static string FindNetStandardReferenceAssemblies(string targetFramework)
    {
        // netstandard2.0 reference assemblies come from NuGet package, not from packs folder
        var nugetPackagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "netstandard.library");

        if (!Directory.Exists(nugetPackagesDir))
        {
            throw new DirectoryNotFoundException($"NETStandard.Library NuGet package not found at {nugetPackagesDir}");
        }

        // Find the lowest installed version for deterministic builds
        var versions = Directory.GetDirectories(nugetPackagesDir)
            .Select(Path.GetFileName)
            .Where(v => v != null)
            .OrderBy(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .ToList();

        foreach (var version in versions)
        {
            var refPath = Path.Combine(nugetPackagesDir, version!, "build", targetFramework, "ref");
            if (Directory.Exists(refPath))
            {
                return refPath;
            }
        }

        throw new DirectoryNotFoundException($"No reference assemblies found for {targetFramework} in {nugetPackagesDir}");
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

        return new()
        {
            Path = outputPath,
            Name = assembly.Name,
            TargetFramework = assembly.TargetFramework,
            IsStrongNamed = assembly.IsStrongNamed,
            Symbol = assembly.Symbol,
            OriginalMetadata = originalMetadata,
            RoundTrippedMetadata = roundTrippedMetadata,
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

    static List<string> GetILVerifyErrors(string assemblyPath)
    {
        using var resolver = new TestAssemblyResolver(assemblyPath);
        var verifier = new ILVerify.Verifier(resolver);
        verifier.SetSystemModuleName(AssemblyNameInfo.Parse(resolver.SystemModuleName));

        using var fs = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fs);
        var reader = peReader.GetMetadataReader();

        var errors = new List<string>();

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var results = verifier.Verify(peReader, methodHandle);
            foreach (var result in results)
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(method.Name);
                var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
                var typeName = reader.GetString(declaringType.Name);
                var ns = reader.GetString(declaringType.Namespace);
                var fullTypeName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

                errors.Add($"[{result.Code}] {fullTypeName}.{methodName}: {result.Message}");
            }
        }

        return errors;
    }

    static List<string> GetPeVerifyErrors(string assemblyPath)
    {
        if (!PeVerifyTool.FoundPeVerify)
        {
            return [];
        }

        var ignoreCodes = new List<string> { "0x80070002", "0x80131252" };
        var workingDirectory = Path.GetDirectoryName(assemblyPath)!;

        var arguments = $"\"{assemblyPath}\" /hresult /nologo /ignore={string.Join(",", ignoreCodes)}";
        var processStartInfo = new ProcessStartInfo(PeVerifyTool.PeVerifyPath!)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(processStartInfo)!;
        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(10000))
        {
            throw new Exception("PeVerify failed to exit");
        }

        // Clean up output
        output = Regex.Replace(output, "^All Classes and Methods.*", "", RegexOptions.Multiline);
        output = Regex.Replace(output, @"\[offset [^\]]*\]", ""); // Remove offset info for comparison
        output = output.Trim();

        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();
    }

    static void ValidateNoNewErrors(string inputPath, string outputPath, string targetFramework)
    {
        // ILVerify check
        var inputIlVerifyErrors = GetILVerifyErrors(inputPath).ToHashSet();
        var outputIlVerifyErrors = GetILVerifyErrors(outputPath);
        var newIlVerifyErrors = outputIlVerifyErrors.Where(e => !inputIlVerifyErrors.Contains(e)).ToList();

        if (newIlVerifyErrors.Count > 0)
        {
            throw new($"ILVerify found {newIlVerifyErrors.Count} new error(s) in output assembly {Path.GetFileName(outputPath)}:\n{string.Join('\n', newIlVerifyErrors)}");
        }

        // PeVerify check (only for .NET Framework assemblies - PeVerify cannot verify .NET Core assemblies)
        var isNetFramework = targetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase);
        if (PeVerifyTool.FoundPeVerify && isNetFramework)
        {
            var inputPeVerifyErrors = GetPeVerifyErrors(inputPath).ToHashSet();
            var outputPeVerifyErrors = GetPeVerifyErrors(outputPath);
            var newPeVerifyErrors = outputPeVerifyErrors.Where(e => !inputPeVerifyErrors.Contains(e)).ToList();

            if (newPeVerifyErrors.Count > 0)
            {
                throw new($"PeVerify found {newPeVerifyErrors.Count} new error(s) in output assembly {Path.GetFileName(outputPath)}:\n{string.Join('\n', newPeVerifyErrors)}");
            }
        }
    }

    static class PeVerifyTool
    {
        public static readonly bool FoundPeVerify;
        public static readonly string? PeVerifyPath;

        static PeVerifyTool() =>
            FoundPeVerify = TryFindPeVerify(out PeVerifyPath);

        static bool TryFindPeVerify(out string? path)
        {
            var programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var windowsSdkDirectory = Path.Combine(programFilesPath, @"Microsoft SDKs\Windows");

            if (!Directory.Exists(windowsSdkDirectory))
            {
                path = null;
                return false;
            }

            path = Directory.EnumerateFiles(windowsSdkDirectory, "peverify.exe", SearchOption.AllDirectories)
                .Where(x => !x.Contains("x64", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x =>
                {
                    var info = FileVersionInfo.GetVersionInfo(x);
                    return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                })
                .FirstOrDefault();

            return path != null;
        }
    }

    sealed class TestAssemblyResolver : ILVerify.IResolver, IDisposable
    {
        string assemblyPath;
        Dictionary<string, PEReader> cache = new(StringComparer.OrdinalIgnoreCase);
        string referenceAssembliesPath;

        public string SystemModuleName { get; }

        public TestAssemblyResolver(string assemblyPath)
        {
            this.assemblyPath = assemblyPath;

            // Determine the reference assemblies path based on runtime
            // For simplicity, use the current runtime's reference assemblies
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            referenceAssembliesPath = runtimeDir;

            // Determine system module name
            SystemModuleName = "System.Private.CoreLib";
        }

        public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName) => Resolve(assemblyName.Name);

        public PEReader? ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) =>
            // For multi-module assemblies - not common, just return null
            null;

        PEReader? Resolve(string simpleName)
        {
            if (cache.TryGetValue(simpleName, out var cached))
            {
                return cached;
            }

            // Check if it's the assembly being verified
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            if (string.Equals(simpleName, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                var reader = new PEReader(File.OpenRead(assemblyPath));
                cache[simpleName] = reader;
                return reader;
            }

            // Try to find in reference assemblies path
            var dllPath = Path.Combine(referenceAssembliesPath, $"{simpleName}.dll");
            if (File.Exists(dllPath))
            {
                var reader = new PEReader(File.OpenRead(dllPath));
                cache[simpleName] = reader;
                return reader;
            }

            return null;
        }

        public void Dispose()
        {
            foreach (var reader in cache.Values)
            {
                reader.Dispose();
            }

            cache.Clear();
        }
    }

    record TestAssembly
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string TargetFramework { get; init; }
        public required bool IsStrongNamed { get; init; }
        public required Symbol Symbol { get; init; }
        public required Compilation Compilation { get; init; }
    }

    record AssemblyRoundTripResult
    {
        public required string Name { get; init; }
        public required string TargetFramework { get; init; }
        public required bool IsStrongNamed { get; init; }
        public required Symbol Symbol { get; init; }
        public required AssemblyMetadataInfo OriginalMetadata { get; init; }
        public required AssemblyMetadataInfo RoundTrippedMetadata { get; init; }
        public required List<string> ValidationErrors { get; init; }
        public required string Path { get; init; }
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

    public enum Symbol
    {
        None,
        Embedded,
        External
    }

    public enum Compilation
    {
        DotNetBuild,
        Roslyn
    }
}

