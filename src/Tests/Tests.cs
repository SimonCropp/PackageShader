using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PackageShader;

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
        Program.Inner(tempPath, namesToShade, new(), keyFile, new(), null, "_Shaded", internalize, _ =>
        {
        });

        return BuildResults(tempPath);
    }

    static IEnumerable<AssemblyResult> BuildResults(string tempPath)
    {
        var resultingFiles = Directory.EnumerateFiles(tempPath);
        foreach (var assemblyPath in resultingFiles.Where(_ => _.EndsWith(".dll")).OrderBy(_ => _))
        {
            using var fileStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(fileStream);
            var metadataReader = peReader.GetMetadataReader();

            var assemblyName = FormatAssemblyName(metadataReader);
            var hasSymbols = HasSymbols(assemblyPath);
            var references = GetAssemblyReferences(metadataReader)
                .OrderBy(_ => _)
                .ToList();
            var attributes = GetAssemblyCustomAttributes(metadataReader)
                .Where(_ => _.typeName.Contains("Internals"))
                .Select(_ => $"{_.typeName}({_.argument})")
                .OrderBy(_ => _)
                .ToList();
            yield return new(assemblyName, hasSymbols, references, attributes);
        }
    }

    static string FormatAssemblyName(MetadataReader reader)
    {
        var assemblyDef = reader.GetAssemblyDefinition();
        var name = reader.GetString(assemblyDef.Name);
        var version = assemblyDef.Version;
        var culture = reader.GetString(assemblyDef.Culture);
        var cultureStr = string.IsNullOrEmpty(culture) ? "neutral" : culture;
        var publicKey = reader.GetBlobBytes(assemblyDef.PublicKey);
        var tokenStr = FormatPublicKeyToken(publicKey);

        return $"{name}, Version={version}, Culture={cultureStr}, PublicKeyToken={tokenStr}";
    }

    static string FormatAssemblyRefName(MetadataReader reader, AssemblyReference assemblyRef)
    {
        var name = reader.GetString(assemblyRef.Name);
        var version = assemblyRef.Version;
        var culture = reader.GetString(assemblyRef.Culture);
        var cultureStr = string.IsNullOrEmpty(culture) ? "neutral" : culture;
        var publicKeyOrToken = reader.GetBlobBytes(assemblyRef.PublicKeyOrToken);
        var tokenStr = publicKeyOrToken.Length == 8
            ? BitConverter.ToString(publicKeyOrToken).Replace("-", "").ToLowerInvariant()
            : FormatPublicKeyToken(publicKeyOrToken);

        return $"{name}, Version={version}, Culture={cultureStr}, PublicKeyToken={tokenStr}";
    }

    static string FormatPublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
            return "null";

        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);

        // Token is last 8 bytes reversed
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];

        return BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
    }

    static IEnumerable<string> GetAssemblyReferences(MetadataReader reader)
    {
        foreach (var refHandle in reader.AssemblyReferences)
        {
            var assemblyRef = reader.GetAssemblyReference(refHandle);
            yield return FormatAssemblyRefName(reader, assemblyRef);
        }
    }

    static IEnumerable<(string typeName, string argument)> GetAssemblyCustomAttributes(MetadataReader reader)
    {
        var assemblyToken = EntityHandle.AssemblyDefinition;
        foreach (var attrHandle in reader.GetCustomAttributes(assemblyToken))
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var typeName = GetAttributeTypeName(reader, attr);
            var argument = ParseAttributeArgument(reader, attr);
            yield return (typeName, argument);
        }
    }

    static string GetAttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                return reader.GetString(typeRef.Name);
            }
        }
        return string.Empty;
    }

    static string ParseAttributeArgument(MetadataReader reader, CustomAttribute attr)
    {
        var blob = reader.GetBlobBytes(attr.Value);
        if (blob.Length < 2)
            return string.Empty;

        // Skip prolog (2 bytes: 0x01 0x00)
        if (blob[0] != 0x01 || blob[1] != 0x00)
            return string.Empty;

        // Try to read a string argument
        var offset = 2;
        if (offset >= blob.Length)
            return string.Empty;

        // Read compressed length
        int length;
        if ((blob[offset] & 0x80) == 0)
        {
            length = blob[offset];
            offset++;
        }
        else if ((blob[offset] & 0xC0) == 0x80)
        {
            if (offset + 1 >= blob.Length) return string.Empty;
            length = ((blob[offset] & 0x3F) << 8) | blob[offset + 1];
            offset += 2;
        }
        else
        {
            if (offset + 3 >= blob.Length) return string.Empty;
            length = ((blob[offset] & 0x1F) << 24) | (blob[offset + 1] << 16) |
                     (blob[offset + 2] << 8) | blob[offset + 3];
            offset += 4;
        }

        if (offset + length > blob.Length)
            return string.Empty;

        return System.Text.Encoding.UTF8.GetString(blob, offset, length);
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
            using var peReader = new PEReader(fileStream);

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


    public static bool HasSymbols(string path)
    {
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdbPath))
        {
            return true;
        }

        return HasEmbeddedPdb(path);
    }

    public static bool HasEmbeddedPdb(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var debug = peReader.ReadDebugDirectory();

        return Enumerable.Any(debug, _ => _.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
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
            bool hasExternalPdb;
            bool hasEmbeddedPdb;
            (hasExternalPdb, hasEmbeddedPdb) = ReadSmybolsInfo(dllPath);

            Debug.WriteLine(hasExternalPdb);
            Debug.WriteLine(hasEmbeddedPdb);
            if (hasExternalPdb || hasEmbeddedPdb)
            {
                assembliesWithSymbolsChecked++;
            }

        }

        Assert.True(assembliesWithSymbolsChecked > 0, "Should have checked at least one assembly with symbols");
    }

    static (bool hasExternalPdb, bool hasEmbeddedPdb) ReadSmybolsInfo(string dllPath) =>
        (HasExternalSymbols(dllPath), HasEmbeddedSymbols(dllPath));

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

    static bool HasEmbeddedSymbols(string dllPath)
    {
        using var dllStream = File.OpenRead(dllPath);
        using var peReader = new PEReader(dllStream);

        var embeddedEntries = peReader.ReadDebugDirectory()
            .Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            .ToList();

        if (embeddedEntries.Count == 0)
        {
            return false;
        }

        foreach (var entry in embeddedEntries)
        {
            using var symbolReader = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            ValidatePdbReader(symbolReader);
        }

        return true;
    }

    [Fact]
    public void ModifiedAssemblyWithEmbeddedPdbHasValidSymbols()
    {
        using var directory = new TempDirectory();
        // copyPdbs=false but AssemblyWithEmbeddedSymbols has embedded PDB
        Run(copyPdbs: false, sign: false, internalize: false, directory);

        var embeddedSymbolsPath = Path.Combine(directory, "AssemblyWithEmbeddedSymbols_Shaded.dll");
        Assert.True(File.Exists(embeddedSymbolsPath), "AssemblyWithEmbeddedSymbols_Shaded.dll should exist");
        Assert.True(HasEmbeddedPdb(embeddedSymbolsPath), "Should have embedded PDB");

        using var dllStream = File.OpenRead(embeddedSymbolsPath);
        using var peReader = new PEReader(dllStream);

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
            references: [],
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
                IsAlias: true))
            .ToList();

        Shader.Run([], infos, internalize: false, key: null);

        // Delete original assemblies (Shader.Run doesn't do this)
        foreach (var info in infos)
        {
            File.Delete(info.SourcePath);
        }

        // Use same BuildResults verification as Combo tests
        var results = BuildResults(directory);
        await Verify(results);
    }
}

public record AssemblyResult(string Name, bool HasSymbols, List<string> References, List<string> Attributes);