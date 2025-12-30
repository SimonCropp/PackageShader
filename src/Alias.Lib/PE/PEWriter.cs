using Alias.Lib.Metadata;

namespace Alias.Lib.PE;

/// <summary>
/// Writes a PE file with modified metadata.
/// </summary>
public sealed class PEWriter
{
    private readonly PEImage _image;
    private readonly byte[] _newMetadata;

    public PEWriter(PEImage image, byte[] newMetadata)
    {
        _image = image;
        _newMetadata = newMetadata;
    }

    /// <summary>
    /// Writes the modified PE to a file.
    /// </summary>
    public void Write(string path)
    {
        var data = Build();
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Builds the modified PE as a byte array.
    /// </summary>
    public byte[] Build()
    {
        // Find the metadata section
        var metadataSection = _image.GetSectionAtRva(_image.MetadataRva)
            ?? throw new InvalidOperationException("Metadata section not found");

        // Calculate metadata offset within section
        var metadataOffsetInSection = _image.MetadataRva - metadataSection.VirtualAddress;

        // Calculate size difference
        var oldMetadataSize = _image.MetadataSize;
        var newMetadataSize = (uint)_newMetadata.Length;
        var sizeDiff = (int)(newMetadataSize - oldMetadataSize);

        // Calculate the new virtual size (actual data size)
        var newVirtualSize = metadataSection.VirtualSize + sizeDiff;

        // Check if the new data fits within the existing raw size (which is file-aligned)
        // Sections often have padding at the end that can absorb growth
        var fileAlignment = GetFileAlignment();
        int newRawSize;
        int rawSizeDiff;

        if (newVirtualSize <= metadataSection.SizeOfRawData)
        {
            // New data fits in existing section - no need to expand
            newRawSize = (int)metadataSection.SizeOfRawData;
            rawSizeDiff = 0;
        }
        else
        {
            // Need to expand - round up to file alignment
            newRawSize = (int)AlignUp((uint)newVirtualSize, fileAlignment);
            rawSizeDiff = newRawSize - (int)metadataSection.SizeOfRawData;
        }

        // Create new section data with proper size
        var newSectionData = new byte[newRawSize];

        // Copy data before metadata
        Array.Copy(metadataSection.Data, 0, newSectionData, 0, (int)metadataOffsetInSection);

        // Copy new metadata
        Array.Copy(_newMetadata, 0, newSectionData, (int)metadataOffsetInSection, _newMetadata.Length);

        // Copy data after metadata (code, entry point, import tables, etc.)
        var afterMetadataOffset = (int)(metadataOffsetInSection + oldMetadataSize);
        var afterMetadataSize = metadataSection.VirtualSize - afterMetadataOffset;
        if (afterMetadataSize > 0)
        {
            Array.Copy(metadataSection.Data, afterMetadataOffset, newSectionData,
                (int)metadataOffsetInSection + _newMetadata.Length, (int)afterMetadataSize);
        }
        // Rest of newSectionData is zero-padded (default for byte[])

        // Build the new PE file
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Copy everything up to and including section headers
        var headerEnd = GetEndOfHeaders();
        writer.Write(_image.RawData, 0, headerEnd);

        // Pad to first section's file offset
        var firstSectionOffset = GetFirstSectionOffset();
        while (ms.Position < firstSectionOffset)
            writer.Write((byte)0);

        // Write sections (with updated offsets if raw size changed)
        var currentOffset = (int)firstSectionOffset;
        foreach (var section in _image.Sections)
        {
            // Pad to section position (accounting for any shift from previous sections)
            while (ms.Position < currentOffset)
                writer.Write((byte)0);

            if (section == metadataSection)
            {
                // Write modified section
                writer.Write(newSectionData);
                currentOffset += newRawSize;
            }
            else
            {
                // For sections after metadata section, account for size change
                writer.Write(section.Data);
                currentOffset += (int)section.SizeOfRawData;
            }
        }

        var result = ms.ToArray();

        // Patch headers with new sizes
        // Pass both the virtual size diff and the raw size diff
        PatchHeaders(result, metadataSection, sizeDiff, rawSizeDiff, (uint)newRawSize);

        return result;
    }

    private int GetEndOfHeaders()
    {
        // End of section headers
        var sectionHeaderSize = 40; // Size of IMAGE_SECTION_HEADER
        return _image.SectionHeadersOffset + _image.Sections.Length * sectionHeaderSize;
    }

    private uint GetFirstSectionOffset()
    {
        uint minOffset = uint.MaxValue;
        foreach (var section in _image.Sections)
        {
            if (section.PointerToRawData > 0 && section.PointerToRawData < minOffset)
                minOffset = section.PointerToRawData;
        }
        return minOffset;
    }

    private uint GetFileAlignment() =>
        // FileAlignment is at offset 36 in the optional header
        BitConverter.ToUInt32(_image.RawData, _image.OptionalHeaderOffset + 36);

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private void PatchHeaders(byte[] data, Section metadataSection, int sizeDiff, int rawSizeDiff, uint newRawSize)
    {
        // Patch CLI header with new metadata size
        var cliHeaderOffset = _image.CLIHeaderFileOffset;

        // Metadata RVA (offset 8) stays the same
        // Metadata Size (offset 12) needs updating
        var newMetadataSize = (uint)_newMetadata.Length;
        WriteUInt32(data, cliHeaderOffset + 12, newMetadataSize);

        // Calculate the file offset where metadata ends
        var metadataOffsetInSection = _image.MetadataRva - metadataSection.VirtualAddress;
        var metadataFileOffset = metadataSection.PointerToRawData + metadataOffsetInSection;
        var oldMetadataEnd = metadataFileOffset + _image.MetadataSize;
        var oldMetadataRvaEnd = _image.MetadataRva + _image.MetadataSize;

        // If metadata size changed, update section sizes and related offsets
        if (sizeDiff != 0)
        {
            // Update section's VirtualSize and SizeOfRawData, and shift subsequent sections' PointerToRawData
            var sectionOffset = _image.SectionHeadersOffset;
            bool foundMetadataSection = false;
            for (int i = 0; i < _image.Sections.Length; i++)
            {
                var sectionHeaderOffset = sectionOffset + i * 40;

                if (_image.Sections[i] == metadataSection)
                {
                    foundMetadataSection = true;
                    // VirtualSize is at offset 8, SizeOfRawData is at offset 16
                    var newVirtualSize = _image.Sections[i].VirtualSize + (uint)sizeDiff;
                    WriteUInt32(data, sectionHeaderOffset + 8, newVirtualSize);
                    WriteUInt32(data, sectionHeaderOffset + 16, newRawSize);

                    // Update SizeOfCode in optional header if this is a code section
                    // Section characteristics: IMAGE_SCN_CNT_CODE = 0x20
                    // Only update if rawSizeDiff != 0 (section actually grew in file)
                    if ((metadataSection.Characteristics & 0x20) != 0 && rawSizeDiff != 0)
                    {
                        var optHdrOffset = _image.OptionalHeaderOffset;
                        var oldSizeOfCode = BitConverter.ToUInt32(data, optHdrOffset + 4);
                        WriteUInt32(data, optHdrOffset + 4, oldSizeOfCode + (uint)rawSizeDiff);
                    }
                }
                else if (foundMetadataSection && rawSizeDiff != 0)
                {
                    // Shift PointerToRawData for sections after the metadata section
                    // Only needed if the section file size actually changed
                    // PointerToRawData is at offset 20 in the section header
                    var oldPointer = _image.Sections[i].PointerToRawData;
                    var newPointer = oldPointer + (uint)rawSizeDiff;
                    WriteUInt32(data, sectionHeaderOffset + 20, newPointer);
                }
            }

            // Update AddressOfEntryPoint if it's after metadata
            var optionalHeaderOffset = _image.OptionalHeaderOffset;
            var isPE64 = _image.IsPE64;
            var entryPointOffset = optionalHeaderOffset + 16; // AddressOfEntryPoint is at offset 16 in optional header
            var entryPointRva = BitConverter.ToUInt32(data, entryPointOffset);
            if (entryPointRva >= oldMetadataRvaEnd && entryPointRva > 0)
            {
                WriteUInt32(data, entryPointOffset, entryPointRva + (uint)sizeDiff);
            }

            // Update data directory RVAs for entries that are in the metadata section and after metadata
            // Data directories start at offset 96 (PE32) or 112 (PE32+) from optional header start
            var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);

            // Get metadata section bounds to check if data directory is in the same section
            var metadataSectionStart = metadataSection.VirtualAddress;
            var metadataSectionEnd = metadataSection.VirtualAddress + metadataSection.VirtualSize + (uint)sizeDiff;

            // Check each data directory and update RVA if it's in the metadata section and after metadata
            // Indices: 0=Export, 1=Import, 2=Resource, 3=Exception, 4=Security, 5=BaseReloc, 6=Debug, ...
            for (int dirIndex = 0; dirIndex < 16; dirIndex++)
            {
                var dirEntryOffset = dataDirectoriesOffset + dirIndex * 8;
                if (dirEntryOffset + 8 > data.Length) break;

                var dirRva = BitConverter.ToUInt32(data, dirEntryOffset);
                // Only update if the RVA is in the metadata section AND after the original metadata end
                if (dirRva >= oldMetadataRvaEnd && dirRva > 0 &&
                    dirRva >= metadataSectionStart && dirRva < metadataSectionEnd)
                {
                    var newDirRva = dirRva + (uint)sizeDiff;
                    WriteUInt32(data, dirEntryOffset, newDirRva);
                }
            }

            // If debug directory exists and is after metadata, also patch the debug directory entries
            if (!_image.DebugDirectory.IsZero && _image.DebugDirectory.VirtualAddress >= oldMetadataRvaEnd)
            {
                PatchDebugDirectory(data, oldMetadataEnd, sizeDiff);
            }

            // If import directory exists and is after metadata, patch its internal RVAs
            if (!_image.ImportDirectory.IsZero && _image.ImportDirectory.VirtualAddress >= oldMetadataRvaEnd)
            {
                PatchImportDirectory(data, oldMetadataRvaEnd, sizeDiff);
            }

            // Patch base relocations that point to addresses after metadata
            PatchBaseRelocations(data, oldMetadataRvaEnd, sizeDiff, metadataSection);
        }

        // Update CLI header's Resources RVA if resources are after metadata
        // CLI header offsets: 24=Resources RVA, 28=Resources Size
        var resourcesRva = BitConverter.ToUInt32(data, cliHeaderOffset + 24);
        var resourcesSize = BitConverter.ToUInt32(data, cliHeaderOffset + 28);
        if (resourcesRva >= oldMetadataRvaEnd && resourcesRva > 0 && sizeDiff != 0)
        {
            WriteUInt32(data, cliHeaderOffset + 24, resourcesRva + (uint)sizeDiff);
        }

        // Update CLI header's VTableFixups RVA if after metadata (offset 40)
        var vtableFixupsRva = BitConverter.ToUInt32(data, cliHeaderOffset + 40);
        if (vtableFixupsRva >= oldMetadataRvaEnd && vtableFixupsRva > 0 && sizeDiff != 0)
        {
            WriteUInt32(data, cliHeaderOffset + 40, vtableFixupsRva + (uint)sizeDiff);
        }

        // Update strong name signature region if present
        // (Will be zeroed out before signing)
        if (_image.StrongNameRva != 0 && _image.StrongNameSize != 0)
        {
            // Calculate new strong name offset (might have shifted)
            var snRva = _image.StrongNameRva;
            var originalSnOffset = _image.ResolveRva(snRva);
            var newSnOffset = originalSnOffset;

            // If strong name is after metadata, it shifted
            if (originalSnOffset >= oldMetadataEnd && sizeDiff != 0)
            {
                newSnOffset = (uint)(originalSnOffset + sizeDiff);

                // Also update the CLI header's StrongNameSignature RVA
                var newSnRva = snRva + (uint)sizeDiff;
                WriteUInt32(data, cliHeaderOffset + 32, newSnRva);
            }

            // Clear strong name signature (will be re-signed later)
            for (int i = 0; i < _image.StrongNameSize; i++)
            {
                if (newSnOffset + i < data.Length)
                    data[newSnOffset + i] = 0;
            }
        }
    }

    private void PatchDebugDirectory(byte[] data, uint oldMetadataEnd, int sizeDiff)
    {
        // Find debug directory in the data
        var debugDirRva = _image.DebugDirectory.VirtualAddress;
        var debugDirSize = _image.DebugDirectory.Size;

        // Get the debug directory section
        var debugSection = _image.GetSectionAtRva(debugDirRva);
        if (debugSection == null) return;

        // Get metadata section to check if debug dir is in same section
        var metadataSection = _image.GetSectionAtRva(_image.MetadataRva);
        var isSameSection = debugSection == metadataSection;

        // Calculate original debug directory file offset
        var debugDirFileOffset = debugDirRva - debugSection.VirtualAddress + debugSection.PointerToRawData;

        // If debug directory is in the same section as metadata AND after metadata, it has shifted
        var actualDebugDirOffset = debugDirFileOffset;
        if (isSameSection && debugDirFileOffset >= oldMetadataEnd)
        {
            actualDebugDirOffset = (uint)(debugDirFileOffset + sizeDiff);
        }

        // For each debug directory entry (28 bytes each)
        var entryCount = (int)(debugDirSize / 28);
        for (int i = 0; i < entryCount; i++)
        {
            var entryOffset = (int)actualDebugDirOffset + i * 28;
            if (entryOffset + 28 > data.Length)
                break;

            // Read AddressOfRawData (RVA) at offset 20 and PointerToRawData at offset 24
            var addrOfRawData = BitConverter.ToUInt32(data, entryOffset + 20);
            var ptrToRawData = BitConverter.ToUInt32(data, entryOffset + 24);

            // Calculate metadata end in RVA terms for AddressOfRawData comparison
            var oldMetadataRvaEnd = _image.MetadataRva + _image.MetadataSize;

            // If the RVA is after the old metadata end, it needs adjustment
            if (addrOfRawData >= oldMetadataRvaEnd && addrOfRawData > 0)
            {
                var newAddr = (uint)(addrOfRawData + sizeDiff);
                WriteUInt32(data, entryOffset + 20, newAddr);
            }

            // If the file pointer is after the old metadata end, it needs adjustment
            if (ptrToRawData >= oldMetadataEnd && ptrToRawData > 0)
            {
                var newPtr = (uint)(ptrToRawData + sizeDiff);
                WriteUInt32(data, entryOffset + 24, newPtr);
            }
        }
    }

    private void PatchImportDirectory(byte[] data, uint oldMetadataRvaEnd, int sizeDiff)
    {
        // Find the import directory in the patched data directories
        var optionalHeaderOffset = _image.OptionalHeaderOffset;
        var isPE64 = _image.IsPE64;
        var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);

        // Import Directory is index 1
        var importDirEntryOffset = dataDirectoriesOffset + 1 * 8;
        if (importDirEntryOffset + 8 > data.Length) return;

        var importDirRva = BitConverter.ToUInt32(data, importDirEntryOffset);
        var importDirSize = BitConverter.ToUInt32(data, importDirEntryOffset + 4);

        if (importDirRva == 0 || importDirSize == 0) return;

        // Get the metadata section
        var metadataSection = _image.GetSectionAtRva(_image.MetadataRva);
        if (metadataSection == null) return;

        // Calculate the file offset of the import directory
        // Since importDirRva is already patched (updated RVA), we need to use the original section info
        // but account for the file data shift
        var originalImportDirRva = _image.ImportDirectory.VirtualAddress;
        var importDirFileOffset = originalImportDirRva - metadataSection.VirtualAddress + metadataSection.PointerToRawData;

        // If import dir was after metadata, it has shifted in the file
        if (originalImportDirRva >= oldMetadataRvaEnd)
        {
            importDirFileOffset = importDirFileOffset + (uint)sizeDiff;
        }

        // Each import directory entry is 20 bytes
        // Structure: OriginalFirstThunk(4), TimeDateStamp(4), ForwarderChain(4), Name(4), FirstThunk(4)
        // The last entry is all zeros (terminator)
        var entrySize = 20;
        var numEntries = (int)(importDirSize / entrySize);

        for (int i = 0; i < numEntries; i++)
        {
            var entryOffset = (int)importDirFileOffset + i * entrySize;
            if (entryOffset + entrySize > data.Length) break;

            // Read all the RVA fields
            var originalFirstThunk = BitConverter.ToUInt32(data, entryOffset + 0);
            var name = BitConverter.ToUInt32(data, entryOffset + 12);
            var firstThunk = BitConverter.ToUInt32(data, entryOffset + 16);

            // Check if this is the null terminator
            if (originalFirstThunk == 0 && name == 0 && firstThunk == 0) break;

            // Update OriginalFirstThunk if it's after metadata
            if (originalFirstThunk >= oldMetadataRvaEnd && originalFirstThunk > 0)
            {
                WriteUInt32(data, entryOffset + 0, originalFirstThunk + (uint)sizeDiff);
            }

            // Update Name if it's after metadata
            if (name >= oldMetadataRvaEnd && name > 0)
            {
                WriteUInt32(data, entryOffset + 12, name + (uint)sizeDiff);
            }

            // Update FirstThunk if it's after metadata
            if (firstThunk >= oldMetadataRvaEnd && firstThunk > 0)
            {
                WriteUInt32(data, entryOffset + 16, firstThunk + (uint)sizeDiff);
            }

            // Patch the Import Lookup Table entries for this import
            // The ILT is at the (patched) OriginalFirstThunk RVA
            var patchedIltRva = BitConverter.ToUInt32(data, entryOffset + 0);
            if (patchedIltRva > 0)
            {
                // Calculate file offset of ILT
                // Since we patched the RVA, use the original to find the file location
                var iltFileOffset = originalFirstThunk - metadataSection.VirtualAddress + metadataSection.PointerToRawData;
                if (originalFirstThunk >= oldMetadataRvaEnd)
                {
                    iltFileOffset = iltFileOffset + (uint)sizeDiff;
                }

                // Patch ILT entries (4 bytes for PE32, 8 for PE64)
                var iltEntrySize = _image.IsPE64 ? 8 : 4;
                for (int j = 0; j < 100; j++) // reasonable limit
                {
                    var iltEntryOffset = (int)iltFileOffset + j * iltEntrySize;
                    if (iltEntryOffset + iltEntrySize > data.Length) break;

                    var iltEntryValue = BitConverter.ToUInt32(data, iltEntryOffset);
                    if (iltEntryValue == 0) break; // null terminator

                    // Skip if ordinal import (high bit set)
                    if ((iltEntryValue & 0x80000000) != 0) continue;

                    // Patch if pointing to data after metadata
                    if (iltEntryValue >= oldMetadataRvaEnd)
                    {
                        WriteUInt32(data, iltEntryOffset, iltEntryValue + (uint)sizeDiff);
                    }
                }
            }
        }
    }

    private void PatchBaseRelocations(byte[] data, uint oldMetadataRvaEnd, int sizeDiff, Section metadataSection)
    {
        // Find base relocation directory (index 5)
        var optionalHeaderOffset = _image.OptionalHeaderOffset;
        var isPE64 = _image.IsPE64;
        var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);
        var relocDirOffset = dataDirectoriesOffset + 5 * 8;

        if (relocDirOffset + 8 > data.Length) return;

        var relocRva = BitConverter.ToUInt32(data, relocDirOffset);
        var relocSize = BitConverter.ToUInt32(data, relocDirOffset + 4);

        if (relocRva == 0 || relocSize == 0) return;

        // Find the relocation section
        var relocSection = _image.GetSectionAtRva(relocRva);
        if (relocSection == null) return;

        // Calculate file offset of relocation data
        var relocFileOffset = (int)(relocRva - relocSection.VirtualAddress + relocSection.PointerToRawData);

        // The relocation section may have shifted if it's after metadata
        if (relocSection.PointerToRawData > metadataSection.PointerToRawData)
        {
            relocFileOffset += sizeDiff;
        }

        // Parse and patch relocation entries
        var pos = relocFileOffset;
        while (pos < relocFileOffset + relocSize && pos + 8 <= data.Length)
        {
            var pageRva = BitConverter.ToUInt32(data, pos);
            var blockSize = BitConverter.ToUInt32(data, pos + 4);

            if (blockSize == 0) break;

            // Process entries in this block (each entry is 2 bytes)
            var numEntries = (blockSize - 8) / 2;
            for (int i = 0; i < numEntries && pos + 8 + i * 2 + 2 <= data.Length; i++)
            {
                var entryOffset = pos + 8 + i * 2;
                var entry = BitConverter.ToUInt16(data, entryOffset);

                var type = entry >> 12;
                var offset = entry & 0xFFF;

                // Skip if type is 0 (IMAGE_REL_BASED_ABSOLUTE - padding)
                if (type == 0) continue;

                // Calculate the RVA this relocation points to
                var targetRva = pageRva + (uint)offset;

                // If the target RVA is after metadata, it needs to be updated
                if (targetRva >= oldMetadataRvaEnd)
                {
                    var newTargetRva = targetRva + (uint)sizeDiff;
                    var newOffset = (int)(newTargetRva - pageRva);

                    // If the new offset still fits in the same page, update it
                    if (newOffset is >= 0 and < 0x1000)
                    {
                        var newEntry = (ushort)((type << 12) | (newOffset & 0xFFF));
                        data[entryOffset] = (byte)(newEntry & 0xFF);
                        data[entryOffset + 1] = (byte)((newEntry >> 8) & 0xFF);
                    }
                    // If it moved to a different page, we'd need to restructure the block
                    // For now, just update within the same page
                }
            }

            pos += (int)blockSize;
        }
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>
    /// Writes a PE file with modified metadata.
    /// </summary>
    public static void Write(string path, PEImage image, MetadataWriter metadataWriter)
    {
        var newMetadata = metadataWriter.Build();
        var writer = new PEWriter(image, newMetadata);
        writer.Write(path);
    }

    /// <summary>
    /// Builds a PE file with modified metadata.
    /// </summary>
    public static byte[] Build(PEImage image, MetadataWriter metadataWriter)
    {
        var newMetadata = metadataWriter.Build();
        var writer = new PEWriter(image, newMetadata);
        return writer.Build();
    }
}
