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
/// PE section header.
/// </summary>
public sealed class Section
{
    public string Name { get; set; } = "";
    public uint VirtualSize { get; set; }
    public uint VirtualAddress { get; set; }
    public uint SizeOfRawData { get; set; }
    public uint PointerToRawData { get; set; }
    public uint Characteristics { get; set; }

    /// <summary>
    /// The raw data of this section.
    /// </summary>
    public byte[] Data { get; set; } = [];
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
/// Target architecture from PE header.
/// </summary>
public enum TargetArchitecture : ushort
{
    I386 = 0x014c,
    AMD64 = 0x8664,
    ARM = 0x01c4,
    ARM64 = 0xaa64
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

/// <summary>
/// Represents a complete PE image with parsed headers and sections.
/// </summary>
public sealed class PEImage
{
    // DOS/PE headers
    public TargetArchitecture Architecture { get; set; }
    public ushort Characteristics { get; set; }
    public uint Timestamp { get; set; }
    public ModuleKind Kind { get; set; }
    public bool IsPE64 { get; set; }

    // Data directories
    public DataDirectory CLIHeaderDirectory { get; set; }
    public DataDirectory DebugDirectory { get; set; }
    public DataDirectory ImportDirectory { get; set; }
    public DataDirectory Win32Resources { get; set; }

    // Sections
    public Section[] Sections { get; set; } = [];

    // CLI header
    public CLIHeader CLIHeader { get; set; }

    // Metadata location
    public uint MetadataRva { get; set; }
    public uint MetadataSize { get; set; }

    /// <summary>
    /// Returns true if this is a managed .NET assembly (has CLI header and metadata).
    /// </summary>
    public bool IsManagedAssembly => MetadataRva != 0 && MetadataSize != 0;

    // Strong name signature location (for re-signing)
    public uint StrongNameRva { get; set; }
    public uint StrongNameSize { get; set; }

    // Metadata streams
    public StreamHeader[] StreamHeaders { get; set; } = [];
    public string MetadataVersionString { get; set; } = "";

    // Raw file data
    public byte[] RawData { get; set; } = [];

    // PE header positions (for patching)
    public int PEHeaderOffset { get; set; }
    public int OptionalHeaderOffset { get; set; }
    public int SectionHeadersOffset { get; set; }
    public int CLIHeaderFileOffset { get; set; }

    /// <summary>
    /// Resolves a virtual address (RVA) to a file offset.
    /// </summary>
    public uint ResolveRva(uint rva)
    {
        var section = GetSectionAtRva(rva);
        if (section == null)
            throw new BadImageFormatException($"RVA 0x{rva:X8} is not within any section");
        return rva - section.VirtualAddress + section.PointerToRawData;
    }

    /// <summary>
    /// Gets the section containing the given RVA.
    /// </summary>
    public Section? GetSectionAtRva(uint rva)
    {
        foreach (var section in Sections)
        {
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData)
                return section;
        }
        return null;
    }

    /// <summary>
    /// Gets the stream header by name.
    /// </summary>
    public StreamHeader? GetStreamHeader(string name)
    {
        foreach (var header in StreamHeaders)
        {
            if (header.Name == name)
                return header;
        }
        return null;
    }
}

/// <summary>
/// Debug directory entry types.
/// </summary>
public enum ImageDebugType
{
    Unknown = 0,
    Coff = 1,
    CodeView = 2,
    Fpo = 3,
    Misc = 4,
    Exception = 5,
    Fixup = 6,
    Reproducible = 16,
    EmbeddedPortablePdb = 17,
    PdbChecksum = 19,
}

/// <summary>
/// Debug directory entry.
/// </summary>
public sealed class DebugDirectoryEntry
{
    public uint Characteristics { get; set; }
    public uint TimeDateStamp { get; set; }
    public ushort MajorVersion { get; set; }
    public ushort MinorVersion { get; set; }
    public ImageDebugType Type { get; set; }
    public uint SizeOfData { get; set; }
    public uint AddressOfRawData { get; set; }
    public uint PointerToRawData { get; set; }
    public byte[] Data { get; set; } = [];
}
