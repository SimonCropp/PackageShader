/// <summary>
/// Information about a PE section.
/// </summary>
class SectionInfo
{
    public uint VirtualSize { get; init; }
    public uint VirtualAddress { get; init; }
    public uint SizeOfRawData { get; init; }
    public uint PointerToRawData { get; init; }
    public uint Characteristics { get; init; }
}