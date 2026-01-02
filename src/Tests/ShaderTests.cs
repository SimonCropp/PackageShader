[Collection("Sequential")]
public class ShaderTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(ShaderTests).Assembly.Location)!;

    static List<string> assemblyFiles =
    [
        "AssemblyToProcess",
        "AssemblyToInclude",
        "AssemblyWithEmbeddedSymbols",
        "AssemblyWithNoStrongName",
        "AssemblyWithStrongName",
        "AssemblyWithNoSymbols",
        "AssemblyWithPdb",
        "AssemblyWithResources",
        "Newtonsoft.Json"
    ];

    static IEnumerable<AssemblyResult> Run(bool copyPdbs, bool sign, bool internalize, string tempPath)
    {
        foreach (var assembly in assemblyFiles.OrderBy(_ => _))
        {
            var assemblyFile = $"{assembly}.dll";
            File.Copy(Path.Combine(binDirectory, assemblyFile), Path.Combine(tempPath, assemblyFile));
            if (copyPdbs)
            {
                var pdbFile = $"{assembly}.pdb";
                var sourceFileName = Path.Combine(binDirectory, pdbFile);
                if (File.Exists(sourceFileName))
                {
                    File.Copy(sourceFileName, Path.Combine(tempPath, pdbFile));
                }
            }
        }

        string? keyFile = null;
        if (sign)
        {
            keyFile = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
        }

        var namesToShade = assemblyFiles.Where(_ => _.StartsWith("AssemblyWith") || _ == "Newtonsoft.Json").ToList();
        Program.Inner(tempPath, namesToShade, keyFile, new(), null, "_Shaded", internalize, _ =>
        {
        });

        return BuildResults(tempPath);
    }

    static IEnumerable<AssemblyResult> BuildResults(string tempPath)
    {
        var resultingFiles = Directory.EnumerateFiles(tempPath);
        foreach (var assemblyPath in resultingFiles.Where(_ => _.EndsWith(".dll")).OrderBy(_ => _))
        {
            yield return BuildAssemblyResult(assemblyPath);
        }
    }

    static AssemblyResult BuildAssemblyResult(string assemblyPath)
    {
        var hasExternalSymbols = HasExternalSymbols(assemblyPath);
        using var fileStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fileStream);
        var hasEmbeddedSymbols = HasEmbeddedSymbols(peReader);
        var metadataReader = peReader.GetMetadataReader();

        var assemblyName = MetadataHelper.FormatAssemblyName(metadataReader);

        var references = MetadataHelper.GetAssemblyReferences(metadataReader);
        var attributes = MetadataHelper.GetInternalVisibleToAttributes(metadataReader);
        return new(assemblyName, references, attributes, hasExternalSymbols, hasEmbeddedSymbols);
    }

    [Theory]
    [MemberData(nameof(GetData))]
    public async Task Combo(bool copyPdbs, bool sign, bool internalize)
    {
        using var directory = new TempDirectory();
        var results = Run(copyPdbs, sign, internalize, directory);

        EnsureLoadable(directory);
        await Verify(results)
            .UseParameters(copyPdbs, sign, internalize);
    }

    static void EnsureLoadable(string directory)
    {
        var context = new AssemblyLoadContext("TestContext", isCollectible: true);

        try
        {
            foreach (var dllPath in Directory.GetFiles(directory, "*.dll"))
            {
                var bytes = File.ReadAllBytes(dllPath);
                using var stream = new MemoryStream(bytes);
                var assembly = context.LoadFromStream(stream);
                assembly.GetTypes();
            }
        }
        finally
        {
            context.Unload();
        }
    }

    static bool HasExternalSymbols(string dllPath)
    {
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return false;
        }

        using var stream = File.OpenRead(pdbPath);
        using var reader = MetadataReaderProvider.FromPortablePdbStream(stream);
        ValidatePdbReader(reader);

        return true;
    }

    static bool HasEmbeddedSymbols(PEReader peReader)
    {
        var entries = peReader.ReadDebugDirectory()
            .Where(_ => _.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            .ToList();

        if (entries.Count == 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            using var symbolReader = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            ValidatePdbReader(symbolReader);
        }

        return true;
    }

    //[Fact]
    //public Task PatternMatching()
    //{
    //    foreach (var assembly in assemblyFiles.OrderBy(_ => _))
    //    {
    //        var assemblyFile = $"{assembly}.dll";
    //        File.Copy(Path.Combine(binDirectory, assemblyFile), Path.Combine(tempPath, assemblyFile));
    //    }

    //    var namesToShade = assemblyFiles.Where(_ => _.StartsWith("AssemblyWith")).ToList();
    //    Program.Inner(tempPath, namesToShade, new(), null, new(), null, "_Shaded", false);
    //    var results = BuildResults();

    //    return Verifier.Verify(results);
    //}

    [Fact]
    public async Task RunTask()
    {
        var solutionDir = ProjectFiles.SolutionDirectory.Path;

        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments("build --configuration IncludeTask --no-restore")
            .WithWorkingDirectory(solutionDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        await Cli.Wrap("dotnet")
            .WithArguments("build-server shutdown")
            .ExecuteAsync(TestContext.Current.CancellationToken);

        if (buildResult.StandardError.Length > 0)
        {
            throw new(buildResult.StandardError);
        }

        if (buildResult.StandardOutput.Contains("error"))
        {
            throw new(buildResult.StandardOutput.Replace(solutionDir, ""));
        }

        var appPath = Path.Combine(solutionDir, "SampleAppForMsBuild/bin/IncludeTask/SampleAppForMsBuild.dll");
        var runResult = await Cli.Wrap("dotnet")
            .WithArguments(appPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        await Verify(
                new
                {
                    buildOutput = buildResult.StandardOutput,
                    consoleOutput = runResult.StandardOutput,
                    consoleError = runResult.StandardError
                })
            .ScrubLinesContaining(
                " -> ",
                "You are using a preview version",
                "MSBuild version ",
                "Time Elapsed")
            .ScrubLinesWithReplace(line => line.Replace('\\', '/'))
            .ScrubLinesWithReplace(line =>
            {
                if (line.Contains("Newtonsoft.Json.dll"))
                {
                    return "  	Newtonsoft.Json.dll";
                }

                return line;
            });
    }

#if DEBUG

    [Fact]
    public async Task RunSample()
    {
        var solutionDirectory = ProjectFiles.SolutionDirectory.Path;

        var targetPath = Path.Combine(solutionDirectory, "SampleApp/bin/Debug/net8.0");

        using var tempPath = new TempDirectory();

        Helpers.CopyFilesRecursively(targetPath, tempPath);

        Program.Inner(
            tempPath,
            assemblyNamesToShade: ["Assembly*"],
            keyFile: null,
            assembliesToExclude:
            [
                "AssemblyToInclude",
                "AssemblyToProcess"
            ],
            prefix: "Shaded_",
            suffix: null,
            internalize: true,
            _ =>
            {
            });

        PatchDependencies(tempPath);

        var exePath = Path.Combine(tempPath, "SampleApp.exe");

        var result = await Cli.Wrap(exePath).ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        await Verify(new
        {
            result.StandardOutput,
            result.StandardError
        });
    }

#endif

    static void PatchDependencies(string targetPath)
    {
        var depsFile = Path.Combine(targetPath, "SampleApp.deps.json");
        var text = File.ReadAllText(depsFile);
        // Only replace assemblies that were actually aliased (AssemblyWith* but not AssemblyTo*)
        text = text.Replace("AssemblyWith", "Shaded_AssemblyWith");
        File.Delete(depsFile);
        File.WriteAllText(depsFile, text);
    }

    static bool[] bools =
    {
        true,
        false
    };

    public static IEnumerable<object[]> GetData()
    {
        foreach (var copyPdbs in bools)
        foreach (var sign in bools)
        foreach (var internalize in bools)
        {
            yield return
            [
                copyPdbs,
                sign,
                internalize
            ];
        }
    }

    static void ValidatePdbReader(MetadataReaderProvider pdbReaderProvider)
    {
        var pdbReader = pdbReaderProvider.GetMetadataReader();

        // Verify PDB has document information
        foreach (var docHandle in pdbReader.Documents)
        {
            var document = pdbReader.GetDocument(docHandle);
            var docName = pdbReader.GetString(document.Name);
            Assert.NotNull(docName);
        }

        // Verify method debug info is readable
        foreach (var methodHandle in pdbReader.MethodDebugInformation)
        {
            var debugInfo = pdbReader.GetMethodDebugInformation(methodHandle);
            // Just verify it doesn't throw - sequence points may be empty for some methods
            var sequencePoints = debugInfo.GetSequencePoints();
            foreach (var sp in sequencePoints)
            {
                // Validate sequence point data is accessible
                _ = sp.StartLine;
                _ = sp.StartColumn;
            }
        }
    }

    static void CompileAssembly(string directory, string assemblyName, string code,
                                params string[] referenceNames)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };

        foreach (var refName in referenceNames)
        {
            var refPath = Path.Combine(directory, $"{refName}.dll");
            references.Add(MetadataReference.CreateFromFile(refPath));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var outputPath = Path.Combine(directory, $"{assemblyName}.dll");
        var result = compilation.Emit(outputPath);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new Exception($"Compilation failed for {assemblyName}:\n{errors}");
        }
    }

    [Fact]
    public async Task TransitiveShading()
    {
        using var directory = new TempDirectory();

        // Create 3-layer transitive chain: Assembly1 -> Assembly2 -> Assembly3
        CompileAssembly(directory, "Assembly3",
            "public static class Class3 { public static string Method() => \"Assembly3\"; }");

        CompileAssembly(directory, "Assembly2",
            "public static class Class2 { public static string Method() => Class3.Method(); }",
            "Assembly3");

        CompileAssembly(directory, "Assembly1",
            "public static class Class1 { public static string Method() => Class2.Method(); }",
            "Assembly2");

        // Shade all assemblies
        var infos = new[] { "Assembly1", "Assembly2", "Assembly3" }
            .Select(name => new SourceTargetInfo(
                SourceName: name,
                SourcePath: Path.Combine(directory, $"{name}.dll"),
                TargetName: $"{name}_Shaded",
                TargetPath: Path.Combine(directory, $"{name}_Shaded.dll"),
                IsShaded: true))
            .ToList();

        Shader.Run(infos, internalize: false, key: null);

        // Delete original assemblies (Shader.Run doesn't do this)
        foreach (var info in infos)
        {
            File.Delete(info.SourcePath);
        }

        // Use same BuildResults verification as Combo tests
        var results = BuildResults(directory);
        await Verify(results);
    }

    public record AssemblyResult(string Name, List<string> References, List<string> InternalsVisibleTo, bool HasExternalSymbols, bool HasEmbeddedSymbols);

}
