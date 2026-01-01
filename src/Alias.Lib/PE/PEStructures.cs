namespace Alias.Lib.PE;

/// <summary>
/// Metadata stream header.
/// </summary>
public sealed class StreamHeader
{
    public uint Offset { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
}
