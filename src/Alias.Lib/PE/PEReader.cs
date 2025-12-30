using System.Text;

namespace Alias.Lib.PE;

/// <summary>
/// Reads a PE file and extracts header information and metadata locations.
/// </summary>
public sealed class PEReader
{
    private readonly byte[] _data;
    private int _position;

    private PEReader(byte[] data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>
    /// Reads a PE file from the given path.
    /// </summary>
    public static PEImage Read(string path)
    {
        var data = File.ReadAllBytes(path);
        return Read(data);
    }

    /// <summary>
    /// Reads a PE file from the given byte array.
    /// </summary>
    public static PEImage Read(byte[] data)
    {
        var reader = new PEReader(data);
        return reader.ReadImage();
    }

    private PEImage ReadImage()
    {
        var image = new PEImage { RawData = _data };

        if (_data.Length < 128)
            throw new BadImageFormatException("File too small to be a valid PE");

        // DOS Header
        if (ReadUInt16() != 0x5a4d) // MZ
            throw new BadImageFormatException("Invalid DOS header");

        Advance(58);
        var peHeaderOffset = (int)ReadUInt32();
        image.PEHeaderOffset = peHeaderOffset;

        MoveTo(peHeaderOffset);

        // PE Signature
        if (ReadUInt32() != 0x00004550) // PE\0\0
            throw new BadImageFormatException("Invalid PE signature");

        // COFF Header
        image.Architecture = (TargetArchitecture)ReadUInt16();
        var numberOfSections = ReadUInt16();
        image.Timestamp = ReadUInt32();
        Advance(10); // PointerToSymbolTable, NumberOfSymbols, SizeOfOptionalHeader
        image.Characteristics = ReadUInt16();

        image.OptionalHeaderOffset = _position;

        // Optional Header
        var (pe64, cliDirectory, debugDirectory, importDirectory, win32Resources) = ReadOptionalHeader();
        image.IsPE64 = pe64;
        image.CLIHeaderDirectory = cliDirectory;
        image.DebugDirectory = debugDirectory;
        image.ImportDirectory = importDirectory;
        image.Win32Resources = win32Resources;

        if (cliDirectory.IsZero)
            throw new BadImageFormatException("Not a .NET assembly (no CLI header)");

        image.SectionHeadersOffset = _position;

        // Section Headers
        image.Sections = ReadSections(numberOfSections);

        // Determine module kind
        image.Kind = GetModuleKind(image.Characteristics);

        // Read CLI Header
        ReadCLIHeader(image);

        // Read Metadata Root
        ReadMetadataRoot(image);

        // Read Debug Directory
        if (!image.DebugDirectory.IsZero)
            ReadDebugDirectory(image);

        return image;
    }

    private (bool pe64, DataDirectory cli, DataDirectory debug, DataDirectory import, DataDirectory resources) ReadOptionalHeader()
    {
        var magic = ReadUInt16();
        var pe64 = magic == 0x20b; // PE32+

        // Skip to subsystem (different offsets for PE32 vs PE32+)
        // After magic (2 bytes read), standard fields:
        // PE32: MajorLinkerVersion(1) + MinorLinkerVersion(1) + SizeOfCode(4) + SizeOfInitializedData(4)
        //       + SizeOfUninitializedData(4) + AddressOfEntryPoint(4) + BaseOfCode(4) + BaseOfData(4) = 26
        // PE32+: Same but no BaseOfData = 22
        //
        // Windows-specific fields to CheckSum:
        // PE32: ImageBase(4) + SectionAlignment(4) + FileAlignment(4) + MajorOSVersion(2) + MinorOSVersion(2)
        //       + MajorImageVersion(2) + MinorImageVersion(2) + MajorSubsystemVersion(2) + MinorSubsystemVersion(2)
        //       + Win32VersionValue(4) + SizeOfImage(4) + SizeOfHeaders(4) + CheckSum(4) = 40
        // PE32+: Same but ImageBase is 8 = 44
        //
        // Total to skip: PE32 = 26 + 40 = 66, PE32+ = 22 + 44 = 66
        Advance(66);

        var subsystem = ReadUInt16();
        ReadUInt16(); // DllCharacteristics

        // Skip to data directories
        // PE32: SizeOfStackReserve(4) + SizeOfStackCommit(4) + SizeOfHeapReserve(4) + SizeOfHeapCommit(4) + LoaderFlags(4) + NumberOfRvaAndSizes(4) = 24
        // PE32+: Same but 8 bytes each for first 4 fields = 40
        Advance(pe64 ? 40 : 24);

        // Data Directories (each is 8 bytes: RVA + Size)
        // [0] Export
        Advance(8);
        // [1] Import
        var import = ReadDataDirectory();
        // [2] Resource
        var resources = ReadDataDirectory();
        // [3] Exception, [4] Certificate, [5] Base Relocation
        Advance(24);
        // [6] Debug
        var debug = ReadDataDirectory();
        // [7] Architecture (Copyright), [8] Global Pointer, [9] TLS, [10] Load Config,
        // [11] Bound Import, [12] IAT, [13] Delay Import
        Advance(56);
        // [14] CLI Header (.NET runtime)
        var cli = ReadDataDirectory();
        // [15] Reserved
        Advance(8);

        return (pe64, cli, debug, import, resources);
    }

    private Section[] ReadSections(int count)
    {
        var sections = new Section[count];

        for (int i = 0; i < count; i++)
        {
            var name = ReadZeroTerminatedString(8);
            var virtualSize = ReadUInt32();
            var virtualAddress = ReadUInt32();
            var sizeOfRawData = ReadUInt32();
            var pointerToRawData = ReadUInt32();
            Advance(12); // PointerToRelocations, PointerToLinenumbers, NumberOfRelocations, NumberOfLinenumbers
            var characteristics = ReadUInt32();

            // Read section data
            var data = new byte[sizeOfRawData];
            if (pointerToRawData > 0 && pointerToRawData + sizeOfRawData <= _data.Length)
            {
                Array.Copy(_data, pointerToRawData, data, 0, Math.Min(sizeOfRawData, _data.Length - pointerToRawData));
            }

            sections[i] = new Section
            {
                Name = name,
                VirtualSize = virtualSize,
                VirtualAddress = virtualAddress,
                SizeOfRawData = sizeOfRawData,
                PointerToRawData = pointerToRawData,
                Characteristics = characteristics,
                Data = data
            };
        }

        return sections;
    }

    private void ReadCLIHeader(PEImage image)
    {
        // Check if this is a .NET assembly (has CLI header)
        if (image.CLIHeaderDirectory.VirtualAddress == 0)
        {
            image.CLIHeader = new CLIHeader();
            return;
        }

        var cliOffset = image.ResolveRva(image.CLIHeaderDirectory.VirtualAddress);
        image.CLIHeaderFileOffset = (int)cliOffset;
        MoveTo((int)cliOffset);

        var cliHeader = new CLIHeader
        {
            Size = ReadUInt32(),
            MajorRuntimeVersion = ReadUInt16(),
            MinorRuntimeVersion = ReadUInt16(),
            Metadata = ReadDataDirectory(),
            Flags = ReadUInt32(),
            EntryPointToken = ReadUInt32(),
            Resources = ReadDataDirectory(),
            StrongNameSignature = ReadDataDirectory(),
            CodeManagerTable = ReadDataDirectory(),
            VTableFixups = ReadDataDirectory(),
            ExportAddressTableJumps = ReadDataDirectory(),
            ManagedNativeHeader = ReadDataDirectory()
        };

        image.CLIHeader = cliHeader;
        image.MetadataRva = cliHeader.Metadata.VirtualAddress;
        image.MetadataSize = cliHeader.Metadata.Size;
        image.StrongNameRva = cliHeader.StrongNameSignature.VirtualAddress;
        image.StrongNameSize = cliHeader.StrongNameSignature.Size;
    }

    private void ReadMetadataRoot(PEImage image)
    {
        // No metadata if not a .NET assembly
        if (image.MetadataRva == 0)
        {
            image.StreamHeaders = [];
            return;
        }

        var metadataOffset = image.ResolveRva(image.MetadataRva);
        MoveTo((int)metadataOffset);

        // Metadata signature: BSJB
        if (ReadUInt32() != 0x424a5342)
            throw new BadImageFormatException("Invalid metadata signature");

        // Major/Minor version
        Advance(4);

        // Reserved
        Advance(4);

        // Version string length and string
        var versionLength = ReadInt32();
        image.MetadataVersionString = ReadZeroTerminatedString(versionLength);

        // Align to 4-byte boundary
        var padding = (4 - (versionLength % 4)) % 4;
        if (padding > 0 && versionLength % 4 != 0)
            padding = 4 - (versionLength % 4);
        // Actually the version length already includes padding, so we just advance by the version length
        // The position is already at versionLength from the length read, need to account for null padding
        MoveTo((int)metadataOffset + 16 + versionLength);

        // Flags
        Advance(2);

        // Number of streams
        var streamCount = ReadUInt16();

        // Read stream headers
        var streamHeaders = new StreamHeader[streamCount];
        for (int i = 0; i < streamCount; i++)
        {
            var offset = ReadUInt32();
            var size = ReadUInt32();
            var name = ReadAlignedString(32);

            streamHeaders[i] = new StreamHeader
            {
                Offset = offset,
                Size = size,
                Name = name
            };
        }

        image.StreamHeaders = streamHeaders;
    }

    private void ReadDebugDirectory(PEImage image)
    {
        // Try to resolve the debug directory RVA - skip if invalid
        var section = image.GetSectionAtRva(image.DebugDirectory.VirtualAddress);
        if (section == null)
            return;

        var debugOffset = image.DebugDirectory.VirtualAddress - section.VirtualAddress + section.PointerToRawData;

        // Ensure offset is within file bounds
        if (debugOffset >= _data.Length)
            return;

        MoveTo((int)debugOffset);

        var entryCount = (int)(image.DebugDirectory.Size / 28); // Debug directory entry is 28 bytes
        var entries = new List<DebugDirectoryEntry>();

        for (int i = 0; i < entryCount; i++)
        {
            // Check bounds before reading
            if (_position + 28 > _data.Length)
                break;

            var entry = new DebugDirectoryEntry
            {
                Characteristics = ReadUInt32(),
                TimeDateStamp = ReadUInt32(),
                MajorVersion = ReadUInt16(),
                MinorVersion = ReadUInt16(),
                Type = (ImageDebugType)ReadUInt32(),
                SizeOfData = ReadUInt32(),
                AddressOfRawData = ReadUInt32(),
                PointerToRawData = ReadUInt32()
            };

            // Read debug data
            if (entry.PointerToRawData > 0 && entry.SizeOfData > 0 &&
                entry.PointerToRawData + entry.SizeOfData <= _data.Length)
            {
                var currentPos = _position;
                MoveTo((int)entry.PointerToRawData);
                entry.Data = ReadBytes((int)entry.SizeOfData);
                MoveTo(currentPos);
            }

            entries.Add(entry);
        }

        // Store in image (could add a property for this)
    }

    private static ModuleKind GetModuleKind(ushort characteristics)
    {
        if ((characteristics & 0x2000) != 0) // IMAGE_FILE_DLL
            return ModuleKind.Dll;
        return ModuleKind.Console;
    }

    #region Reading Helpers

    private byte ReadByte()
    {
        return _data[_position++];
    }

    private byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];
        Array.Copy(_data, _position, bytes, 0, count);
        _position += count;
        return bytes;
    }

    private ushort ReadUInt16()
    {
        var value = BitConverter.ToUInt16(_data, _position);
        _position += 2;
        return value;
    }

    private int ReadInt32()
    {
        var value = BitConverter.ToInt32(_data, _position);
        _position += 4;
        return value;
    }

    private uint ReadUInt32()
    {
        var value = BitConverter.ToUInt32(_data, _position);
        _position += 4;
        return value;
    }

    private DataDirectory ReadDataDirectory()
    {
        return new DataDirectory
        {
            VirtualAddress = ReadUInt32(),
            Size = ReadUInt32()
        };
    }

    private void Advance(int bytes)
    {
        _position += bytes;
    }

    private void MoveTo(int position)
    {
        _position = position;
    }

    private string ReadZeroTerminatedString(int maxLength)
    {
        var start = _position;
        var length = 0;

        while (length < maxLength && _data[_position + length] != 0)
            length++;

        _position += maxLength;
        return Encoding.ASCII.GetString(_data, start, length);
    }

    private string ReadAlignedString(int maxLength)
    {
        var sb = new StringBuilder();
        var read = 0;

        while (read < maxLength)
        {
            var b = ReadByte();
            read++;
            if (b == 0)
                break;
            sb.Append((char)b);
        }

        // Align to 4-byte boundary
        var totalRead = read;
        var aligned = (totalRead + 3) & ~3;
        Advance(aligned - totalRead);

        return sb.ToString();
    }

    #endregion
}
