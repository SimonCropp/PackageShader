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

        var namesToShade = assemblyFiles
            .Where(_ => _.StartsWith("AssemblyWith") ||
                        _ == "Newtonsoft.Json").ToList();
        Program.Inner(
            tempPath,
            namesToShade,
            keyFile,
            [],
            null,
            "_Shaded",
            internalize,
            _ =>
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

    static void PatchDependencies(string targetPath)
    {
        var depsFile = Path.Combine(targetPath, "SampleApp.deps.json");
        var text = File.ReadAllText(depsFile);
        // Only replace assemblies that were actually aliased (AssemblyWith* but not AssemblyTo*)
        text = text.Replace("AssemblyWith", "Shaded_AssemblyWith");
        File.Delete(depsFile);
        File.WriteAllText(depsFile, text);
    }
#endif

    static bool[] bools = [true, false];

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
            new(OutputKind.DynamicallyLinkedLibrary));

        var outputPath = Path.Combine(directory, $"{assemblyName}.dll");
        var result = compilation.Emit(outputPath);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(_ => _.Severity == DiagnosticSeverity.Error));
            throw new($"Compilation failed for {assemblyName}:\n{errors}");
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
        var infos = new[]
            {
                "Assembly1",
                "Assembly2",
                "Assembly3"
            }
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

    [Fact]
    public void DetectsBrokenConfiguration_WhenUnshadedAssemblyReferencesShadedAssembly()
    {
        // This test reproduces the MarkdownSnippets issue:
        // - AssemblyToProcess (unshaded) references AssemblyToInclude
        // - AssemblyToInclude is being shaded
        // - This creates a broken configuration with dangling references

        using var directory = new TempDirectory();

        // Copy both assemblies
        var assemblyToProcess = Path.Combine(binDirectory, "AssemblyToProcess.dll");
        var assemblyToInclude = Path.Combine(binDirectory, "AssemblyToInclude.dll");

        File.Copy(assemblyToProcess, Path.Combine(directory, "AssemblyToProcess.dll"));
        File.Copy(assemblyToInclude, Path.Combine(directory, "AssemblyToInclude.dll"));

        var infos = new List<SourceTargetInfo>
        {
            // Shade AssemblyToInclude
            new(
                "AssemblyToInclude",
                Path.Combine(directory, "AssemblyToInclude.dll"),
                "Test_AssemblyToInclude",
                Path.Combine(directory, "Test_AssemblyToInclude.dll"),
                IsShaded: true
            ),
            // Don't shade AssemblyToProcess, but redirect its refs
            new(
                "AssemblyToProcess",
                Path.Combine(directory, "AssemblyToProcess.dll"),
                "AssemblyToProcess",
                Path.Combine(directory, "AssemblyToProcess.dll"),
                IsShaded: false
            )
        };

        // This should throw an error about broken configuration
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            Shader.Run(infos, internalize: false, key: null);
        });

        // Verify the error message is helpful
        Assert.Contains("AssemblyToProcess", exception.Message);
        Assert.Contains("AssemblyToInclude", exception.Message);
        Assert.Contains("reference", exception.Message.ToLower());
    }

    /// <summary>
    /// Tests that when metadata grows significantly (causing section VAs to shift),
    /// the data directory RVAs (Resource, BaseReloc) are correctly updated.
    /// Per ECMA-335 II.25.2.3.3 and II.25.3, data directory RVAs must point to
    /// valid locations within their respective sections.
    /// </summary>
    [Fact]
    public void DataDirectoryRvasUpdatedWhenSectionsShift()
    {
        using var directory = new TempDirectory();

        // Copy AssemblyWithResources which has .rsrc section
        var sourceAssembly = Path.Combine(binDirectory, "AssemblyWithResources.dll");
        Assert.True(File.Exists(sourceAssembly), "AssemblyWithResources.dll not found");
        File.Copy(sourceAssembly, Path.Combine(directory, "AssemblyWithResources.dll"));

        // Get original section info
        using var originalFs = File.OpenRead(sourceAssembly);
        using var originalPe = new PEReader(originalFs);
        var originalResourceRva = originalPe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
        var originalRelocRva = originalPe.PEHeaders.PEHeader.BaseRelocationTableDirectory.RelativeVirtualAddress;
        var originalRsrcSection = originalPe.PEHeaders.SectionHeaders.FirstOrDefault(s => s.Name == ".rsrc");
        var originalRelocSection = originalPe.PEHeaders.SectionHeaders.FirstOrDefault(s => s.Name == ".reloc");

        var infos = new List<SourceTargetInfo>
        {
            new(
                "AssemblyWithResources",
                Path.Combine(directory, "AssemblyWithResources.dll"),
                "Shaded.AssemblyWithResources",
                Path.Combine(directory, "Shaded.AssemblyWithResources.dll"),
                IsShaded: true
            )
        };

        // Use StreamingAssemblyModifier to add many IVT attributes (forces metadata growth)
        var shadedPath = Path.Combine(directory, "Shaded.AssemblyWithResources.dll");
        using (var modifier = StreamingAssemblyModifier.Open(sourceAssembly))
        {
            // Add many InternalsVisibleTo attributes to force significant metadata growth
            for (var i = 0; i < 50; i++)
            {
                modifier.AddInternalsVisibleTo($"FriendAssembly{i}");
            }
            modifier.SetAssemblyName("Shaded.AssemblyWithResources");
            modifier.Save(shadedPath);
        }

        // Verify the shaded assembly has correct data directory RVAs
        using var shadedFs = File.OpenRead(shadedPath);
        using var shadedPe = new PEReader(shadedFs);

        var shadedResourceRva = shadedPe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
        var shadedRelocRva = shadedPe.PEHeaders.PEHeader.BaseRelocationTableDirectory.RelativeVirtualAddress;
        var shadedRsrcSection = shadedPe.PEHeaders.SectionHeaders.FirstOrDefault(s => s.Name == ".rsrc");
        var shadedRelocSection = shadedPe.PEHeaders.SectionHeaders.FirstOrDefault(s => s.Name == ".reloc");

        // Verify Resource RVA is within .rsrc section (if present)
        if (shadedRsrcSection.Name == ".rsrc" && shadedResourceRva > 0)
        {
            Assert.True(
                shadedResourceRva >= shadedRsrcSection.VirtualAddress &&
                shadedResourceRva < shadedRsrcSection.VirtualAddress + shadedRsrcSection.VirtualSize,
                $"Resource RVA {shadedResourceRva} should be within .rsrc section " +
                $"({shadedRsrcSection.VirtualAddress}-{shadedRsrcSection.VirtualAddress + shadedRsrcSection.VirtualSize})");
        }

        // Verify BaseReloc RVA is within .reloc section (if present)
        if (shadedRelocSection.Name == ".reloc" && shadedRelocRva > 0)
        {
            Assert.True(
                shadedRelocRva >= shadedRelocSection.VirtualAddress &&
                shadedRelocRva < shadedRelocSection.VirtualAddress + shadedRelocSection.VirtualSize,
                $"BaseReloc RVA {shadedRelocRva} should be within .reloc section " +
                $"({shadedRelocSection.VirtualAddress}-{shadedRelocSection.VirtualAddress + shadedRelocSection.VirtualSize})");
        }

        // Verify the assembly can be loaded (the ultimate test)
        var loadContext = new AssemblyLoadContext("DataDirTest", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(shadedPath);
            using var ms = new MemoryStream(bytes);
            var asm = loadContext.LoadFromStream(ms);
            Assert.Contains("Shaded.AssemblyWithResources", asm.FullName);
        }
        finally
        {
            loadContext.Unload();
        }
    }
}
