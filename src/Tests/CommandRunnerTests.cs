public class CommandRunnerTests
{
    [Fact]
    public Task MissingAssembliesToShade()
    {
        var result = Parse("--target-directory directory --suffix _Shaded");
        return Verify(result);
    }

    [Fact]
    public Task All()
    {
        Directory.CreateDirectory("directory");
        var result = Parse("--target-directory directory --suffix _Shaded --prefix Shaded_ --key test.snk --assemblies-to-shade assembly");
        return Verify(result);
    }

    [Fact]
    public Task Prefix()
    {
        var result = Parse("--prefix Shaded_ --assemblies-to-shade assembly");
        return Verify(result);
    }

    [Fact]
    public Task Suffix()
    {
        var result = Parse("--suffix _Shaded --assemblies-to-shade assembly");
        return Verify(result);
    }

    [Fact]
    public Task NoPrefixOrSuffix() =>
        Throws(() => Parse("--assemblies-to-shade assembly"));

    [Fact]
    public Task BadKeyPath() =>
        Throws(() => Parse("--key bad.snk --assemblies-to-shade assembly --suffix _Shaded"));

    [Fact]
    public Task KeyRelative()
    {
        var result = Parse("--key test.snk --assemblies-to-shade assembly --suffix _Shaded");
        return Verify(result);
    }

    [Fact]
    public Task KeyFull()
    {
        var result = Parse($"--key {Environment.CurrentDirectory}/test.snk --assemblies-to-shade assembly --suffix _Shaded");
        return Verify(result);
    }

    [Fact]
    public Task CurrentDirectory()
    {
        var result = Parse("--assemblies-to-shade assembly --suffix _Shaded");
        return Verify(result);
    }

    [Fact]
    public Task MultipleAssemblies()
    {
        var result = Parse("--assemblies-to-shade assembly1;assembly2 --suffix _Shaded");
        return Verify(result);
    }

    //[Fact]
    //public Task MultipleAssembliesSplit()
    //{
    //    var result = Parse("--assemblies-to-shade", "assembly2", "--assemblies-to-shade", "assembly2 --suffix _Shaded");
    //    return Verifier.Verify(result);
    //}

    static Result Parse(string input)
    {
        var consoleOut = new StringBuilder();
        string? directory = null;
        string? key = null;
        string? prefix = null;
        string? suffix = null;
        var internalize = false;
        IEnumerable<string>? assembliesToShade = null;
        IEnumerable<string>? assembliesToExclude = null;
        var result = CommandRunner.RunCommand(
            (_directory, _assembliesToShade, _key, _assembliesToExclude, _prefix, _suffix, _internalize, _) =>
            {
                directory = _directory;
                key = _key;
                assembliesToShade = _assembliesToShade;
                assembliesToExclude = _assembliesToExclude;
                prefix = _prefix;
                suffix = _suffix;
                internalize = _internalize;
            },
            line => consoleOut.AppendLine(line),
            input.Split(' '));
        return new(result, directory, prefix, suffix, key, assembliesToShade, assembliesToExclude, consoleOut.ToString(), internalize);
    }

    public record Result(
        IEnumerable<Error> errors,
        string? directory,
        string? prefix,
        string? suffix,
        string? key,
        IEnumerable<string>? assembliesToShade,
        IEnumerable<string>? assembliesToExclude,
        string consoleOut,
        bool internalize);

    // -------------------------------------------------------------------------
    // Direct tests of ValidatePrefixSuffix
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePrefixSuffix_Valid_NoThrow()
    {
        CommandRunner.ValidatePrefixSuffix("Shaded_");
        CommandRunner.ValidatePrefixSuffix("_Shaded");
    }

    [Fact]
    public void ValidatePrefixSuffix_Empty_Throws() =>
        Assert.Throws<ErrorException>(() => CommandRunner.ValidatePrefixSuffix(""));

    [Fact]
    public void ValidatePrefixSuffix_Whitespace_Throws() =>
        Assert.Throws<ErrorException>(() => CommandRunner.ValidatePrefixSuffix("   "));

    [Fact]
    public void ValidatePrefixSuffix_Tab_Throws() =>
        Assert.Throws<ErrorException>(() => CommandRunner.ValidatePrefixSuffix("\t"));

    // -------------------------------------------------------------------------
    // Direct tests of FindTargetDirectory
    // -------------------------------------------------------------------------

    [Fact]
    public void FindTargetDirectory_Null_ReturnsCurrentDirectory()
    {
        var result = CommandRunner.FindTargetDirectory(null);

        Assert.Equal(Environment.CurrentDirectory, result);
    }

    [Fact]
    public void FindTargetDirectory_Absolute_ReturnsSame()
    {
        var absolutePath = Path.GetFullPath(Environment.CurrentDirectory);

        var result = CommandRunner.FindTargetDirectory(absolutePath);

        Assert.Equal(absolutePath, result);
    }

    [Fact]
    public void FindTargetDirectory_Relative_ReturnsAbsolute()
    {
        var result = CommandRunner.FindTargetDirectory("subdir");

        Assert.True(Path.IsPathRooted(result));
        Assert.EndsWith("subdir", result);
    }
}