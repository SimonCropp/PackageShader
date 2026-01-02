namespace PackageShader;

public static class Finder
{
    public static IEnumerable<SourceTargetInfo> FindAssemblyInfos(
        IEnumerable<string> assemblyNamesToSade,
        IEnumerable<string> allFiles,
        string? prefix,
        string? suffix)
    {
        if (prefix == null && suffix == null)
        {
            throw new ErrorException("Either prefix or suffix must be defined.");
        }

        return FindAssemblyInfos(assemblyNamesToSade, allFiles, name => $"{prefix}{name}{suffix}");
    }

    static IEnumerable<SourceTargetInfo> FindAssemblyInfos(
        IEnumerable<string> assemblyNamesToShade,
        IEnumerable<string> allFiles,
        Func<string, string> getTargetName)
    {
        assemblyNamesToShade = assemblyNamesToShade.ToList();

        foreach (var file in allFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var fileDirectory = Path.GetDirectoryName(file)!;
            var isShaded = false;
            foreach (var assemblyToShade in assemblyNamesToShade)
            {
                var targetName = getTargetName(name);
                var targetPath = Path.Combine(fileDirectory, $"{targetName}.dll");

                if (assemblyToShade.EndsWith('*'))
                {
                    var match = assemblyToShade.TrimEnd('*');
                    if (name.StartsWith(match))
                    {
                        yield return new(name, file, targetName, targetPath, true);
                        isShaded = true;
                    }

                    continue;
                }

                if (name == assemblyToShade)
                {
                    yield return new(name, file, targetName, targetPath, true);
                    isShaded = true;
                }
            }

            if (!isShaded)
            {
                yield return new(name, file, name, file, false);
            }
        }
    }
}