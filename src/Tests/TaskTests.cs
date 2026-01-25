using Microsoft.Build.Framework;

[Collection("Sequential")]
public class TaskTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(TaskTests).Assembly.Location)!;
    static string testKeyFile = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");

    [Fact]
    public void Execute_WithValidInputs_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: false);

        var result = task.Execute();

        Assert.True(result);
        Assert.NotNull(task.CopyLocalPathsToRemove);
        Assert.NotNull(task.CopyLocalPathsToAdd);
    }

    [Fact]
    public void Execute_WithInternalize_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: true);

        var result = task.Execute();

        Assert.True(result);
    }

    [Fact]
    public void Execute_WithSigning_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: true, internalize: false);

        var result = task.Execute();

        Assert.True(result);
    }

    [Fact]
    public void Execute_WithSignAssemblyButNoKeyFile_ReturnsFalse()
    {
        using var tempDir = new TempDirectory();
        var buildEngine = new MockBuildEngine();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.SignAssembly = true;
        task.AssemblyOriginatorKeyFile = null;
        task.BuildEngine = buildEngine;

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, e => e.Contains("AssemblyOriginatorKeyFile not defined"));
    }

    [Fact]
    public void Execute_WithSignAssemblyAndNonExistentKeyFile_ReturnsFalse()
    {
        using var tempDir = new TempDirectory();
        var buildEngine = new MockBuildEngine();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.SignAssembly = true;
        task.AssemblyOriginatorKeyFile = Path.Combine(tempDir, "nonexistent.snk");
        task.BuildEngine = buildEngine;

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, e => e.Contains("AssemblyOriginatorKeyFile does no exist"));
    }

    [Fact]
    public void Execute_SetsOutputProperties()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: false);

        task.Execute();

        Assert.NotEmpty(task.CopyLocalPathsToRemove);
        Assert.NotEmpty(task.CopyLocalPathsToAdd);
    }

    [Fact]
    public void Execute_WithAssembliesToShade_OnlyShadesThoseAssemblies()
    {
        using var tempDir = new TempDirectory();
        SetupTestAssemblies(tempDir);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var assemblyWithPdbPath = assemblyPaths.First(p => p.Contains("AssemblyWithPdb"));
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            AssembliesToShade = [new MockTaskItem(assemblyWithPdbPath)],
            SignAssembly = false,
            Internalize = false
        };

        var result = task.Execute();

        Assert.True(result);
        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        // Only AssemblyWithPdb should be shaded with prefix
        Assert.Contains("AssemblyToProcess.AssemblyWithPdb.dll", outputFiles);
        // AssemblyWithNoSymbols should NOT be shaded (not in AssembliesToShade)
        Assert.DoesNotContain("AssemblyToProcess.AssemblyWithNoSymbols.dll", outputFiles);
        Assert.Contains("AssemblyWithNoSymbols.dll", outputFiles);
    }

    [Fact]
    public void Execute_DerivesPrefix_FromIntermediateAssemblyName()
    {
        using var tempDir = new TempDirectory();
        SetupTestAssemblies(tempDir);

        // Rename the intermediate assembly to a custom name
        var originalAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll");
        var customAssembly = Path.Combine(tempDir, "MyCustomApp.dll");
        File.Copy(originalAssembly, customAssembly);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var assemblyWithPdbPath = assemblyPaths.First(p => p.Contains("AssemblyWithPdb"));
        // Exclude the original AssemblyToProcess.dll since MyCustomApp is a copy of it
        var referenceCopyLocalPaths = assemblyPaths
            .Where(p => !p.Contains("AssemblyToProcess.dll"))
            .Select(p => new MockTaskItem(p))
            .ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = customAssembly,
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            AssembliesToShade = [new MockTaskItem(assemblyWithPdbPath)],
            SignAssembly = false,
            Internalize = false
        };

        task.Execute();

        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        // Prefix should be derived from "MyCustomApp"
        Assert.Contains("MyCustomApp.AssemblyWithPdb.dll", outputFiles);
    }

    [Fact]
    public void Execute_WithNoAssembliesToShade_DoesNothing()
    {
        using var tempDir = new TempDirectory();
        SetupTestAssemblies(tempDir);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            AssembliesToShade = null, // No assemblies to shade
            SignAssembly = false,
            Internalize = false
        };

        var result = task.Execute();

        Assert.True(result);
        // All assemblies should be in output without prefix (none shaded)
        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        Assert.DoesNotContain(outputFiles, f => f.StartsWith("AssemblyToProcess.Assembly"));
    }

    [Fact]
    public void Execute_WithEmptyAssembliesToShade_DoesNothing()
    {
        using var tempDir = new TempDirectory();
        SetupTestAssemblies(tempDir);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            AssembliesToShade = [], // Empty array
            SignAssembly = false,
            Internalize = false
        };

        var result = task.Execute();

        Assert.True(result);
        // All assemblies should be in output without prefix (none shaded)
        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        Assert.DoesNotContain(outputFiles, f => f.StartsWith("AssemblyToProcess.Assembly"));
    }

    [Fact]
    public void Execute_LogsTimingInformation()
    {
        using var tempDir = new TempDirectory();
        var buildEngine = new MockBuildEngine();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.BuildEngine = buildEngine;

        task.Execute();

        Assert.Contains(buildEngine.Messages, m => m.Contains("Finished PackageShader") && m.Contains("ms"));
    }

    [Fact]
    public void Cancel_DoesNotThrow()
    {
        var task = new ShadeTask();
        var exception = Record.Exception(() => task.Cancel());
        Assert.Null(exception);
    }

    [Fact]
    public void Execute_WithEmptyReferenceCopyLocalPaths_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        SetupIntermediateAssembly(tempDir);

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "Target.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = [],
            AssembliesToShade = null
        };

        var result = task.Execute();

        Assert.True(result);
    }

    static ShadeTask CreateTask(string tempDir, bool sign, bool internalize)
    {
        SetupTestAssemblies(tempDir);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

        // By default, shade all assemblies except the main one
        var assembliesToShade = assemblyPaths
            .Where(p => !p.Contains("AssemblyToProcess"))
            .Select(p => new MockTaskItem(p))
            .ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            AssembliesToShade = assembliesToShade,
            SignAssembly = sign,
            AssemblyOriginatorKeyFile = sign ? testKeyFile : null,
            Internalize = internalize
        };

        return task;
    }

    static void SetupTestAssemblies(string tempDir)
    {
        var assemblies = new[]
        {
            "AssemblyToProcess",
            "AssemblyWithNoSymbols",
            "AssemblyWithPdb",
            "AssemblyWithStrongName"
        };

        foreach (var assembly in assemblies)
        {
            var dllPath = Path.Combine(binDirectory, $"{assembly}.dll");
            if (File.Exists(dllPath))
            {
                File.Copy(dllPath, Path.Combine(tempDir, $"{assembly}.dll"), overwrite: true);
            }

            var pdbPath = Path.Combine(binDirectory, $"{assembly}.pdb");
            if (File.Exists(pdbPath))
            {
                File.Copy(pdbPath, Path.Combine(tempDir, $"{assembly}.pdb"), overwrite: true);
            }
        }
    }

    static void SetupIntermediateAssembly(string tempDir)
    {
        var sourceAssembly = Path.Combine(binDirectory, "AssemblyToProcess.dll");
        File.Copy(sourceAssembly, Path.Combine(tempDir, "Target.dll"), overwrite: true);
    }

    static List<string> GetTestAssemblyPaths(string tempDir) =>
        Directory.GetFiles(tempDir, "*.dll").ToList();

    class MockBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Messages { get; } = [];

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs e) =>
            Errors.Add(e.Message ?? "");

        public void LogMessageEvent(BuildMessageEventArgs e) =>
            Messages.Add(e.Message ?? "");

        public void LogWarningEvent(BuildWarningEventArgs e) =>
            Warnings.Add(e.Message ?? "");
    }

    class MockTaskItem(string itemSpec) :
        ITaskItem
    {
        public string ItemSpec { get; set; } = itemSpec;
        public int MetadataCount => 0;
        public ICollection MetadataNames => Array.Empty<string>();

        public IDictionary CloneCustomMetadata() => new Dictionary<string, string>();

        public void CopyMetadataTo(ITaskItem destinationItem)
        {
        }

        public string GetMetadata(string metadataName) => "";

        public void RemoveMetadata(string metadataName)
        {
        }

        public void SetMetadata(string metadataName, string metadataValue)
        {
        }
    }

#if !DEBUG
    [Fact]
    public async Task IncludesShadedAssembliesInPackageWhenIncludeBuildOutputIsFalse_ReleaseOnly()
    {
        using var tempDirectory = new TempDirectory();
        var projectDir = (string)tempDirectory;

        // Get the actual built version from the PackageShader.MsBuild assembly
        var packageVersion = typeof(ShadeTask).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // Create test project with IncludeBuildOutput=false (MSBuild task package pattern)
        var projectContent =
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
                <Version>1.0.0</Version>
                <LangVersion>latest</LangVersion>
                <IncludeBuildOutput>false</IncludeBuildOutput>
                <Shader_Internalize>true</Shader_Internalize>
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="PackageShader.MsBuild" Version="{packageVersion}" PrivateAssets="all" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
              </ItemGroup>
            </Project>
            """;

        var projectPath = Path.Combine(projectDir, "TestProject.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent, TestContext.Current.CancellationToken);

        // Create a minimal class file that uses the shaded dependency
        var classContent =
            """
            namespace TestProject;
            public class TestClass
            {
                public string GetJson() => Newtonsoft.Json.JsonConvert.SerializeObject(new { test = "value" });
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "TestClass.cs"), classContent, TestContext.Current.CancellationToken);

        // Create NuGet.config pointing to local PackageShader package
        var packageShaderBinPath = Path.Combine(ProjectFiles.SolutionDirectory.Path, "..", "nugets");
        var nugetConfig =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <configuration>
               <packageSources>
                 <clear />
                 <add key="local" value="{packageShaderBinPath}" />
                 <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
               </packageSources>
             </configuration>
             """;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "NuGet.config"), nugetConfig, TestContext.Current.CancellationToken);

        // Restore
        var restoreResult = await Cli.Wrap("dotnet")
            .WithArguments(["restore"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (restoreResult.ExitCode != 0)
        {
            throw new($"Restore failed:\n{restoreResult.StandardOutput}\n{restoreResult.StandardError}");
        }

        // Build
        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments(["build", "-c", "Release"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (buildResult.ExitCode != 0)
        {
            throw new($"Build failed:\n{buildResult.StandardOutput}\n{buildResult.StandardError}");
        }

        // Pack (with --no-build to test temp file persistence)
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments(["pack", "-c", "Release", "--no-build"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (packResult.ExitCode != 0)
        {
            throw new($"Pack failed:\n{packResult.StandardOutput}\n{packResult.StandardError}");
        }

        // Verify package exists
        var nupkgPath = Path.Combine(projectDir, "bin", "Release", "TestProject.1.0.0.nupkg");
        Assert.True(File.Exists(nupkgPath), $"Package not found at {nupkgPath}");

        await using var archive = await ZipFile.OpenReadAsync(nupkgPath, TestContext.Current.CancellationToken);

        // Verify shaded assembly exists in lib folder
        var shadedEntry = archive.GetEntry("lib/netstandard2.0/TestProject.Newtonsoft.Json.dll");
        Assert.NotNull(shadedEntry);

        // Verify original dependency is NOT in package (excluded by PrivateAssets and Shade)
        var originalEntry = archive.GetEntry("lib/netstandard2.0/Newtonsoft.Json.dll");
        Assert.Null(originalEntry);

        // Get all entries for verification output
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        var libEntries = entries.Where(e => e.StartsWith("lib/")).OrderBy(e => e).ToList();

        await Verify(new
        {
            LibEntries = libEntries,
            HasOriginalAssembly = originalEntry != null
        });
    }
#endif

    [Fact]
    public async Task NuGetPackExcludesShadedDependencies()
    {
        using var tempDir = new TempDirectory();

        // Create a minimal library project with a shaded PackageReference
        var projectContent = """
                             <Project Sdk="Microsoft.NET.Sdk">
                               <PropertyGroup>
                                 <TargetFramework>net8.0</TargetFramework>
                                 <PackageId>TestLibWithShadedDeps</PackageId>
                                 <Version>1.0.0</Version>
                                 <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
                               </PropertyGroup>
                               <ItemGroup>
                                 <PackageReference Include="Argon" Version="0.28.0" Shade="true" />
                               </ItemGroup>
                               <Import Project="$MsBuildTargetsPath$" />
                             </Project>
                             """;

        // Point to the actual targets file
        var targetsPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "..", "PackageShader.MsBuild", "build", "PackageShader.MsBuild.targets");
        projectContent = projectContent.Replace("$MsBuildTargetsPath$", targetsPath);

        var projectPath = Path.Combine(tempDir, "TestLib.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent, TestContext.Current.CancellationToken);

        // Create a minimal class file
        var classContent = """
                           namespace TestLib;
                           public class MyClass
                           {
                               public string GetJson() => Argon.JObject.Parse("{}").ToString();
                           }
                           """;
        await File.WriteAllTextAsync(Path.Combine(tempDir, "MyClass.cs"), classContent, TestContext.Current.CancellationToken);

        // Restore first
        await Cli.Wrap("dotnet")
            .WithArguments("restore")
            .WithWorkingDirectory(tempDir)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        // Build and Pack together (pack --no-build skips shader target which runs AfterCompile)
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments("pack -c Release -o .")
            .WithWorkingDirectory(tempDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (packResult.ExitCode != 0)
        {
            throw new($"Pack failed:\n{packResult.StandardOutput}\n{packResult.StandardError}");
        }

        // Debug: Check what files are in the output directory
        var outputDir = Path.Combine(tempDir, "bin", "Release", "net8.0");
        var outputFiles = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.dll").Select(Path.GetFileName).ToList()
            : [];

        // Inspect the nupkg
        var nupkgPath = Path.Combine(tempDir, "TestLibWithShadedDeps.1.0.0.nupkg");
        Assert.True(File.Exists(nupkgPath), $"NuGet package should exist at {nupkgPath}");

        await using var archive = await ZipFile.OpenReadAsync(nupkgPath, TestContext.Current.CancellationToken);
        var entries = archive.Entries.Select(_ => _.FullName).ToList();

        var libEntries = entries.Where(e => e.StartsWith("lib/")).ToList();

        // Read nuspec from package to verify no dependency on Argon
        var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec"));
        Assert.NotNull(nuspecEntry);

        await using var nuspecStream = await nuspecEntry.OpenAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(nuspecStream);
        var nuspecContent = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Verify Argon is NOT listed as a dependency
        Assert.DoesNotContain("Argon", nuspecContent);

        await Verify(new
        {
            OutputFiles = outputFiles.OrderBy(_ => _).ToList(),
            LibEntries = libEntries.OrderBy(_ => _).ToList(),
            NuspecHasArgonDependency = nuspecContent.Contains("Argon")
        });
    }

    [Fact]
    public async Task NuGetPackExcludesShadedDependencies_MultiTargeting()
    {
        using var tempDir = new TempDirectory();

        // Create a multi-targeted library project with shaded PackageReferences
        // that only apply to netstandard2.0 (like MarkdownSnippets.MsBuild)
        var projectContent = """
                             <Project Sdk="Microsoft.NET.Sdk">
                               <PropertyGroup>
                                 <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
                                 <PackageId>TestLibMultiTarget</PackageId>
                                 <Version>1.0.0</Version>
                                 <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
                               </PropertyGroup>
                               <ItemGroup>
                                 <!-- Shade System.Memory only for netstandard2.0, like MarkdownSnippets.MsBuild -->
                                 <PackageReference Include="System.Memory" Version="4.6.3" Shade="true" Condition="'$(TargetFramework)' == 'netstandard2.0'" />
                               </ItemGroup>
                               <Import Project="$MsBuildTargetsPath$" />
                             </Project>
                             """;

        // Point to the actual targets file
        var targetsPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "..", "PackageShader.MsBuild", "build", "PackageShader.MsBuild.targets");
        projectContent = projectContent.Replace("$MsBuildTargetsPath$", targetsPath);

        var projectPath = Path.Combine(tempDir, "TestLib.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent, TestContext.Current.CancellationToken);

        // Create a minimal class file that uses System.Memory
        var classContent = """
                           namespace TestLib
                           {
                               public class MyClass
                               {
                                   public System.ReadOnlySpan<byte> GetSpan() => System.ReadOnlySpan<byte>.Empty;
                               }
                           }
                           """;
        await File.WriteAllTextAsync(Path.Combine(tempDir, "MyClass.cs"), classContent, TestContext.Current.CancellationToken);

        // Restore first
        await Cli.Wrap("dotnet")
            .WithArguments("restore")
            .WithWorkingDirectory(tempDir)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        // Build and Pack together
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments("pack -c Release -o .")
            .WithWorkingDirectory(tempDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (packResult.ExitCode != 0)
        {
            throw new($"Pack failed:\n{packResult.StandardOutput}\n{packResult.StandardError}");
        }

        // Inspect the nupkg
        var nupkgPath = Path.Combine(tempDir, "TestLibMultiTarget.1.0.0.nupkg");
        Assert.True(File.Exists(nupkgPath), $"NuGet package should exist at {nupkgPath}");

        await using var archive = await ZipFile.OpenReadAsync(nupkgPath, TestContext.Current.CancellationToken);
        var entries = archive.Entries.Select(_ => _.FullName).ToList();

        var libEntries = entries.Where(e => e.StartsWith("lib/")).ToList();

        // Read nuspec from package to verify no dependency on System.Memory
        var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec"));
        Assert.NotNull(nuspecEntry);

        await using var nuspecStream = await nuspecEntry.OpenAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(nuspecStream);
        var nuspecContent = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Verify System.Memory is NOT listed as a dependency in any target framework group
        // This is the key assertion - before the fix, System.Memory would appear in netstandard2.0 group
        Assert.DoesNotContain("System.Memory", nuspecContent);

        // Also verify the transitive dependencies are not there
        Assert.DoesNotContain("System.Buffers", nuspecContent);
        Assert.DoesNotContain("System.Runtime.CompilerServices.Unsafe", nuspecContent);

        await Verify(new
        {
            LibEntries = libEntries.OrderBy(_ => _).ToList(),
            NuspecHasSystemMemoryDependency = nuspecContent.Contains("System.Memory")
        });
    }

    [Fact]
    public async Task BuildToolTransitiveDependenciesShouldNotTriggerValidation()
    {
        // This test reproduces the MarkdownSnippets issue:
        // - A project shades System.Memory
        // - System.IO.Pipelines also references System.Memory but is NOT shaded
        // - System.IO.Pipelines is in CopyLocal (transitive from System.Text.Json)
        // - But System.IO.Pipelines is NOT referenced by the root assembly
        // - Therefore validation should skip it (it's a "stray" dependency)
        using var tempDir = new TempDirectory();

        // Create project targeting netstandard2.0 where System.Memory needs to be shaded
        // Use Import directive to directly use local targets file (bypasses NuGet caching)
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
                <Version>1.0.0</Version>
                <LangVersion>latest</LangVersion>
                <Shader_Internalize>true</Shader_Internalize>
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
              </PropertyGroup>
              <ItemGroup>
                <!-- System.Memory needs shading on netstandard2.0 to avoid version conflicts -->
                <PackageReference Include="System.Memory" Version="4.6.3" Shade="true" />
                <!-- System.Text.Json brings in System.IO.Pipelines which also refs System.Memory -->
                <PackageReference Include="System.Text.Json" Version="10.0.2" />
              </ItemGroup>
              <Import Project="$MsBuildTargetsPath$" />
            </Project>
            """;

        // Point to the actual targets file
        var targetsPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "..", "PackageShader.MsBuild", "build", "PackageShader.MsBuild.targets");
        projectContent = projectContent.Replace("$MsBuildTargetsPath$", targetsPath);

        var projectPath = Path.Combine(tempDir, "TestProject.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent, TestContext.Current.CancellationToken);

        var classContent =
            """
            namespace TestProject;
            public class TestClass
            {
                // Uses System.Memory (via ReadOnlySpan) but NOT System.IO.Pipelines
                public System.ReadOnlySpan<byte> GetSpan() => System.ReadOnlySpan<byte>.Empty;
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(tempDir, "TestClass.cs"), classContent, TestContext.Current.CancellationToken);

        // Restore first
        await Cli.Wrap("dotnet")
            .WithArguments("restore")
            .WithWorkingDirectory(tempDir)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        // Build should succeed - System.IO.Pipelines should be skipped in validation
        // because it's not referenced by the root assembly
        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments(["build", "-c", "Release"])
            .WithWorkingDirectory(tempDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        // The build should succeed without complaining about System.IO.Pipelines
        Assert.True(buildResult.ExitCode == 0,
            $"Build failed unexpectedly:\n{buildResult.StandardOutput}\n{buildResult.StandardError}");

        // Verify that there's no validation ERROR about System.IO.Pipelines
        // (it may appear in diagnostic output listing assemblies, but should not cause an error)
        Assert.DoesNotContain("Invalid shading configuration", buildResult.StandardOutput);
        Assert.DoesNotContain("Invalid shading configuration", buildResult.StandardError);
    }

#if !DEBUG
    [Fact]
    public async Task ShadedAssembliesCoLocatedWithCustomPackagePath()
    {
        using var tempDirectory = new TempDirectory();
        var projectDir = (string)tempDirectory;

        // Get the actual built version from the PackageShader.MsBuild assembly
        var packageVersion = typeof(ShadeTask).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // Create test project with custom TfmSpecificPackageFile placing DLL in task/ folder
        var projectContent =
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Version>1.0.0</Version>
                <LangVersion>latest</LangVersion>
                <IncludeBuildOutput>false</IncludeBuildOutput>
                <Shader_Internalize>true</Shader_Internalize>
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
              </PropertyGroup>

              <ItemGroup>
                <!-- Custom package path for primary DLL in task/ folder -->
                <TfmSpecificPackageFile Include="$(OutputPath)$(TargetFileName)">
                  <PackagePath>task/$(TargetFramework)</PackagePath>
                  <Pack>true</Pack>
                </TfmSpecificPackageFile>

                <PackageReference Include="PackageShader.MsBuild" Version="{packageVersion}" PrivateAssets="all" />
                <PackageReference Include="Argon" Version="0.28.0" Shade="true" />
              </ItemGroup>
            </Project>
            """;

        var projectPath = Path.Combine(projectDir, "TestProject.csproj");
        await File.WriteAllTextAsync(projectPath, projectContent, TestContext.Current.CancellationToken);

        // Create a minimal class file that uses the shaded dependency
        var classContent =
            """
            namespace TestProject;
            public class TestClass
            {
                public string GetJson() => Argon.JObject.Parse("{}").ToString();
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "TestClass.cs"), classContent, TestContext.Current.CancellationToken);

        // Create NuGet.config pointing to local PackageShader package
        var packageShaderBinPath = Path.Combine(ProjectFiles.SolutionDirectory.Path, "..", "nugets");
        var nugetConfig =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <configuration>
               <packageSources>
                 <clear />
                 <add key="local" value="{packageShaderBinPath}" />
                 <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
               </packageSources>
             </configuration>
             """;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "NuGet.config"), nugetConfig, TestContext.Current.CancellationToken);

        // Restore
        var restoreResult = await Cli.Wrap("dotnet")
            .WithArguments(["restore"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (restoreResult.ExitCode != 0)
        {
            throw new($"Restore failed:\n{restoreResult.StandardOutput}\n{restoreResult.StandardError}");
        }

        // Build
        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments(["build", "-c", "Release"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (buildResult.ExitCode != 0)
        {
            throw new($"Build failed:\n{buildResult.StandardOutput}\n{buildResult.StandardError}");
        }

        // Pack (with --no-build to test temp file persistence)
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments(["pack", "-c", "Release", "--no-build"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        if (packResult.ExitCode != 0)
        {
            throw new($"Pack failed:\n{packResult.StandardOutput}\n{packResult.StandardError}");
        }

        // Verify package exists
        var nupkgPath = Path.Combine(projectDir, "bin", "Release", "TestProject.1.0.0.nupkg");
        Assert.True(File.Exists(nupkgPath), $"Package not found at {nupkgPath}");

        await using var archive = await ZipFile.OpenReadAsync(nupkgPath, TestContext.Current.CancellationToken);

        // Get all entries for verification
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        var taskEntries = entries.Where(e => e.StartsWith("task/")).OrderBy(e => e).ToList();
        var libEntries = entries.Where(e => e.StartsWith("lib/")).OrderBy(e => e).ToList();

        // Verify shaded assembly exists in task/ folder (co-located with primary DLL)
        var shadedEntryInTask = archive.GetEntry("task/net8.0/TestProject.Argon.dll");
        Assert.NotNull(shadedEntryInTask);

        // Verify shaded assembly does NOT exist in lib/ folder (old behavior)
        var shadedEntryInLib = archive.GetEntry("lib/net8.0/TestProject.Argon.dll");
        Assert.Null(shadedEntryInLib);

        // Verify primary DLL is in task/ folder
        var primaryEntry = archive.GetEntry("task/net8.0/TestProject.dll");
        Assert.NotNull(primaryEntry);

        await Verify(new
        {
            TaskEntries = taskEntries,
            LibEntries = libEntries,
            ShadedInLibFolder = shadedEntryInLib != null
        });
    }
#endif
}
