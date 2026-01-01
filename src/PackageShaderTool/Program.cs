public static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var errors = CommandRunner.RunCommand(Inner, Console.WriteLine, args);

            if (errors.Any())
            {
                return 1;
            }

            return 0;
        }
        catch (ErrorException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    public static void Inner(
        string directory,
        List<string> assemblyNamesToShade,
        List<string> references,
        string? keyFile,
        List<string> assembliesToExclude,
        string? prefix,
        string? suffix,
        bool internalize,
        Action<string> log)
    {
        var list = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories).ToList();

        // Include all files, but mark excluded ones as not-aliased after finding matches
        var assemblyInfos = Finder.FindAssemblyInfos(assemblyNamesToShade, list, prefix, suffix)
            .Select(info =>
            {
                // If assembly matches exclusion list, mark it as not-aliased
                if (assembliesToExclude.Contains(info.SourceName))
                {
                    return info with { TargetName = info.SourceName, TargetPath = info.SourcePath, IsAlias = false };
                }
                return info;
            })
            .ToList();

        var builder = new StringBuilder("Resolved assemblies to alias:");
        builder.AppendLine();
        foreach (var assemblyInfo in assemblyInfos.Where(_ => _.IsAlias))
        {
            builder.AppendLine($" * {assemblyInfo.SourceName}");
        }

        log(builder.ToString());

        var keyPair = GetKeyPair(keyFile);

        Shader.Run(references, assemblyInfos, internalize, keyPair);

        foreach (var assembly in assemblyInfos.Where(_ => _.IsAlias))
        {
            File.Delete(assembly.SourcePath);
            var pdbPath = Path.ChangeExtension(assembly.SourcePath, "pdb");
            if (File.Exists(pdbPath))
            {
                File.Delete(pdbPath);
            }
        }
    }

    static StrongNameKey? GetKeyPair(string? keyFile)
    {
        if (keyFile == null)
        {
            return null;
        }

        return StrongNameKey.FromFile(keyFile);
    }
}
