public class Options
{
    [Option('t', "target-directory", Required = false)]
    public string? TargetDirectory { get; set; }

    [Option('i', "internalize", Required = false)]
    public bool Internalize { get; set; } = false;

    [Option('a', "assemblies-to-shade", Required = true, Separator = ';')]
    public IEnumerable<string> AssembliesToShade { get; set; } = null!;

    [Option('e', "assemblies-to-exclude", Required = false, Separator = ';')]
    public IEnumerable<string> AssembliesToExclude { get; set; } = null!;

    [Option('k', "key", Required = false)]
    public string? Key { get; set; }

    [Option('p', "prefix", Required = false)]
    public string? Prefix { get; set; }

    [Option('s', "suffix", Required = false)]
    public string? Suffix { get; set; }
}