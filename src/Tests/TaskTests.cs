using System.Collections;
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
    public void Execute_WithAssembliesToSkipRename_ExcludesThoseAssemblies()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.AssembliesToSkipRename = [new MockTaskItem("AssemblyWithNoSymbols")];

        var result = task.Execute();

        Assert.True(result);
        // The skipped assembly should be in output without the suffix
        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        Assert.Contains("AssemblyWithNoSymbols.dll", outputFiles);
        Assert.DoesNotContain("AssemblyWithNoSymbols_Shaded.dll", outputFiles);
    }

    [Fact]
    public void Execute_AppliesPrefixAndSuffix()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.Prefix = "Pre_";
        task.Suffix = "_Suf";

        task.Execute();

        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        Assert.Contains(outputFiles, f => f.StartsWith("Pre_") && f.Contains("_Suf.dll"));
    }

    [Fact]
    public void Execute_WithPrefixOnly_Works()
    {
        using var tempDir = new TempDirectory();
        var task = CreateTask(tempDir, sign: false, internalize: false);
        task.Prefix = "Prefix_";
        task.Suffix = null;

        var result = task.Execute();

        Assert.True(result);
        var outputFiles = task.CopyLocalPathsToAdd.Select(i => Path.GetFileName(i.ItemSpec)).ToList();
        Assert.Contains(outputFiles, f => f.StartsWith("Prefix_"));
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
            ReferencePath = [],
            Suffix = "_Shaded"
        };

        var result = task.Execute();

        Assert.True(result);
    }

    static ShadeTask CreateTask(string tempDir, bool sign, bool internalize)
    {
        SetupTestAssemblies(tempDir);

        var assemblyPaths = GetTestAssemblyPaths(tempDir);
        var referenceCopyLocalPaths = assemblyPaths.Select(p => new MockTaskItem(p)).ToArray();

        var task = new ShadeTask
        {
            BuildEngine = new MockBuildEngine(),
            IntermediateAssembly = Path.Combine(tempDir, "AssemblyToProcess.dll"),
            IntermediateDirectory = tempDir,
            ReferenceCopyLocalPaths = referenceCopyLocalPaths,
            ReferencePath = [],
            Suffix = "_Shaded",
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
