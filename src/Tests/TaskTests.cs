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
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

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

        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public void LogErrorEvent(BuildErrorEventArgs e) =>
            Errors.Add(e.Message ?? "");

        public void LogMessageEvent(BuildMessageEventArgs e) =>
            Messages.Add(e.Message ?? "");

        public void LogWarningEvent(BuildWarningEventArgs e) =>
            Warnings.Add(e.Message ?? "");
    }

    class MockTaskItem : ITaskItem
    {
        public MockTaskItem(string itemSpec) =>
            ItemSpec = itemSpec;

        public string ItemSpec { get; set; }
        public int MetadataCount => 0;
        public ICollection MetadataNames => Array.Empty<string>();

        public IDictionary CloneCustomMetadata() => new Dictionary<string, string>();
        public void CopyMetadataTo(ITaskItem destinationItem) { }
        public string GetMetadata(string metadataName) => "";
        public void RemoveMetadata(string metadataName) { }
        public void SetMetadata(string metadataName, string metadataValue) { }
    }
}
