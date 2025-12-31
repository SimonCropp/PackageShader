using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using CliWrap;
using CliWrap.Buffered;
using Alias.Lib.Pdb;
using AliasMetadataReader = Alias.Lib.Metadata.MetadataReader;
using AliasPEReader = Alias.Lib.PE.PEReader;

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
                var reader = AliasMetadataReader.FromFile(modifiedPath);
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
            var origImage = AliasPEReader.Read(originalPath);
            var modImage = AliasPEReader.Read(modifiedPath);
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
        Program.Inner(tempPath, namesToAliases, new(), keyFile, new(), null, "_Alias", internalize, _ =>
        {
        });

        return BuildResults(tempPath);
    }

    static IEnumerable<AssemblyResult> BuildResults(string tempPath)
    {
        var resultingFiles = Directory.EnumerateFiles(tempPath);
        foreach (var assembly in resultingFiles.Where(_ => _.EndsWith(".dll")).OrderBy(_ => _))
        {
            var reader = AliasMetadataReader.FromFile(assembly);
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

    [Theory]
    [MemberData(nameof(GetData))]
    public void ModifiedAssembliesAreLoadable(bool copyPdbs, bool sign, bool internalize)
    {
        using var directory = new TempDirectory();
        Run(copyPdbs, sign, internalize, directory);

        var loadContext = new AssemblyLoadContext("TestContext", isCollectible: true);
        var loadedAssemblies = new List<(string Name, Assembly Assembly)>();

        try
        {
            foreach (var dllPath in Directory.GetFiles(directory, "*.dll"))
            {
                var assemblyBytes = File.ReadAllBytes(dllPath);
                using var stream = new MemoryStream(assemblyBytes);
                var assembly = loadContext.LoadFromStream(stream);
                loadedAssemblies.Add((Path.GetFileName(dllPath), assembly));
            }

            Assert.True(loadedAssemblies.Count > 0, "Should have loaded at least one assembly");

            foreach (var (name, assembly) in loadedAssemblies)
            {
                Assert.NotNull(assembly.GetName());
                Assert.NotNull(assembly.GetName().Name);
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Theory]
    [MemberData(nameof(GetData))]
    public void ModifiedAssembliesHaveValidMetadata(bool copyPdbs, bool sign, bool internalize)
    {
        using var directory = new TempDirectory();
        Run(copyPdbs, sign, internalize, directory);

        foreach (var dllPath in Directory.GetFiles(directory, "*.dll"))
        {
            using var fileStream = File.OpenRead(dllPath);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(fileStream);

            Assert.True(peReader.HasMetadata, $"{Path.GetFileName(dllPath)} should have metadata");

            var metadataReader = peReader.GetMetadataReader();

            // Verify assembly definition is valid
            Assert.True(metadataReader.IsAssembly, $"{Path.GetFileName(dllPath)} should be an assembly");

            var assemblyDef = metadataReader.GetAssemblyDefinition();
            var assemblyName = metadataReader.GetString(assemblyDef.Name);
            Assert.False(string.IsNullOrEmpty(assemblyName), "Assembly name should not be empty");

            // Verify type definitions are readable
            var typeCount = 0;
            foreach (var typeHandle in metadataReader.TypeDefinitions)
            {
                var typeDef = metadataReader.GetTypeDefinition(typeHandle);
                var typeName = metadataReader.GetString(typeDef.Name);
                Assert.NotNull(typeName);
                typeCount++;
            }
            Assert.True(typeCount > 0, $"{Path.GetFileName(dllPath)} should have types");

            // Verify method definitions are readable
            foreach (var typeHandle in metadataReader.TypeDefinitions)
            {
                var typeDef = metadataReader.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var methodDef = metadataReader.GetMethodDefinition(methodHandle);
                    var methodName = metadataReader.GetString(methodDef.Name);
                    Assert.NotNull(methodName);
                }
            }

            // Verify assembly references are readable
            foreach (var refHandle in metadataReader.AssemblyReferences)
            {
                var assemblyRef = metadataReader.GetAssemblyReference(refHandle);
                var refName = metadataReader.GetString(assemblyRef.Name);
                Assert.NotNull(refName);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModifiedAssembliesWithSymbolsHaveValidPdb(bool sign)
    {
        using var directory = new TempDirectory();
        // Run with copyPdbs=true to have PDB files, internalize doesn't matter for PDB validity
        Run(copyPdbs: true, sign: sign, internalize: false, directory);

        var assembliesWithSymbolsChecked = 0;

        foreach (var dllPath in Directory.GetFiles(directory, "*.dll"))
        {
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            var hasExternalPdb = File.Exists(pdbPath);
            var hasEmbeddedPdb = PdbHandler.HasEmbeddedPdb(File.ReadAllBytes(dllPath));

            if (!hasExternalPdb && !hasEmbeddedPdb)
            {
                continue;
            }

            assembliesWithSymbolsChecked++;

            using var dllStream = File.OpenRead(dllPath);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(dllStream);

            System.Reflection.Metadata.MetadataReaderProvider? pdbReaderProvider = null;

            try
            {
                if (hasEmbeddedPdb)
                {
                    // Read embedded PDB
                    var embeddedEntries = peReader.ReadDebugDirectory()
                        .Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                        .ToList();

                    if (embeddedEntries.Count > 0)
                    {
                        pdbReaderProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntries[0]);
                        ValidatePdbReader(pdbReaderProvider);
                    }
                }
                else if (hasExternalPdb)
                {
                    // Read external PDB - load into memory to avoid stream disposal issues
                    var pdbBytes = File.ReadAllBytes(pdbPath);
                    using var pdbStream = new MemoryStream(pdbBytes);
                    pdbReaderProvider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                    ValidatePdbReader(pdbReaderProvider);
                }
            }
            finally
            {
                pdbReaderProvider?.Dispose();
            }
        }

        Assert.True(assembliesWithSymbolsChecked > 0, "Should have checked at least one assembly with symbols");
    }

    [Fact]
    public void ModifiedAssemblyWithEmbeddedPdbHasValidSymbols()
    {
        using var directory = new TempDirectory();
        // copyPdbs=false but AssemblyWithEmbeddedSymbols has embedded PDB
        Run(copyPdbs: false, sign: false, internalize: false, directory);

        var embeddedSymbolsPath = Path.Combine(directory, "AssemblyWithEmbeddedSymbols_Alias.dll");
        Assert.True(File.Exists(embeddedSymbolsPath), "AssemblyWithEmbeddedSymbols_Alias.dll should exist");
        Assert.True(PdbHandler.HasEmbeddedPdb(File.ReadAllBytes(embeddedSymbolsPath)), "Should have embedded PDB");

        using var dllStream = File.OpenRead(embeddedSymbolsPath);
        using var peReader = new System.Reflection.PortableExecutable.PEReader(dllStream);

        var embeddedEntry = peReader.ReadDebugDirectory()
            .First(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        using var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
        var pdbReader = pdbProvider.GetMetadataReader();

        // Verify we can read document info
        var documents = pdbReader.Documents.Select(h =>
        {
            var doc = pdbReader.GetDocument(h);
            return pdbReader.GetString(doc.Name);
        }).ToList();

        Assert.True(documents.Count > 0, "Should have source documents in PDB");
        Assert.True(documents.Any(d => d.Contains("AssemblyWithEmbeddedSymbols")),
            "Should have source file for AssemblyWithEmbeddedSymbols");
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
            assemblyNamesToAlias: ["Assembly*"],
            references: [],
            keyFile: null,
            assembliesToExclude:
            [
                "AssemblyToInclude",
                "AssemblyToProcess"
            ],
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

    static void ValidatePdbReader(System.Reflection.Metadata.MetadataReaderProvider pdbReaderProvider)
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
}

public record AssemblyResult(string Name, bool HasSymbols, List<string> References, List<string> Attributes);