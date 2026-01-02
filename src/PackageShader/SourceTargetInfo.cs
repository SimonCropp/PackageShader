namespace PackageShader;

// begin-snippet: SourceTargetInfo
public record SourceTargetInfo(
    string SourceName,
    string SourcePath,
    string TargetName,
    string TargetPath,
    bool IsShaded);
// end-snippet