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
    public async Task MarkdownSnippetsShading()
    {
        // This test uses actual MarkdownSnippets assemblies to verify shading works correctly
        // The net10.0 shaded assembly was being produced with an invalid format (BadImageFormatException)
        // MarkdownSnippets repo is at C:\Code\MarkdownSnippets (sibling to PackageShader repo)
        var markdownSnippetsPath = @"C:\Code\MarkdownSnippets\src\MarkdownSnippets\bin\Release\net10.0";
        var msBuildPath = @"C:\Code\MarkdownSnippets\src\MarkdownSnippets.MsBuild\bin\Release\net10.0";

        var markdownSnippetsDll = Path.Combine(markdownSnippetsPath, "MarkdownSnippets.dll");
        var msBuildDll = Path.Combine(msBuildPath, "MarkdownSnippets.MsBuild.dll");

        Assert.True(File.Exists(markdownSnippetsDll), $"MarkdownSnippets.dll not found at: {markdownSnippetsDll}");
        Assert.True(File.Exists(msBuildDll), $"MarkdownSnippets.MsBuild.dll not found at: {msBuildDll}");

        using var directory = new TempDirectory();

        // Copy the assemblies
        File.Copy(markdownSnippetsDll, Path.Combine(directory, "MarkdownSnippets.dll"));
        File.Copy(msBuildDll, Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"));

        var keyFile = @"C:\Code\MarkdownSnippets\src\key.snk";
        var key = File.Exists(keyFile) ? StrongNameKey.FromFile(keyFile) : (StrongNameKey?)null;

        var infos = new List<SourceTargetInfo>
        {
            // MarkdownSnippets.MsBuild is the root assembly (not shaded, but references MarkdownSnippets)
            new(
                "MarkdownSnippets.MsBuild",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"),
                "MarkdownSnippets.MsBuild",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"),
                IsShaded: false,
                IsRootAssembly: true
            ),
            // MarkdownSnippets is shaded
            new(
                "MarkdownSnippets",
                Path.Combine(directory, "MarkdownSnippets.dll"),
                "MarkdownSnippets.MsBuild.MarkdownSnippets",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.MarkdownSnippets.dll"),
                IsShaded: true
            )
        };

        Shader.Run(infos, internalize: true, key: key);

        // Verify shaded assembly is loadable - this was failing with BadImageFormatException
        var shadedAssemblyPath = Path.Combine(directory, "MarkdownSnippets.MsBuild.MarkdownSnippets.dll");

        // List all files in the temp directory for debugging
        var files = Directory.GetFiles(directory, "*.dll");
        Assert.True(File.Exists(shadedAssemblyPath),
            $"Shaded assembly should exist. Files in directory: {string.Join(", ", files.Select(Path.GetFileName))}");

        // Verify the shaded assembly has valid PE format by checking if we can read its assembly name
        using (var fs = File.OpenRead(shadedAssemblyPath))
        using (var peReader = new PEReader(fs))
        {
            var metadataReader = peReader.GetMetadataReader();
            var asmName = MetadataHelper.FormatAssemblyName(metadataReader);
            Assert.Contains("MarkdownSnippets.MsBuild.MarkdownSnippets", asmName);
        }

        // Compare with the assembly produced by MarkdownSnippets.MsBuild's own build
        var existingShadedPath = Path.Combine(msBuildPath, "MarkdownSnippets.MsBuild.MarkdownSnippets.dll");
        if (File.Exists(existingShadedPath))
        {
            // Read both assemblies and compare their PE characteristics
            using var existingFs = File.OpenRead(existingShadedPath);
            using var existingPeReader = new PEReader(existingFs);

            using var newFs = File.OpenRead(shadedAssemblyPath);
            using var newPeReader = new PEReader(newFs);

            var existingHeaders = existingPeReader.PEHeaders;
            var newHeaders = newPeReader.PEHeaders;

            // Check if there's a machine type mismatch
            var existingMachine = existingHeaders.CoffHeader.Machine;
            var newMachine = newHeaders.CoffHeader.Machine;

            // Check for CorFlags (determines managed code characteristics)
            var existingCorFlags = existingHeaders.CorHeader?.Flags ?? 0;
            var newCorFlags = newHeaders.CorHeader?.Flags ?? 0;

            // Also check the source MarkdownSnippets.dll
            var sourceMarkdownSnippets = Path.Combine(markdownSnippetsPath, "MarkdownSnippets.dll");
            using var sourceFs = File.OpenRead(sourceMarkdownSnippets);
            using var sourcePeReader = new PEReader(sourceFs);
            var sourceHeaders = sourcePeReader.PEHeaders;

            // Check 32BIT_REQUIRED flag (value 2 in CorFlags)
            var source32BitRequired = (sourceHeaders.CorHeader?.Flags & CorFlags.Requires32Bit) != 0;
            var existing32BitRequired = (existingCorFlags & CorFlags.Requires32Bit) != 0;
            var new32BitRequired = (newCorFlags & CorFlags.Requires32Bit) != 0;

            var comparison = $@"
PE Comparison:
  Source MarkdownSnippets.dll (original):
    Machine: {sourceHeaders.CoffHeader.Machine}
    CorFlags: {sourceHeaders.CorHeader?.Flags}
    PE Magic: {sourceHeaders.PEHeader?.Magic}
    32BIT_REQUIRED: {source32BitRequired}

  Existing shaded assembly (from MarkdownSnippets.MsBuild build):
    Machine: {existingMachine}
    CorFlags: {existingCorFlags}
    PE Magic: {existingHeaders.PEHeader?.Magic}
    32BIT_REQUIRED: {existing32BitRequired}

  New shaded assembly (from Shader.Run in test):
    Machine: {newMachine}
    CorFlags: {newCorFlags}
    PE Magic: {newHeaders.PEHeader?.Magic}
    32BIT_REQUIRED: {new32BitRequired}
";
            // Try to load the NEW shaded assembly from our test using MemoryStream to avoid file locks
            var newBytes = File.ReadAllBytes(shadedAssemblyPath);
            var newTestContext = new AssemblyLoadContext("NewTestLoadContext", isCollectible: true);
            try
            {
                using var newStream = new MemoryStream(newBytes);
                var newLoadedAsm = newTestContext.LoadFromStream(newStream);
                var newTypes = newLoadedAsm.GetTypes();
                // New assembly loads successfully - this proves Shader.Run works
            }
            catch (BadImageFormatException ex)
            {
                throw new($"NEW shaded assembly FAILED to load: {ex.Message}");
            }
            finally
            {
                newTestContext.Unload();
            }

            // Try to load the EXISTING shaded assembly (now expected to succeed after the fix)
            var existingBytes = File.ReadAllBytes(existingShadedPath);
            var existingTestContext = new AssemblyLoadContext("ExistingTestLoadContext", isCollectible: true);
            try
            {
                using var existingStream = new MemoryStream(existingBytes);
                var existingLoadedAsm = existingTestContext.LoadFromStream(existingStream);
                var existingTypes = existingLoadedAsm.GetTypes();
                // After the fix, both assemblies should load successfully
            }
            catch (BadImageFormatException ex)
            {
                throw new($"Existing shaded assembly still has Bad IL format - fix may not be complete: {ex.Message}. {comparison}");
            }
            finally
            {
                existingTestContext.Unload();
            }
        }

        // Test passes - we've confirmed both assemblies load successfully
        // This verifies the fix for the section VA overlap bug is working
    }

    [Fact]
    public async Task MarkdownSnippetsShadingWithOriginalUnshaded()
    {
        // This test uses the ORIGINAL UNSHADED assemblies to exactly replicate MSBuild scenario
        // The original files were captured by building MarkdownSnippets.MsBuild without PackageShader
        var testDataDir = Path.Combine(ProjectFiles.SolutionDirectory.Path, "Tests", "TestData", "MarkdownSnippetsOriginal");
        var originalMsBuildDll = Path.Combine(testDataDir, "MarkdownSnippets.MsBuild.dll");
        var originalMarkdownSnippetsDll = Path.Combine(testDataDir, "MarkdownSnippets.dll");

        Assert.True(File.Exists(originalMsBuildDll), $"Original MarkdownSnippets.MsBuild.dll not found at: {originalMsBuildDll}");
        Assert.True(File.Exists(originalMarkdownSnippetsDll), $"Original MarkdownSnippets.dll not found at: {originalMarkdownSnippetsDll}");

        using var directory = new TempDirectory();

        // Copy the original unshaded assemblies
        File.Copy(originalMsBuildDll, Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"));
        File.Copy(originalMarkdownSnippetsDll, Path.Combine(directory, "MarkdownSnippets.dll"));

        var keyFile = @"C:\Code\MarkdownSnippets\src\key.snk";
        var key = File.Exists(keyFile) ? StrongNameKey.FromFile(keyFile) : (StrongNameKey?)null;

        // Setup exactly like MSBuild does:
        // - MarkdownSnippets is shaded (IsShaded: true)
        // - MarkdownSnippets.MsBuild is the root assembly (IsShaded: false, IsRootAssembly: true)
        // - Source and target path for root assembly are the same (in-place modification)
        var infos = new List<SourceTargetInfo>
        {
            // MarkdownSnippets is shaded
            new(
                "MarkdownSnippets",
                Path.Combine(directory, "MarkdownSnippets.dll"),
                "MarkdownSnippets.MsBuild.MarkdownSnippets",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.MarkdownSnippets.dll"),
                IsShaded: true
            ),
            // MarkdownSnippets.MsBuild is the root assembly with SAME source and target path
            new(
                "MarkdownSnippets.MsBuild",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"),
                "MarkdownSnippets.MsBuild",
                Path.Combine(directory, "MarkdownSnippets.MsBuild.dll"),
                IsShaded: false,
                IsRootAssembly: true
            )
        };

        Shader.Run(infos, internalize: true, key: key);

        // Verify the shaded assembly is loadable
        var shadedPath = Path.Combine(directory, "MarkdownSnippets.MsBuild.MarkdownSnippets.dll");
        Assert.True(File.Exists(shadedPath), "Shaded assembly should exist");

        var shadedBytes = File.ReadAllBytes(shadedPath);
        var testContext = new AssemblyLoadContext("TestLoadContext", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(shadedBytes);
            var loadedAsm = testContext.LoadFromStream(stream);
            var types = loadedAsm.GetTypes();
            // Assembly loads successfully
        }
        catch (BadImageFormatException ex)
        {
            throw new($"Shaded assembly is corrupt: {ex.Message}");
        }
        finally
        {
            testContext.Unload();
        }

        // Also verify the root assembly was updated correctly
        var rootPath = Path.Combine(directory, "MarkdownSnippets.MsBuild.dll");
        var rootBytes = File.ReadAllBytes(rootPath);
        var rootContext = new AssemblyLoadContext("RootTestContext", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(rootBytes);
            var loadedAsm = rootContext.LoadFromStream(stream);

            // Verify references were updated
            var refs = loadedAsm.GetReferencedAssemblies();
            var hasShaded = refs.Any(r => r.Name == "MarkdownSnippets.MsBuild.MarkdownSnippets");
            var hasOriginal = refs.Any(r => r.Name == "MarkdownSnippets");
            Assert.True(hasShaded, "Root assembly should reference the shaded name");
            Assert.False(hasOriginal, "Root assembly should not reference the original name");
        }
        finally
        {
            rootContext.Unload();
        }

        // Now compare with the MSBuild output if it exists
        var msBuildCorruptPath = @"C:\Code\MarkdownSnippets\src\MarkdownSnippets.MsBuild\obj\Release\net10.0\MarkdownSnippets.MsBuild.MarkdownSnippets.dll";
        if (File.Exists(msBuildCorruptPath))
        {
            // Read both files and compare PE structure
            var testBytes = File.ReadAllBytes(shadedPath);
            var msBuildBytes = File.ReadAllBytes(msBuildCorruptPath);

            using var testStream = new MemoryStream(testBytes);
            using var msBuildStream = new MemoryStream(msBuildBytes);

            using var testPE = new PEReader(testStream);
            using var msBuildPE = new PEReader(msBuildStream);

            var comparison = new StringBuilder();
            comparison.AppendLine("PE Comparison (Test vs MSBuild):");
            comparison.AppendLine($"  File size: {testBytes.Length} vs {msBuildBytes.Length}");
            comparison.AppendLine($"  Has metadata: {testPE.HasMetadata} vs {msBuildPE.HasMetadata}");

            var testHeaders = testPE.PEHeaders;
            var msBuildHeaders = msBuildPE.PEHeaders;

            comparison.AppendLine($"  Machine: {testHeaders.CoffHeader.Machine} vs {msBuildHeaders.CoffHeader.Machine}");
            comparison.AppendLine($"  Characteristics: {testHeaders.CoffHeader.Characteristics} vs {msBuildHeaders.CoffHeader.Characteristics}");
            comparison.AppendLine($"  PE Magic: {testHeaders.PEHeader?.Magic} vs {msBuildHeaders.PEHeader?.Magic}");
            comparison.AppendLine($"  CorFlags: {testHeaders.CorHeader?.Flags} vs {msBuildHeaders.CorHeader?.Flags}");
            comparison.AppendLine($"  Section count: {testHeaders.SectionHeaders.Length} vs {msBuildHeaders.SectionHeaders.Length}");

            // If metadata is valid but Assembly.LoadFrom fails, the IL might be corrupt
            // This suggests the RVA patching or IL body relocation might be wrong

            // Let's compare the method RVAs between the two files
            var testMd2 = testPE.GetMetadataReader();
            var msBuildMd2 = msBuildPE.GetMetadataReader();

            comparison.AppendLine($"  Test metadata: OK, assembly name: {testMd2.GetString(testMd2.GetAssemblyDefinition().Name)}");
            comparison.AppendLine($"  MSBuild metadata: OK, assembly name: {msBuildMd2.GetString(msBuildMd2.GetAssemblyDefinition().Name)}");

            comparison.AppendLine("\nMethod comparison:");
            var testMethodCount = testMd2.MethodDefinitions.Count;
            var msBuildMethodCount = msBuildMd2.MethodDefinitions.Count;
            comparison.AppendLine($"  Method count: {testMethodCount} vs {msBuildMethodCount}");

            // Compare first few method RVAs
            var compareCount = Math.Min(5, Math.Min(testMethodCount, msBuildMethodCount));
            var testMethodHandles = testMd2.MethodDefinitions.ToArray();
            var msBuildMethodHandles = msBuildMd2.MethodDefinitions.ToArray();

            for (var i = 0; i < compareCount; i++)
            {
                var testMethod = testMd2.GetMethodDefinition(testMethodHandles[i]);
                var msBuildMethod = msBuildMd2.GetMethodDefinition(msBuildMethodHandles[i]);
                var testName = testMd2.GetString(testMethod.Name);
                var msBuildName = msBuildMd2.GetString(msBuildMethod.Name);
                comparison.AppendLine($"  Method {i}: '{testName}' RVA={testMethod.RelativeVirtualAddress} vs '{msBuildName}' RVA={msBuildMethod.RelativeVirtualAddress}");
            }

            // Test if we can read method bodies from the PE
            comparison.AppendLine("\nMethod body validation:");
            try
            {
                for (var i = 0; i < Math.Min(3, testMethodCount); i++)
                {
                    var method = testMd2.GetMethodDefinition(testMethodHandles[i]);
                    if (method.RelativeVirtualAddress != 0)
                    {
                        var body = testPE.GetMethodBody(method.RelativeVirtualAddress);
                        var ilBytes = body.GetILBytes();
                        comparison.AppendLine($"  Test method {i}: IL size = {ilBytes?.Length ?? 0}");
                    }
                }
            }
            catch (Exception ex)
            {
                comparison.AppendLine($"  Test method body read FAILED: {ex.Message}");
            }

            try
            {
                for (var i = 0; i < Math.Min(3, msBuildMethodCount); i++)
                {
                    var method = msBuildMd2.GetMethodDefinition(msBuildMethodHandles[i]);
                    if (method.RelativeVirtualAddress != 0)
                    {
                        var body = msBuildPE.GetMethodBody(method.RelativeVirtualAddress);
                        var ilBytes = body.GetILBytes();
                        comparison.AppendLine($"  MSBuild method {i}: IL size = {ilBytes?.Length ?? 0}");
                    }
                }
            }
            catch (Exception ex)
            {
                comparison.AppendLine($"  MSBuild method body read FAILED: {ex.Message}");
            }

            // Compare sections
            comparison.AppendLine("\nSection comparison:");
            for (var s = 0; s < testHeaders.SectionHeaders.Length; s++)
            {
                var testSection = testHeaders.SectionHeaders[s];
                var msBuildSection = msBuildHeaders.SectionHeaders[s];
                var testSectionName = testSection.Name;
                var msBuildSectionName = msBuildSection.Name;
                comparison.AppendLine($"  Section {s}: '{testSectionName}' vs '{msBuildSectionName}'");
                comparison.AppendLine($"    VirtualAddress: {testSection.VirtualAddress} vs {msBuildSection.VirtualAddress}");
                comparison.AppendLine($"    VirtualSize: {testSection.VirtualSize} vs {msBuildSection.VirtualSize}");
                comparison.AppendLine($"    SizeOfRawData: {testSection.SizeOfRawData} vs {msBuildSection.SizeOfRawData}");
                comparison.AppendLine($"    PointerToRawData: {testSection.PointerToRawData} vs {msBuildSection.PointerToRawData}");
            }

            // Check strong name directory
            comparison.AppendLine("\nCLR Header:");
            var testCor = testHeaders.CorHeader;
            var msBuildCor = msBuildHeaders.CorHeader;
            if (testCor != null && msBuildCor != null)
            {
                comparison.AppendLine($"  EntryPointToken: {testCor.EntryPointTokenOrRelativeVirtualAddress} vs {msBuildCor.EntryPointTokenOrRelativeVirtualAddress}");
                comparison.AppendLine($"  StrongNameSignature RVA: {testCor.StrongNameSignatureDirectory.RelativeVirtualAddress} vs {msBuildCor.StrongNameSignatureDirectory.RelativeVirtualAddress}");
                comparison.AppendLine($"  StrongNameSignature Size: {testCor.StrongNameSignatureDirectory.Size} vs {msBuildCor.StrongNameSignatureDirectory.Size}");
                comparison.AppendLine($"  MetadataDirectory RVA: {testCor.MetadataDirectory.RelativeVirtualAddress} vs {msBuildCor.MetadataDirectory.RelativeVirtualAddress}");
                comparison.AppendLine($"  MetadataDirectory Size: {testCor.MetadataDirectory.Size} vs {msBuildCor.MetadataDirectory.Size}");
                comparison.AppendLine($"  Resources RVA: {testCor.ResourcesDirectory.RelativeVirtualAddress} vs {msBuildCor.ResourcesDirectory.RelativeVirtualAddress}");
                comparison.AppendLine($"  Resources Size: {testCor.ResourcesDirectory.Size} vs {msBuildCor.ResourcesDirectory.Size}");
            }

            // Check metadata tables
            comparison.AppendLine("\nMetadata tables:");
            comparison.AppendLine($"  AssemblyRef count: {testMd2.AssemblyReferences.Count} vs {msBuildMd2.AssemblyReferences.Count}");
            comparison.AppendLine($"  TypeRef count: {testMd2.TypeReferences.Count} vs {msBuildMd2.TypeReferences.Count}");
            comparison.AppendLine($"  MemberRef count: {testMd2.MemberReferences.Count} vs {msBuildMd2.MemberReferences.Count}");
            comparison.AppendLine($"  TypeDef count: {testMd2.TypeDefinitions.Count} vs {msBuildMd2.TypeDefinitions.Count}");
            comparison.AppendLine($"  CustomAttribute count: {testMd2.CustomAttributes.Count} vs {msBuildMd2.CustomAttributes.Count}");

            // Debug comparison for manual inspection
            // The MSBuild output was created before the VA fix, so section addresses don't match
            // This is expected - the fix should make new builds produce valid output
        }
    }

    [Fact]
    public async Task MarkdownSnippetsShadingWithNetStandard20()
    {
        // This test uses the RELEASE NETSTANDARD2.0 build of PackageShader to reproduce the MSBuild issue
        // The MSBuild task uses the Release netstandard2.0 PackageShader.dll from NuGet
        var netStandard20Path = @"C:\Code\PackageShader\src\PackageShader\bin\Release\netstandard2.0\PackageShader.dll";

        Assert.True(File.Exists(netStandard20Path), $"netstandard2.0 build not found at: {netStandard20Path}");

        var markdownSnippetsPath = @"C:\Code\MarkdownSnippets\src\MarkdownSnippets\bin\Release\net10.0";
        var markdownSnippetsDll = Path.Combine(markdownSnippetsPath, "MarkdownSnippets.dll");
        Assert.True(File.Exists(markdownSnippetsDll), $"MarkdownSnippets.dll not found");

        using var directory = new TempDirectory();

        // Copy the source assembly
        File.Copy(markdownSnippetsDll, Path.Combine(directory, "MarkdownSnippets.dll"));

        // Load netstandard2.0 PackageShader using AssemblyLoadContext
        var loadContext = new AssemblyLoadContext("NetStandard20PackageShader", isCollectible: true);
        try
        {
            var netStdAsm = loadContext.LoadFromAssemblyPath(netStandard20Path);

            // Get Shader type and Run method
            var shaderType = netStdAsm.GetType("PackageShader.Shader");
            Assert.NotNull(shaderType);

            var runMethod = shaderType!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(runMethod);

            // Get SourceTargetInfo type
            var sourceTargetInfoType = netStdAsm.GetType("PackageShader.SourceTargetInfo");
            Assert.NotNull(sourceTargetInfoType);

            // Create list of SourceTargetInfo
            var listType = typeof(List<>).MakeGenericType(sourceTargetInfoType!);
            var infos = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add");

            // Create SourceTargetInfo for the shaded assembly
            // Record constructor: (SourceName, SourcePath, TargetName, TargetPath, IsShaded, IsRootAssembly)
            var info = Activator.CreateInstance(sourceTargetInfoType,
                "MarkdownSnippets",                                    // SourceName
                Path.Combine(directory, "MarkdownSnippets.dll"),      // SourcePath
                "Test.MarkdownSnippets",                              // TargetName
                Path.Combine(directory, "Test.MarkdownSnippets.dll"), // TargetPath
                true,                                                  // IsShaded
                false);                                                // IsRootAssembly
            addMethod!.Invoke(infos, [info]);

            // Call Shader.Run using reflection
            runMethod!.Invoke(null, [infos, true, null]);

            // Verify the shaded assembly was created
            var shadedPath = Path.Combine(directory, "Test.MarkdownSnippets.dll");
            Assert.True(File.Exists(shadedPath), "Shaded assembly should exist");

            // Try to load the shaded assembly
            var shadedBytes = File.ReadAllBytes(shadedPath);
            var testContext = new AssemblyLoadContext("TestLoadContext", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(shadedBytes);
                var loadedAsm = testContext.LoadFromStream(stream);
                var types = loadedAsm.GetTypes();
                // If we get here, the netstandard2.0 build works correctly
            }
            catch (BadImageFormatException ex)
            {
                // This would indicate the netstandard2.0 build has the same bug
                throw new($"NETSTANDARD2.0 PackageShader.dll produces corrupt assemblies: {ex.Message}");
            }
            finally
            {
                testContext.Unload();
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }

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
