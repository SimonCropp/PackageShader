using CliWrap;
using CliWrap.Buffered;
using Alias.Lib.Metadata;
using Alias.Lib.PE;
using Alias.Lib.Pdb;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollection;

[Collection("Sequential")]
public class AliasTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(AliasTests).Assembly.Location)!;

    [Fact]
    public void DiagnoseRunSample()
    {
        var solutionDirectory = ProjectFiles.SolutionDirectory.Path;
        var targetPath = Path.Combine(solutionDirectory, "SampleApp/bin/Debug/net8.0");
        var tempPath2 = Path.Combine(targetPath, "temp");

        // Check modified SampleApp.dll
        var modifiedPath = Path.Combine(tempPath2, "SampleApp.dll");
        if (File.Exists(modifiedPath))
        {
            Console.WriteLine($"Modified SampleApp.dll size: {new FileInfo(modifiedPath).Length}");

            try
            {
                var reader = MetadataReader.FromFile(modifiedPath);
                Console.WriteLine($"Assembly name: {reader.GetAssemblyName()}");
                Console.WriteLine($"References: {reader.GetAssemblyRefs().Count()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading modified assembly: {ex.GetType().Name}: {ex.Message}");
            }

            // Compare PE headers
            var originalPath = Path.Combine(targetPath, "SampleApp.dll");
            var origBytes = File.ReadAllBytes(originalPath);
            var modBytes = File.ReadAllBytes(modifiedPath);

            // Check DOS/PE signature
            Console.WriteLine($"Original PE offset: {BitConverter.ToInt32(origBytes, 60):X}");
            Console.WriteLine($"Modified PE offset: {BitConverter.ToInt32(modBytes, 60):X}");

            // Check metadata RVA from CLI header
            var origImage = PEReader.Read(originalPath);
            var modImage = PEReader.Read(modifiedPath);
            Console.WriteLine($"Original metadata RVA: {origImage.MetadataRva:X}, Size: {origImage.MetadataSize}");
            Console.WriteLine($"Modified metadata RVA: {modImage.MetadataRva:X}, Size: {modImage.MetadataSize}");

            // Check if sections are correct
            Console.WriteLine($"Original sections: {origImage.Sections.Length}");
            Console.WriteLine($"Modified sections: {modImage.Sections.Length}");
            foreach (var section in modImage.Sections)
            {
                Console.WriteLine($"  {section.Name}: VA={section.VirtualAddress:X}, Size={section.SizeOfRawData}, RawPtr={section.PointerToRawData:X}");
            }
        }
        else
        {
            Console.WriteLine("Modified SampleApp.dll not found");
        }
    }

    static List<string> assemblyFiles = new()
    {
        "AssemblyToProcess",
        "AssemblyToInclude",
        "AssemblyWithEmbeddedSymbols",
        "AssemblyWithNoStrongName",
        "AssemblyWithStrongName",
        "AssemblyWithNoSymbols",
        "AssemblyWithPdb",
        "AssemblyWithResources",
        "Newtonsoft.Json"
    };

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

        var namesToAliases = assemblyFiles.Where(_ => _.StartsWith("AssemblyWith") || _ == "Newtonsoft.Json").ToList();
        Program.Inner(tempPath, namesToAliases, new(), keyFile, new(), null, "_Alias", internalize, _=>{});

        return BuildResults(tempPath);
    }

    static IEnumerable<AssemblyResult> BuildResults(string tempPath)
    {
        var resultingFiles = Directory.EnumerateFiles(tempPath);
        foreach (var assembly in resultingFiles.Where(_ => _.EndsWith(".dll")).OrderBy(_ => _))
        {
            var reader = MetadataReader.FromFile(assembly);
            var assemblyName = reader.GetAssemblyName();
            var hasSymbols = PdbHandler.HasSymbols(assembly);
            var references = reader.GetAssemblyRefs()
                .Select(r => r.FullName)
                .OrderBy(_ => _)
                .ToList();
            var attributes = reader.GetAssemblyCustomAttributes()
                .Where(a => a.AttributeTypeName.Contains("Internals"))
                .Select(a => $"{a.AttributeTypeName}({a.ConstructorArgument})")
                .OrderBy(_ => _)
                .ToList();
            yield return new(assemblyName, hasSymbols, references, attributes);
        }
    }

    [Theory]
    [MemberData(nameof(GetData))]
    public async Task Combo(bool copyPdbs, bool sign, bool internalize)
    {
        using var directory = new TempDirectory();
        var results = Run(copyPdbs, sign, internalize, directory);

        await Verify(results)
            .UseParameters(copyPdbs, sign, internalize);
    }

    //[Fact]
    //public Task PatternMatching()
    //{
    //    foreach (var assembly in assemblyFiles.OrderBy(_ => _))
    //    {
    //        var assemblyFile = $"{assembly}.dll";
    //        File.Copy(Path.Combine(binDirectory, assemblyFile), Path.Combine(tempPath, assemblyFile));
    //    }

    //    var namesToAliases = assemblyFiles.Where(_ => _.StartsWith("AssemblyWith")).ToList();
    //    Program.Inner(tempPath, namesToAliases, new(), null, new(), null, "_Alias", false);
    //    var results = BuildResults();

    //    return Verifier.Verify(results);
    //}

    [Fact]
    public async Task RunTask()
    {
        var solutionDir = ProjectFiles.SolutionDirectory.Path;

        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments("build --configuration IncludeAliasTask --no-restore")
            .WithWorkingDirectory(solutionDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        var shutdown = Cli.Wrap("dotnet")
            .WithArguments("build-server shutdown")
            .ExecuteAsync(TestContext.Current.CancellationToken);

        try
        {
            if (buildResult.StandardError.Length > 0)
            {
                throw new(buildResult.StandardError);
            }

            if (buildResult.StandardOutput.Contains("error"))
            {
                throw new(buildResult.StandardOutput.Replace(solutionDir, ""));
            }

            var appPath = Path.Combine(solutionDir, "SampleAppForMsBuild/bin/IncludeAliasTask/SampleAppForMsBuild.dll");
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
        finally
        {
            await shutdown;
        }
    }

#if DEBUG

    [Fact]
    public async Task RunSample()
    {
        var solutionDirectory = ProjectFiles.SolutionDirectory.Path;

        var targetPath = Path.Combine(solutionDirectory, "SampleApp/bin/Debug/net8.0");

        using var tempPath = new TempDirectory();
        Directory.CreateDirectory(tempPath);
        Helpers.PurgeDirectory(tempPath);

        Helpers.CopyFilesRecursively(targetPath, tempPath);

        Program.Inner(
            tempPath,
            assemblyNamesToAlias: new()
            {
                "Assembly*"
            },
            references: new(),
            keyFile: null,
            assembliesToExclude: new()
            {
                "AssemblyToInclude",
                "AssemblyToProcess"
            },
            prefix: "Alias_",
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
        text = text.Replace("AssemblyWith", "Alias_AssemblyWith");
        File.Delete(depsFile);
        File.WriteAllText(depsFile, text);
    }

    static bool[] bools = {true, false};

    public static IEnumerable<object[]> GetData()
    {
        foreach (var copyPdbs in bools)
        foreach (var sign in bools)
        foreach (var internalize in bools)
        {
            yield return new object[] {copyPdbs, sign, internalize};
        }
    }
}

public record AssemblyResult(string Name, bool HasSymbols, List<string> References, List<string> Attributes);