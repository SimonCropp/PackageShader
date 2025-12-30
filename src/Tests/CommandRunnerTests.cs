using System.Text;
using CommandLine;

public class CommandRunnerTests
{
    [Fact]
    public Task MissingAssembliesToAlias()
    {
        var result = Parse("--target-directory directory --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task All()
    {
        Directory.CreateDirectory("directory");
        var result = Parse("--target-directory directory --suffix _Alias --prefix Alias_ --key test.snk --assemblies-to-alias assembly");
        return Verify(result);
    }

    [Fact]
    public Task Prefix()
    {
        var result = Parse("--prefix Alias_ --assemblies-to-alias assembly");
        return Verify(result);
    }

    [Fact]
    public Task Suffix()
    {
        var result = Parse("--suffix _Alias --assemblies-to-alias assembly");
        return Verify(result);
    }

    [Fact]
    public Task NoPrefixOrSuffix() =>
        Throws(() => Parse("--assemblies-to-alias assembly"));

    [Fact]
    public Task BadKeyPath() =>
        Throws(() => Parse("--key bad.snk --assemblies-to-alias assembly --suffix _Alias"));

    [Fact]
    public Task KeyRelative()
    {
        var result = Parse("--key test.snk --assemblies-to-alias assembly --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task KeyFull()
    {
        var result = Parse($"--key {Environment.CurrentDirectory}/test.snk --assemblies-to-alias assembly --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task ReferenceFile()
    {
        var result = Parse("--assemblies-to-alias assembly --reference-file referenceFile --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task References()
    {
        var result = Parse("--assemblies-to-alias assembly --references reference1 --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task ReferencesMultiple()
    {
        var result = Parse("--assemblies-to-alias assembly --references reference1;reference2 --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task CurrentDirectory()
    {
        var result = Parse("--assemblies-to-alias assembly --suffix _Alias");
        return Verify(result);
    }

    [Fact]
    public Task MultipleAssemblies()
    {
        var result = Parse("--assemblies-to-alias assembly1;assembly2 --suffix _Alias");
        return Verify(result);
    }

    //[Fact]
    //public Task MultipleAssembliesSplit()
    //{
    //    var result = Parse("--assemblies-to-alias", "assembly2", "--assemblies-to-alias", "assembly2 --suffix _Alias");
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
        IEnumerable<string>? assembliesToAlias = null;
        IEnumerable<string>? assembliesToExclude = null;
        IEnumerable<string>? references = null;
        var result = CommandRunner.RunCommand(
            (_directory, _assembliesToAlias, _references, _key, _assembliesToExclude, _prefix, _suffix, _internalize, _) =>
            {
                directory = _directory;
                key = _key;
                assembliesToAlias = _assembliesToAlias;
                assembliesToExclude = _assembliesToExclude;
                references = _references;
                prefix = _prefix;
                suffix = _suffix;
                internalize = _internalize;
            },
            line => consoleOut.AppendLine(line),
            input.Split(' '));
        return new(result, directory, prefix, suffix, key, assembliesToAlias, references, assembliesToExclude, consoleOut.ToString(), internalize);
    }

    public record Result(
        IEnumerable<Error> errors,
        string? directory,
        string? prefix,
        string? suffix,
        string? key,
        IEnumerable<string>? assembliesToAlias,
        IEnumerable<string>? references,
        IEnumerable<string>? assembliesToExclude,
        string consoleOut,
        bool internalize);
}