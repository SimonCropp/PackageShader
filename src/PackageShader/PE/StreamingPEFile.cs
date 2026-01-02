/// <summary>
/// Provides lazy, streaming access to a PE file without loading it entirely into memory.
/// Uses System.Reflection.PortableExecutable.PEReader for header parsing.
/// </summary>
sealed class StreamingPEFile : IDisposable
{
    FileStream stream;
    PEReader peReader;
    bool disposed;

    // Cached from PEHeaders for convenience
    public bool IsPE64 { get; }
    public SectionInfo[] Sections { get; }

    // Header offsets (for patching)
    public int OptionalHeaderOffset { get; }
    public int SectionHeadersOffset { get; }
    public int CLIHeaderFileOffset { get; }

    // Metadata location
    public uint MetadataRva { get; }
    public uint MetadataSize { get; }
    public long MetadataFileOffset { get; }

    // Strong name signature location
    public uint StrongNameRva { get; }
    public uint StrongNameSize { get; }
    public long StrongNameFileOffset { get; }

    // Metadata streams (parsed separately - PEReader doesn't expose these)
    public StreamHeader[] StreamHeaders { get; private set; } = [];
    public string MetadataVersionString { get; private set; } = "";

    // File info
    public long FileLength { get; }
    public string FilePath { get; }

    /// <summary>
    /// Gets the underlying PEReader for direct access to PE headers.
    /// </summary>
    public PEReader PEReader => peReader;

    StreamingPEFile(string path, FileStream stream, PEReader peReader)
    {
        FilePath = path;
        this.stream = stream;
        this.peReader = peReader;
        FileLength = stream.Length;

        var headers = peReader.PEHeaders;

        // Basic info
        IsPE64 = headers.PEHeader!.Magic == PEMagic.PE32Plus;

        // Header offsets
        OptionalHeaderOffset = headers.PEHeaderStartOffset;
        CLIHeaderFileOffset = headers.CorHeaderStartOffset;

        // Calculate section headers offset: after optional header
        // Optional header size is in COFF header at offset 16
        var coffHeaderOffset = headers.CoffHeaderStartOffset;
        var optionalHeaderSize = headers.CoffHeader.SizeOfOptionalHeader;
        SectionHeadersOffset = coffHeaderOffset + 20 + optionalHeaderSize; // 20 = COFF header size

        // Convert section headers
        Sections = new SectionInfo[headers.SectionHeaders.Length];
        for (var i = 0; i < headers.SectionHeaders.Length; i++)
        {
            var sh = headers.SectionHeaders[i];
            Sections[i] = new()
            {
                VirtualSize = (uint)sh.VirtualSize,
                VirtualAddress = (uint)sh.VirtualAddress,
                SizeOfRawData = (uint)sh.SizeOfRawData,
                PointerToRawData = (uint)sh.PointerToRawData,
                Characteristics = (uint)sh.SectionCharacteristics
            };
        }

        // Metadata location
        if (headers.CorHeader != null)
        {
            var metadataDir = headers.CorHeader.MetadataDirectory;
            MetadataRva = (uint)metadataDir.RelativeVirtualAddress;
            MetadataSize = (uint)metadataDir.Size;
            MetadataFileOffset = MetadataRva > 0 ? ResolveRva(MetadataRva) : 0;

            var snDir = headers.CorHeader.StrongNameSignatureDirectory;
            StrongNameRva = (uint)snDir.RelativeVirtualAddress;
            StrongNameSize = (uint)snDir.Size;
            StrongNameFileOffset = StrongNameRva > 0 ? ResolveRva(StrongNameRva) : 0;
        }

        // Parse metadata stream headers (PEReader doesn't expose these)
        if (MetadataRva > 0)
        {
            ParseMetadataStreamHeaders();
        }
    }

    /// <summary>
    /// Opens a PE file for streaming access.
    /// </summary>
    public static StreamingPEFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);

            if (peReader.PEHeaders.CorHeader == null)
            {
                throw new BadImageFormatException("Not a .NET assembly (no CLI header)");
            }

            return new(path, stream, peReader);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    void ParseMetadataStreamHeaders()
    {
        stream.Position = MetadataFileOffset;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        // Metadata signature: BSJB
        if (reader.ReadUInt32() != 0x424a5342)
        {
            throw new BadImageFormatException("Invalid metadata signature");
        }

        stream.Position += 8; // Major/Minor version + Reserved

        // Version string
        var versionLength = reader.ReadInt32();
        var versionBytes = reader.ReadBytes(versionLength);
        MetadataVersionString = Encoding.ASCII.GetString(versionBytes).TrimEnd('\0');

        // Align to 4-byte boundary
        stream.Position += 2; // Flags

        var streamCount = reader.ReadUInt16();

        StreamHeaders = new StreamHeader[streamCount];
        for (var i = 0; i < streamCount; i++)
        {
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();

            // Read null-terminated, 4-byte aligned name
            var nameBuilder = new StringBuilder();
            var bytesRead = 0;
            while (bytesRead < 32)
            {
                var b = reader.ReadByte();
                bytesRead++;
                if (b == 0) break;
                nameBuilder.Append((char)b);
            }
            // Align to 4-byte boundary
            var aligned = (bytesRead + 3) & ~3;
            stream.Position += aligned - bytesRead;

            StreamHeaders[i] = new()
            {
                Offset = offset,
                Size = size,
                Name = nameBuilder.ToString()
            };
        }
    }

    /// <summary>
    /// Gets the stream header by name.
    /// </summary>
    public StreamHeader? GetStreamHeader(string name) =>
        StreamHeaders.FirstOrDefault(_ => _.Name == name);

    /// <summary>
    /// Resolves a virtual address (RVA) to a file offset.
    /// </summary>
    public long ResolveRva(uint rva)
    {
        var section = GetSectionAtRva(rva);
        if (section == null)
        {
            throw new BadImageFormatException($"RVA 0x{rva:X8} is not within any section");
        }

        return rva - section.VirtualAddress + section.PointerToRawData;
    }

    /// <summary>
    /// Gets the section containing the given RVA.
    /// </summary>
    public SectionInfo? GetSectionAtRva(uint rva) =>
        Sections.FirstOrDefault(_ =>
            rva >= _.VirtualAddress &&
            rva < _.VirtualAddress + _.SizeOfRawData);

    /// <summary>
    /// Reads bytes at a specific file offset into a span.
    /// </summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        stream.Position = offset;
        return stream.Read(buffer);
    }

    /// <summary>
    /// Reads bytes at a specific file offset into a new array.
    /// </summary>
    public byte[] ReadBytesAt(long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        var read = stream.Read(buffer, 0, count);
        if (read < count)
        {
            Array.Resize(ref buffer, read);
        }

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

        stream.Position = offset;
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, bufferSize);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        peReader.Dispose();
        stream.Dispose();
    }
}