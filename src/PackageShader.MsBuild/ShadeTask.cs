using Task = Microsoft.Build.Utilities.Task;

namespace PackageShader;

public class ShadeTask :
    Task,
    ICancelableTask
{
    [Required]
    public ITaskItem[] ReferenceCopyLocalPaths { get; set; } = null!;

    [Required]
    public string IntermediateAssembly { get; set; } = null!;

    [Required]
    public string IntermediateDirectory { get; set; } = null!;
    public string? SolutionDir { get; set; }
    public string? AssemblyOriginatorKeyFile { get; set; }

    public ITaskItem[]? AssembliesToShade { get; set; }

    public bool SignAssembly { get; set; }
    public bool Internalize { get; set; }

    [Output]
    public ITaskItem[] CopyLocalPathsToRemove { get; set; } = null!;

    [Output]
    public ITaskItem[] CopyLocalPathsToAdd { get; set; } = null!;

    /// <summary>
    /// Output: Mappings from original assembly names to shaded names.
    /// ItemSpec = shaded name (e.g., "MyApp.Newtonsoft.Json")
    /// OriginalName metadata = original name (e.g., "Newtonsoft.Json")
    /// </summary>
    [Output]
    public ITaskItem[] ShadedNameMappings { get; set; } = null!;

    public override bool Execute()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            InnerExecute();
            return true;
        }
        catch (ErrorException exception)
        {
            Log.LogError($"PackageShader: {exception}");
            return false;
        }
        finally
        {
            Log.LogMessageFromText($"Finished PackageShader {stopwatch.ElapsedMilliseconds}ms", MessageImportance.Normal);
        }
    }

    void InnerExecute()
    {
        // Derive prefix from the consuming assembly's name
        var assemblyName = Path.GetFileNameWithoutExtension(IntermediateAssembly);
        var prefix = $"{assemblyName}.";

        // Get the set of assemblies to shade from the explicit input
        var assembliesToShadeSet = new HashSet<string>(
            (AssembliesToShade ?? []).Select(_ => _.ItemSpec),
            StringComparer.OrdinalIgnoreCase);

        var referenceCopyLocalPaths = ReferenceCopyLocalPaths
            .Select(_ => _.ItemSpec)
            .ToList();
        var assemblyCopyLocalPaths = referenceCopyLocalPaths
            .Where(_ => Path.GetExtension(_).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only shade assemblies in the explicit shade list
        var assembliesToShade = assemblyCopyLocalPaths
            .Where(_ => assembliesToShadeSet.Contains(_))
            .ToList();

        // Assemblies that reference shaded ones but aren't shaded themselves
        var assembliesToTarget = assemblyCopyLocalPaths
            .Where(_ => !assembliesToShadeSet.Contains(_))
            .ToList();

        assembliesToTarget.Insert(0, IntermediateAssembly);

        var sourceTargetInfos = new List<SourceTargetInfo>();
        var copyLocalPathsToRemove = new List<ITaskItem>();
        var copyLocalPathsToAdd = new List<ITaskItem>();
        var shadedNameMappings = new List<ITaskItem>();

        void ProcessCopyLocal(string sourcePath, string targetPath)
        {
            var copyLocalToRemove = ReferenceCopyLocalPaths.SingleOrDefault(_ => _.ItemSpec == sourcePath);
            if (copyLocalToRemove != null)
            {
                copyLocalPathsToRemove.Add(copyLocalToRemove);
            }

            var pdbToRemove = Path.ChangeExtension(sourcePath, "pdb");
            copyLocalToRemove = ReferenceCopyLocalPaths.SingleOrDefault(_ => _.ItemSpec == pdbToRemove);
            if (copyLocalToRemove != null)
            {
                copyLocalPathsToRemove.Add(copyLocalToRemove);
            }

            copyLocalPathsToAdd.Add(new TaskItem(targetPath));

            var pdbToAdd = Path.ChangeExtension(targetPath, "pdb");
            if (File.Exists(pdbToAdd))
            {
                copyLocalPathsToAdd.Add(new TaskItem(pdbToAdd));
            }
        }

        foreach (var sourcePath in assembliesToShade)
        {
            var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
            var targetName = $"{prefix}{sourceName}";
            var targetPath = Path.Combine(IntermediateDirectory, $"{targetName}.dll");
            sourceTargetInfos.Add(new(sourceName, sourcePath, targetName, targetPath, true));
            ProcessCopyLocal(sourcePath, targetPath);

            // Record the mapping for deps.json patching
            var mapping = new TaskItem(targetName);
            mapping.SetMetadata("OriginalName", sourceName);
            shadedNameMappings.Add(mapping);
        }

        foreach (var sourcePath in assembliesToTarget)
        {
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var targetPath = Path.Combine(IntermediateDirectory, $"{name}.dll");
            sourceTargetInfos.Add(new(name, sourcePath, name, targetPath, false));
            ProcessCopyLocal(sourcePath, targetPath);
        }

        var separator = $"{Environment.NewLine}\t";

        var strongNameKey = GetKey();
        var inputs = $"""

                      Prefix: {prefix}
                      Internalize: {Internalize}
                      StrongName: {strongNameKey != null}
                      AssembliesToShade: {separator}{string.Join(separator, assembliesToShade.Select(Path.GetFileNameWithoutExtension))}
                      AssembliesToTarget: {separator}{string.Join(separator, assembliesToTarget.Select(Path.GetFileNameWithoutExtension))}
                      TargetInfos: {separator}{string.Join(separator, sourceTargetInfos.Select(_ => $"{_.SourceName} => {_.TargetName}"))}
                      ReferenceCopyLocalPaths: {separator}{string.Join(separator, referenceCopyLocalPaths.Select(x=> SolutionDir != null ? x.Replace(SolutionDir, "{SolutionDir}") : x))}

                      """;
        Log.LogMessageFromText(inputs, MessageImportance.High);

        Shader.Run(sourceTargetInfos, Internalize, strongNameKey);
        CopyLocalPathsToRemove = copyLocalPathsToRemove.ToArray();
        CopyLocalPathsToAdd = copyLocalPathsToAdd.ToArray();
        ShadedNameMappings = shadedNameMappings.ToArray();
    }

    StrongNameKey? GetKey()
    {
        if (!SignAssembly)
        {
            return null;
        }

        if (AssemblyOriginatorKeyFile == null)
        {
            throw new ErrorException("AssemblyOriginatorKeyFile not defined");
        }

        if (!File.Exists(AssemblyOriginatorKeyFile))
        {
            throw new ErrorException($"AssemblyOriginatorKeyFile does no exist:{AssemblyOriginatorKeyFile}");
        }

        return StrongNameKey.FromFile(AssemblyOriginatorKeyFile);
    }

    public void Cancel()
    {
    }
}
