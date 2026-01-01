using System.Runtime.InteropServices;

namespace Alias.Lib.PE;

/// <summary>
/// Data directory entry in PE optional header.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DataDirectory
{
    public uint VirtualAddress;
    public uint Size;

    public bool IsZero => VirtualAddress == 0 && Size == 0;
}

/// <summary>
/// CLI header (COR20 header) at the start of .NET metadata.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CLIHeader
{
    public uint Size;
    public ushort MajorRuntimeVersion;
    public ushort MinorRuntimeVersion;
    public DataDirectory Metadata;
    public uint Flags;
    public uint EntryPointToken;
    public DataDirectory Resources;
    public DataDirectory StrongNameSignature;
    public DataDirectory CodeManagerTable;
    public DataDirectory VTableFixups;
    public DataDirectory ExportAddressTableJumps;
    public DataDirectory ManagedNativeHeader;
}

/// <summary>
/// Metadata stream header.
/// </summary>
public sealed class StreamHeader
{
    public uint Offset { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Module kind derived from PE characteristics.
/// </summary>
public enum ModuleKind
{
    Dll,
    Console,
    Windows
}
