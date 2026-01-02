public static class CommandRunner
{
    public static IEnumerable<Error> RunCommand(Invoke invoke, Action<string> log, params string[] args)
    {
        var arguments = Parser.Default.ParseArguments<Options>(args);

        if (arguments is NotParsed<Options> errors)
        {
            return errors.Errors;
        }

        var parsed = (Parsed<Options>) arguments;

        var options = parsed.Value;
        var targetDirectory = FindTargetDirectory(options.TargetDirectory);
        if (!Directory.Exists(targetDirectory))
        {
            throw new ErrorException($"Target directory does not exist: {targetDirectory}");
        }

        log($"TargetDirectory: {targetDirectory}");
        log($"Internalize: {options.Internalize}");
        var prefix = options.Prefix;
        if (prefix != null)
        {
            ValidatePrefixSuffix(prefix);
            log($"Prefix: {prefix}");
        }

        var suffix = options.Suffix;
        if (suffix != null)
        {
            ValidatePrefixSuffix(suffix);
            log($"Suffix: {suffix}");
        }

        if (prefix == null && suffix == null)
        {
            throw new ErrorException("Either prefix or suffix must be defined.");
        }

        var keyFile = options.Key;

        if (keyFile != null)
        {
            keyFile = Path.GetFullPath(keyFile);
            log($"KeyFile: {keyFile}");
            if (!File.Exists(keyFile))
            {
                throw new ErrorException($"KeyFile directory does not exist: {keyFile}");
            }
        }

        log("AssembliesToShade:");
        var assemblyToShade = options.AssembliesToShade.ToList();
        foreach (var assembly in assemblyToShade)
        {
            log($" * {assembly}");
        }

        var assembliesToExclude = options.AssembliesToExclude.ToList();

        if (assembliesToExclude.Any())
        {
            log("AssembliesToExclude:");
            foreach (var assembly in assembliesToExclude)
            {
                log($" * {assembly}");
            }
        }

        invoke(
            targetDirectory,
            assemblyToShade,
            keyFile,
            assembliesToExclude,
            prefix,
            suffix,
            options.Internalize,
            log);
        return Enumerable.Empty<Error>();
    }

    static void ValidatePrefixSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ErrorException("Prefix/Suffix must not contain whitespace");
        }
    }

    static string FindTargetDirectory(string? targetDirectory)
    {
        if (targetDirectory == null)
        {
            return Environment.CurrentDirectory;
        }

        return Path.GetFullPath(targetDirectory);
    }
}