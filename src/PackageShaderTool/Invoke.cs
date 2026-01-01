public delegate void Invoke(
    string directory,
    List<string> assemblyToShade,
    List<string> references,
    string? keyFile,
    List<string> assembliesToExclude,
    string? prefix,
    string? suffix,
    bool internalize,
    Action<string> log);
