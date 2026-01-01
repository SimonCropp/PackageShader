using System.Text;

namespace Alias.Lib.PE;

/// <summary>
/// Provides lazy, streaming access to a PE file without loading it entirely into memory.
/// Only parses headers on open (~2KB), section data is read on-demand.
/// </summary>
public sealed class StreamingPEFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private bool _disposed;

    // Cached header info (small - always loaded)
    public ushort Characteristics { get; private set; }
    public uint Timestamp { get; private set; }
    public bool IsPE64 { get; private set; }

    // Data directories
    public DataDirectory CLIHeaderDirectory { get; private set; }
    public DataDirectory DebugDirectory { get; private set; }
    public DataDirectory ImportDirectory { get; private set; }
    public DataDirectory Win32Resources { get; private set; }

    // Section info (no data - just metadata)
    public SectionInfo[] Sections { get; private set; } = [];

    // CLI header
    public CLIHeader CLIHeader { get; private set; }

    // Metadata location
    public uint MetadataRva { get; private set; }
    public uint MetadataSize { get; private set; }
    public long MetadataFileOffset { get; private set; }

    // Strong name signature location
    public uint StrongNameRva { get; private set; }
    public uint StrongNameSize { get; private set; }
    public long StrongNameFileOffset { get; private set; }

    // Metadata streams
    public StreamHeader[] StreamHeaders { get; private set; } = [];
    public string MetadataVersionString { get; private set; } = "";

    // PE header positions (for patching)
    public int OptionalHeaderOffset { get; private set; }
    public int SectionHeadersOffset { get; private set; }
    public int CLIHeaderFileOffset { get; private set; }

    // File info
    public long FileLength { get; private set; }
    public string FilePath { get; }

    private StreamingPEFile(string path, FileStream stream)
    {
        FilePath = path;
        _stream = stream;
        _reader = new(stream, Encoding.ASCII, leaveOpen: true);
        FileLength = stream.Length;
    }

    /// <summary>
    /// Opens a PE file for streaming access.
    /// </summary>
    public static StreamingPEFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var file = new StreamingPEFile(path, stream);
            file.ParseHeaders();
            return file;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void ParseHeaders()
    {
        if (FileLength < 128)
            throw new BadImageFormatException("File too small to be a valid PE");

        // DOS Header
        if (_reader.ReadUInt16() != 0x5a4d) // MZ
            throw new BadImageFormatException("Invalid DOS header");

        _stream.Position = 60;
        var peHeaderOffset = _reader.ReadInt32();

        _stream.Position = peHeaderOffset;

        // PE Signature
        if (_reader.ReadUInt32() != 0x00004550) // PE\0\0
            throw new BadImageFormatException("Invalid PE signature");

        // COFF Header
        //Architecture
        _reader.ReadUInt16();
        var numberOfSections = _reader.ReadUInt16();
        Timestamp = _reader.ReadUInt32();
        _stream.Position += 10; // Skip PointerToSymbolTable, NumberOfSymbols, SizeOfOptionalHeader
        Characteristics = _reader.ReadUInt16();

        OptionalHeaderOffset = (int)_stream.Position;

        // Optional Header
        ParseOptionalHeader();

        if (CLIHeaderDirectory.IsZero)
        {
            throw new BadImageFormatException("Not a .NET assembly (no CLI header)");
        }

        SectionHeadersOffset = (int)_stream.Position;

        // Section Headers (metadata only, no data)
        Sections = ParseSectionHeaders(numberOfSections);

        // Read CLI Header
        ParseCLIHeader();

        // Read Metadata Root (for stream headers)
        ParseMetadataRoot();
    }

    private void ParseOptionalHeader()
    {
        var magic = _reader.ReadUInt16();
        IsPE64 = magic == 0x20b; // PE32+

        // Skip to data directories (same offset for PE32 and PE32+)
        _stream.Position += 66;

        _reader.ReadUInt16(); // Subsystem
        _reader.ReadUInt16(); // DllCharacteristics

        // Skip stack/heap sizes
        _stream.Position += IsPE64 ? 40 : 24;

        // Data Directories
        _stream.Position += 8; // [0] Export
        ImportDirectory = ReadDataDirectory(); // [1] Import
        Win32Resources = ReadDataDirectory(); // [2] Resource
        _stream.Position += 24; // [3-5] Exception, Certificate, Base Relocation
        DebugDirectory = ReadDataDirectory(); // [6] Debug
        _stream.Position += 56; // [7-13]
        CLIHeaderDirectory = ReadDataDirectory(); // [14] CLI Header
        _stream.Position += 8; // [15] Reserved
    }

    private SectionInfo[] ParseSectionHeaders(int count)
    {
        var sections = new SectionInfo[count];

        for (int i = 0; i < count; i++)
        {
            var nameBytes = _reader.ReadBytes(8);
            var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            sections[i] = new()
            {
                Name = name,
                VirtualSize = _reader.ReadUInt32(),
                VirtualAddress = _reader.ReadUInt32(),
                SizeOfRawData = _reader.ReadUInt32(),
                PointerToRawData = _reader.ReadUInt32()
            };

            _stream.Position += 12; // Skip PointerToRelocations, etc.
            sections[i].Characteristics = _reader.ReadUInt32();
        }

        return sections;
    }

    private void ParseCLIHeader()
    {
        if (CLIHeaderDirectory.VirtualAddress == 0)
            return;

        var cliOffset = ResolveRva(CLIHeaderDirectory.VirtualAddress);
        CLIHeaderFileOffset = (int)cliOffset;
        _stream.Position = cliOffset;

        CLIHeader = new()
        {
            Size = _reader.ReadUInt32(),
            MajorRuntimeVersion = _reader.ReadUInt16(),
            MinorRuntimeVersion = _reader.ReadUInt16(),
            Metadata = ReadDataDirectory(),
            Flags = _reader.ReadUInt32(),
            EntryPointToken = _reader.ReadUInt32(),
            Resources = ReadDataDirectory(),
            StrongNameSignature = ReadDataDirectory(),
            CodeManagerTable = ReadDataDirectory(),
            VTableFixups = ReadDataDirectory(),
            ExportAddressTableJumps = ReadDataDirectory(),
            ManagedNativeHeader = ReadDataDirectory()
        };

        MetadataRva = CLIHeader.Metadata.VirtualAddress;
        MetadataSize = CLIHeader.Metadata.Size;
        MetadataFileOffset = ResolveRva(MetadataRva);

        StrongNameRva = CLIHeader.StrongNameSignature.VirtualAddress;
        StrongNameSize = CLIHeader.StrongNameSignature.Size;
        if (StrongNameRva != 0)
            StrongNameFileOffset = ResolveRva(StrongNameRva);
    }

    private void ParseMetadataRoot()
    {
        if (MetadataRva == 0)
        {
            StreamHeaders = [];
            return;
        }

        _stream.Position = MetadataFileOffset;

        // Metadata signature: BSJB
        if (_reader.ReadUInt32() != 0x424a5342)
            throw new BadImageFormatException("Invalid metadata signature");

        _stream.Position += 8; // Major/Minor version + Reserved

        // Version string
        var versionLength = _reader.ReadInt32();
        var versionBytes = _reader.ReadBytes(versionLength);
        MetadataVersionString = Encoding.ASCII.GetString(versionBytes).TrimEnd('\0');

        // Align to 4-byte boundary
        _stream.Position = MetadataFileOffset + 16 + versionLength;
        _stream.Position += 2; // Flags

        var streamCount = _reader.ReadUInt16();

        StreamHeaders = new StreamHeader[streamCount];
        for (int i = 0; i < streamCount; i++)
        {
            var offset = _reader.ReadUInt32();
            var size = _reader.ReadUInt32();

            // Read null-terminated, 4-byte aligned name
            var nameBuilder = new StringBuilder();
            int bytesRead = 0;
            while (bytesRead < 32)
            {
                var b = _reader.ReadByte();
                bytesRead++;
                if (b == 0) break;
                nameBuilder.Append((char)b);
            }
            // Align to 4-byte boundary
            var aligned = (bytesRead + 3) & ~3;
            _stream.Position += aligned - bytesRead;

            StreamHeaders[i] = new()
            {
                Offset = offset,
                Size = size,
                Name = nameBuilder.ToString()
            };
        }
    }

    private DataDirectory ReadDataDirectory() =>
        new()
        {
            VirtualAddress = _reader.ReadUInt32(),
            Size = _reader.ReadUInt32()
        };

    /// <summary>
    /// Resolves a virtual address (RVA) to a file offset.
    /// </summary>
    public long ResolveRva(uint rva)
    {
        var section = GetSectionAtRva(rva);
        if (section == null)
            throw new BadImageFormatException($"RVA 0x{rva:X8} is not within any section");
        return rva - section.VirtualAddress + section.PointerToRawData;
    }

    /// <summary>
    /// Gets the section containing the given RVA.
    /// </summary>
    public SectionInfo? GetSectionAtRva(uint rva)
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

    /// <summary>
    /// Reads bytes at a specific file offset without caching.
    /// </summary>
    public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        _stream.Position = offset;
        return _stream.Read(buffer, bufferOffset, count);
    }

    /// <summary>
    /// Reads bytes at a specific file offset into a new array.
    /// </summary>
    public byte[] ReadBytesAt(long offset, int count)
    {
        _stream.Position = offset;
        var buffer = new byte[count];
        var read = _stream.Read(buffer, 0, count);
        if (read < count)
            Array.Resize(ref buffer, read);
        return buffer;
    }

    /// <summary>
    /// Copies a region of the file directly to an output stream.
    /// Efficient for streaming unchanged portions of the PE.
    /// </summary>
    public void CopyRegion(long offset, long length, Stream destination)
    {
        const int bufferSize = 81920; // 80KB buffer
        var buffer = new byte[bufferSize];

        _stream.Position = offset;
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, bufferSize);
            var read = _stream.Read(buffer, 0, toRead);
            if (read == 0) break;

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Gets a read-only view of the underlying stream positioned at metadata.
    /// Caller should not dispose this stream.
    /// </summary>
    public Stream GetMetadataStream()
    {
        _stream.Position = MetadataFileOffset;
        return _stream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
        _stream.Dispose();
    }
}

/// <summary>
/// Section info without the actual data (for streaming).
/// </summary>
public sealed class SectionInfo
{
    public string Name { get; set; } = "";
    public uint VirtualSize { get; set; }
    public uint VirtualAddress { get; set; }
    public uint SizeOfRawData { get; set; }
    public uint PointerToRawData { get; set; }
    public uint Characteristics { get; set; }
}
