using System.Reflection.Metadata.Ecma335;
using Alias.Lib.Metadata;
using Alias.Lib.Metadata.Tables;
using CodedIndex = Alias.Lib.Metadata.Tables.CodedIndex;
using Alias.Lib.Modification;

namespace Alias.Lib.PE;

/// <summary>
/// Writes modified PE files using streaming where possible.
/// </summary>
public sealed class StreamingPEWriter
{
    private readonly StreamingPEFile _source;
    private readonly StreamingMetadataReader _metadata;
    private readonly ModificationPlan _plan;

    public StreamingPEWriter(StreamingPEFile source, StreamingMetadataReader metadata, ModificationPlan plan)
    {
        _source = source;
        _metadata = metadata;
        _plan = plan;
    }

    /// <summary>
    /// Writes the modified PE to the output stream.
    /// </summary>
    public void Write(Stream output)
    {
        var strategy = _plan.GetStrategy();

        switch (strategy)
        {
            case ModificationStrategy.InPlacePatch:
                WriteWithInPlacePatches(output);
                break;

            case ModificationStrategy.MetadataRebuildWithPadding:
            case ModificationStrategy.FullMetadataSectionRebuild:
                WriteWithMetadataRebuild(output);
                break;
        }
    }

    /// <summary>
    /// Writes by copying the file and applying in-place patches.
    /// Used when no heap growth is needed.
    /// </summary>
    private void WriteWithInPlacePatches(Stream output)
    {
        // Copy entire file first
        _source.CopyRegion(0, _source.FileLength, output);

        // Build patch list
        var patches = BuildInPlacePatches();

        // Apply patches
        foreach (var (offset, data) in patches)
        {
            output.Position = offset;
            output.Write(data, 0, data.Length);
        }
    }

    /// <summary>
    /// Writes by streaming unchanged parts and inserting rebuilt metadata.
    /// </summary>
    private void WriteWithMetadataRebuild(Stream output)
    {
        var metadataSection = _source.GetSectionAtRva(_source.MetadataRva)
            ?? throw new InvalidOperationException("Metadata section not found");

        var metadataOffsetInSection = _source.MetadataRva - metadataSection.VirtualAddress;
        var metadataFileOffset = metadataSection.PointerToRawData + metadataOffsetInSection;

        // Build new metadata
        using var newMetadataStream = new MemoryStream();
        var metadataWriter = new StreamingMetadataWriter(_metadata, _plan);
        metadataWriter.Write(newMetadataStream);
        var newMetadata = newMetadataStream.ToArray();

        var oldMetadataSize = (int)_source.MetadataSize;
        var newMetadataSize = newMetadata.Length;
        var sizeDiff = newMetadataSize - oldMetadataSize;

        // Calculate section sizes
        var oldVirtualSize = metadataSection.VirtualSize;
        var newVirtualSize = oldVirtualSize + sizeDiff;
        var oldRawSize = (int)metadataSection.SizeOfRawData;

        int newRawSize;
        int rawSizeDiff;

        if (newVirtualSize <= oldRawSize)
        {
            // Fits in existing section padding
            newRawSize = oldRawSize;
            rawSizeDiff = 0;
        }
        else
        {
            // Need to expand section
            var fileAlignment = GetFileAlignment();
            newRawSize = (int)AlignUp((uint)newVirtualSize, fileAlignment);
            rawSizeDiff = newRawSize - oldRawSize;
        }

        // Copy headers (up to first section)
        var firstSectionOffset = GetFirstSectionOffset();
        _source.CopyRegion(0, firstSectionOffset, output);

        // Write sections
        foreach (var section in _source.Sections)
        {
            // Ensure we're at the correct offset
            while (output.Position < section.PointerToRawData + (section.PointerToRawData > metadataSection.PointerToRawData ? rawSizeDiff : 0))
            {
                output.WriteByte(0);
            }

            if (section.VirtualAddress == metadataSection.VirtualAddress)
            {
                // This is the metadata section - write modified version
                var sectionData = new byte[newRawSize];

                // Copy data before metadata
                var beforeMetadata = _source.ReadBytesAt((int)section.PointerToRawData, (int)metadataOffsetInSection);
                Array.Copy(beforeMetadata, 0, sectionData, 0, beforeMetadata.Length);

                // Copy new metadata
                Array.Copy(newMetadata, 0, sectionData, (int)metadataOffsetInSection, newMetadata.Length);

                // Copy data after metadata
                var afterMetadataOffset = (int)(metadataOffsetInSection + oldMetadataSize);
                var afterMetadataSize = (int)(oldVirtualSize - afterMetadataOffset);
                if (afterMetadataSize > 0)
                {
                    var afterMetadata = _source.ReadBytesAt(
                        (int)section.PointerToRawData + afterMetadataOffset,
                        afterMetadataSize);
                    Array.Copy(afterMetadata, 0, sectionData,
                        (int)metadataOffsetInSection + newMetadataSize,
                        afterMetadata.Length);
                }

                output.Write(sectionData, 0, sectionData.Length);
            }
            else
            {
                // Copy section unchanged
                _source.CopyRegion(section.PointerToRawData, section.SizeOfRawData, output);
            }
        }

        // Patch headers
        PatchHeaders(output, metadataSection, sizeDiff, rawSizeDiff, (uint)newRawSize, (uint)newMetadataSize);
    }

    private List<(long offset, byte[] data)> BuildInPlacePatches()
    {
        var patches = new List<(long offset, byte[] data)>();

        // Patch Assembly table row
        foreach (var (rid, row) in _plan.ModifiedAssemblyRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.Assembly, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.BlobIndexSize, _metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch AssemblyRef table rows
        foreach (var (rid, row) in _plan.ModifiedAssemblyRefRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.AssemblyRef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.BlobIndexSize, _metadata.StringIndexSize);
            patches.Add((offset, ms.ToArray()));
        }

        // Patch TypeDef table rows
        foreach (var (rid, row) in _plan.ModifiedTypeDefRows)
        {
            var offset = _metadata.GetRowOffset(TableIndex.TypeDef, rid);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            row.Write(writer, _metadata.StringIndexSize,
                _metadata.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                _metadata.GetTableIndexSize(TableIndex.Field),
                _metadata.GetTableIndexSize(TableIndex.MethodDef));
            patches.Add((offset, ms.ToArray()));
        }

        return patches;
    }

    private void PatchHeaders(Stream output, SectionInfo metadataSection,
        int sizeDiff, int rawSizeDiff, uint newRawSize, uint newMetadataSize)
    {
        // Patch CLI header with new metadata size
        output.Position = _source.CLIHeaderFileOffset + 12;
        WriteUInt32(output, newMetadataSize);

        var oldMetadataRvaEnd = _source.MetadataRva + _source.MetadataSize;
        var metadataFileOffset = metadataSection.PointerToRawData +
            (_source.MetadataRva - metadataSection.VirtualAddress);
        var oldMetadataEnd = metadataFileOffset + _source.MetadataSize;

        // Calculate the RVA range of the metadata section
        var metadataSectionRvaStart = metadataSection.VirtualAddress;
        var metadataSectionRvaEnd = metadataSection.VirtualAddress + metadataSection.VirtualSize;

        if (sizeDiff == 0)
            return;

        // Patch section header
        var sectionOffset = _source.SectionHeadersOffset;
        for (int i = 0; i < _source.Sections.Length; i++)
        {
            var section = _source.Sections[i];
            var headerOffset = sectionOffset + i * 40;

            if (section.VirtualAddress == metadataSection.VirtualAddress)
            {
                // Update VirtualSize and SizeOfRawData
                output.Position = headerOffset + 8;
                WriteUInt32(output, (uint)(section.VirtualSize + sizeDiff));
                output.Position = headerOffset + 16;
                WriteUInt32(output, newRawSize);
            }
            else if (section.PointerToRawData > metadataSection.PointerToRawData && rawSizeDiff != 0)
            {
                // Shift PointerToRawData for sections after metadata section
                output.Position = headerOffset + 20;
                WriteUInt32(output, (uint)(section.PointerToRawData + rawSizeDiff));
            }
        }

        // Patch entry point if it's in the metadata section and after metadata
        var entryPointOffset = _source.OptionalHeaderOffset + 16;

        output.Position = entryPointOffset;
        var entryPointRva = ReadUInt32(output);
        // Only patch if entry point is within metadata section and after the metadata
        bool entryPointInMetadataSection = entryPointRva >= metadataSectionRvaStart && entryPointRva < metadataSectionRvaEnd;
        bool entryPointAfterMetadata = entryPointRva >= oldMetadataRvaEnd;
        if (entryPointInMetadataSection && entryPointAfterMetadata && entryPointRva > 0)
        {
            output.Position = entryPointOffset;
            WriteUInt32(output, (uint)(entryPointRva + sizeDiff));

            // For PE64: The entry point stub contains a RIP-relative JMP instruction (FF 25 disp32)
            // Since the stub shifted but the IAT didn't, we need to adjust the displacement.
            // For PE32: The stub contains an absolute VA that's fixed up by base relocations.
            // We don't modify the stub bytes, but we do need to update the relocation entries.
            if (_source.IsPE64)
            {
                // Calculate the file offset of the entry point stub (after shift)
                long entryPointStubFileOffset = entryPointRva - metadataSection.VirtualAddress
                    + metadataSection.PointerToRawData + sizeDiff;

                // Read the first two bytes to check for FF 25 (JMP [RIP+disp32])
                output.Position = entryPointStubFileOffset;
                var stubBytes = new byte[6];
                _ = output.Read(stubBytes, 0, 6);

                if (stubBytes[0] == 0xFF && stubBytes[1] == 0x25)
                {
                    int disp32 = BitConverter.ToInt32(stubBytes, 2);
                    int newDisp32 = disp32 - sizeDiff;

                    output.Position = entryPointStubFileOffset + 2;
                    WriteInt32(output, newDisp32);
                }
            }
        }

        // Patch data directories
        var isPE64 = _source.IsPE64;
        var dataDirectoriesOffset = _source.OptionalHeaderOffset + (isPE64 ? 112 : 96);

        // Read data directory values BEFORE patching them
        var debugDirOffset = dataDirectoriesOffset + 6 * 8;
        output.Position = debugDirOffset;
        var debugDirRva = ReadUInt32(output);
        var debugDirSize = ReadUInt32(output);

        var importDirOffset = dataDirectoriesOffset + 1 * 8;
        output.Position = importDirOffset;
        var importDirRva = ReadUInt32(output);
        var importDirSize = ReadUInt32(output);

        for (int dirIndex = 0; dirIndex < 16; dirIndex++)
        {
            var dirEntryOffset = dataDirectoriesOffset + dirIndex * 8;
            output.Position = dirEntryOffset;
            var dirRva = ReadUInt32(output);

            // Only patch if the RVA is within the metadata section and after metadata
            bool inMetadataSection = dirRva >= metadataSectionRvaStart && dirRva < metadataSectionRvaEnd;
            bool afterMetadata = dirRva >= oldMetadataRvaEnd;
            bool shouldPatch = inMetadataSection && afterMetadata && dirRva > 0;
            if (shouldPatch)
            {
                output.Position = dirEntryOffset;
                WriteUInt32(output, (uint)(dirRva + sizeDiff));
            }
        }

        // Patch Import directory internal RVAs
        // The Import Directory Table contains RVAs to Import Lookup Table, DLL name, and Import Address Table
        // Note: importDirRva and importDirSize were read BEFORE the patching loop above
        bool importInMetadataSection = importDirRva >= metadataSectionRvaStart && importDirRva < metadataSectionRvaEnd;
        if (importDirRva > 0 && importDirSize > 0 && importInMetadataSection)
        {
            // Calculate the file offset of the Import Directory Table (may have shifted)
            long importFileOffset = importDirRva - metadataSection.VirtualAddress + metadataSection.PointerToRawData;
            if (importDirRva >= oldMetadataRvaEnd)
                importFileOffset += sizeDiff;

            // Each IMAGE_IMPORT_DESCRIPTOR is 20 bytes
            // Structure: OriginalFirstThunk(4), TimeDateStamp(4), ForwarderChain(4), Name(4), FirstThunk(4)
            int maxEntries = (int)(importDirSize / 20);
            for (int i = 0; i < maxEntries; i++)
            {
                var entryOffset = importFileOffset + i * 20;

                // Check if this is a null terminator (all zeros)
                output.Position = entryOffset;
                var originalFirstThunk = ReadUInt32(output);
                if (originalFirstThunk == 0)
                    break;  // Null terminator

                // Read other RVAs
                output.Position = entryOffset + 12;  // Name field
                var nameRva = ReadUInt32(output);
                var firstThunk = ReadUInt32(output);

                // Patch OriginalFirstThunk if it points to shifted data
                bool oftInSection = originalFirstThunk >= metadataSectionRvaStart && originalFirstThunk < metadataSectionRvaEnd;
                if (oftInSection && originalFirstThunk >= oldMetadataRvaEnd)
                {
                    output.Position = entryOffset;
                    WriteUInt32(output, (uint)(originalFirstThunk + sizeDiff));
                }

                // Patch Name if it points to shifted data
                bool nameInSection = nameRva >= metadataSectionRvaStart && nameRva < metadataSectionRvaEnd;
                if (nameInSection && nameRva >= oldMetadataRvaEnd && nameRva > 0)
                {
                    output.Position = entryOffset + 12;
                    WriteUInt32(output, (uint)(nameRva + sizeDiff));
                }

                // Patch FirstThunk if it points to shifted data
                bool ftInSection = firstThunk >= metadataSectionRvaStart && firstThunk < metadataSectionRvaEnd;
                if (ftInSection && firstThunk >= oldMetadataRvaEnd && firstThunk > 0)
                {
                    output.Position = entryOffset + 16;
                    WriteUInt32(output, (uint)(firstThunk + sizeDiff));
                }
            }

            // Patch both Import Lookup Table and Import Address Table entries
            // Re-read the patched values
            output.Position = importFileOffset;
            var patchedOft = ReadUInt32(output);
            output.Position = importFileOffset + 16;  // FirstThunk field
            var patchedFt = ReadUInt32(output);

            // Each entry is 4 bytes (PE32) or 8 bytes (PE64)
            int entrySize = _source.IsPE64 ? 8 : 4;

            // Patch ILT entries if OriginalFirstThunk is valid
            // Note: patchedOft is the ALREADY-PATCHED RVA, so it already represents the new location.
            // The file offset is computed directly from the patched RVA - no need to add sizeDiff again.
            if (patchedOft > 0)
            {
                long iltFileOffset = patchedOft - metadataSection.VirtualAddress + metadataSection.PointerToRawData;
                PatchImportTableEntries(output, iltFileOffset, entrySize, metadataSectionRvaStart, metadataSectionRvaEnd, oldMetadataRvaEnd, sizeDiff);
            }

            // Patch IAT entries if FirstThunk is valid and different from OriginalFirstThunk
            // Note: patchedFt is the RVA from the import directory. For IAT at 0x2000 (before metadata),
            // this is unchanged and we should calculate the file offset normally.
            if (patchedFt > 0 && patchedFt != patchedOft)
            {
                long iatFileOffset = patchedFt - metadataSection.VirtualAddress + metadataSection.PointerToRawData;
                PatchImportTableEntries(output, iatFileOffset, entrySize, metadataSectionRvaStart, metadataSectionRvaEnd, oldMetadataRvaEnd, sizeDiff);
            }
        }

        // Patch debug directory entries (each entry is 28 bytes)
        // The entries contain AddressOfRawData (RVA at +20) and PointerToRawData (file offset at +24)
        // Only process if debug directory is within the metadata section
        bool debugDirInMetadataSection = debugDirRva >= metadataSectionRvaStart && debugDirRva < metadataSectionRvaEnd;
        if (debugDirRva > 0 && debugDirSize > 0 && debugDirInMetadataSection)
        {
            // Calculate the new file offset of the debug directory
            long debugDirFileOffset = debugDirRva - metadataSection.VirtualAddress + metadataSection.PointerToRawData;
            bool debugDirAfterMetadata = debugDirRva >= oldMetadataRvaEnd;
            if (debugDirAfterMetadata)
                debugDirFileOffset += sizeDiff;

            int entryCount = (int)(debugDirSize / 28);
            for (int i = 0; i < entryCount; i++)
            {
                var entryOffset = debugDirFileOffset + i * 28;

                // Read AddressOfRawData (RVA) at offset 20
                output.Position = entryOffset + 20;
                var addressOfRawData = ReadUInt32(output);

                // Read PointerToRawData (file offset) at offset 24
                var pointerToRawData = ReadUInt32(output);

                // Update if they point to data within metadata section past the old metadata end
                bool addrInSection = addressOfRawData >= metadataSectionRvaStart && addressOfRawData < metadataSectionRvaEnd;
                if (addrInSection && addressOfRawData >= oldMetadataRvaEnd && addressOfRawData > 0)
                {
                    output.Position = entryOffset + 20;
                    WriteUInt32(output, (uint)(addressOfRawData + sizeDiff));
                }

                // For file offset, check if it's within the metadata section's file range
                var metadataSectionFileEnd = metadataSection.PointerToRawData + metadataSection.SizeOfRawData;
                bool ptrInSection = pointerToRawData >= metadataSection.PointerToRawData && pointerToRawData < metadataSectionFileEnd;
                if (ptrInSection && pointerToRawData >= oldMetadataEnd && pointerToRawData > 0)
                {
                    output.Position = entryOffset + 24;
                    WriteUInt32(output, (uint)(pointerToRawData + sizeDiff));
                }
            }
        }

        // Patch CLI header's Resources RVA if in metadata section and after metadata
        output.Position = _source.CLIHeaderFileOffset + 24;
        var resourcesRva = ReadUInt32(output);
        bool resourcesInMetadataSection = resourcesRva >= metadataSectionRvaStart && resourcesRva < metadataSectionRvaEnd;
        if (resourcesInMetadataSection && resourcesRva >= oldMetadataRvaEnd && resourcesRva > 0)
        {
            output.Position = _source.CLIHeaderFileOffset + 24;
            WriteUInt32(output, (uint)(resourcesRva + sizeDiff));
        }

        // Patch base relocation table entries that point to shifted addresses
        // The .reloc section contains fixup entries for addresses that need adjusting when the image is loaded
        // If those entries point to shifted data, we need to update the offsets
        var baseRelocDirOffset = dataDirectoriesOffset + 5 * 8;  // Data directory 5 is Base Relocation Table
        output.Position = baseRelocDirOffset;
        var baseRelocRva = ReadUInt32(output);
        var baseRelocSize = ReadUInt32(output);

        if (baseRelocRva > 0 && baseRelocSize > 0)
        {
            // Find the section containing the relocation table
            SectionInfo? relocTableSection = null;
            foreach (var s in _source.Sections)
            {
                if (baseRelocRva >= s.VirtualAddress && baseRelocRva < s.VirtualAddress + s.VirtualSize)
                {
                    relocTableSection = s;
                    break;
                }
            }

            if (relocTableSection != null)
            {
                // Calculate file offset of relocation table
                // Note: .reloc section is typically after .text, so its file offset doesn't change
                // unless rawSizeDiff != 0 and it comes after the metadata section
                long relocTableFileOffset = baseRelocRva - relocTableSection.VirtualAddress
                    + relocTableSection.PointerToRawData;

                // Only adjust if .reloc section comes after metadata section and section grew
                if (relocTableSection.PointerToRawData > metadataSection.PointerToRawData && rawSizeDiff != 0)
                    relocTableFileOffset += rawSizeDiff;

                // Process each relocation block
                long blockOffset = 0;
                while (blockOffset < baseRelocSize)
                {
                    output.Position = relocTableFileOffset + blockOffset;
                    var pageRva = ReadUInt32(output);
                    var blockSize = ReadUInt32(output);

                    if (blockSize == 0)
                        break;

                    // Check if this page overlaps with the shifted region
                    bool pageOverlapsShiftedRegion = pageRva >= metadataSectionRvaStart &&
                        pageRva < metadataSectionRvaEnd &&
                        pageRva + 0x1000 > oldMetadataRvaEnd;

                    if (pageOverlapsShiftedRegion)
                    {
                        // Process entries in this block
                        var entryCount = (blockSize - 8) / 2;
                        for (int i = 0; i < entryCount; i++)
                        {
                            output.Position = relocTableFileOffset + blockOffset + 8 + i * 2;
                            var entry = ReadUInt16(output);
                            var type = entry >> 12;
                            var offsetInPage = entry & 0xFFF;

                            // Type 0 is padding, skip it
                            if (type == 0)
                                continue;

                            var targetRva = pageRva + offsetInPage;

                            // If this entry points to an address in the shifted region, update it
                            if (targetRva >= oldMetadataRvaEnd && targetRva < metadataSectionRvaEnd)
                            {
                                var newOffset = offsetInPage + sizeDiff;
                                // Check if new offset would overflow the page
                                if (newOffset < 0x1000)
                                {
                                    var newEntry = (ushort)((type << 12) | (newOffset & 0xFFF));
                                    output.Position = relocTableFileOffset + blockOffset + 8 + i * 2;
                                    WriteUInt16(output, newEntry);
                                }
                                // If overflow, would need to create new block - not handling that case
                            }
                        }
                    }

                    blockOffset += blockSize;
                }
            }
        }

        // Patch strong name signature RVA if in metadata section and after metadata
        if (_source.StrongNameRva != 0)
        {
            output.Position = _source.CLIHeaderFileOffset + 32;
            var snRva = ReadUInt32(output);
            bool snInMetadataSection = snRva >= metadataSectionRvaStart && snRva < metadataSectionRvaEnd;
            if (snInMetadataSection && snRva >= oldMetadataRvaEnd)
            {
                output.Position = _source.CLIHeaderFileOffset + 32;
                WriteUInt32(output, (uint)(snRva + sizeDiff));
            }

            // Clear strong name signature (will be re-signed)
            // Only adjust offset if strong name is in metadata section and after metadata
            var snOffset = _source.StrongNameFileOffset;
            var metadataSectionFileEnd = metadataSection.PointerToRawData + metadataSection.SizeOfRawData;
            bool snOffsetInSection = snOffset >= metadataSection.PointerToRawData && snOffset < metadataSectionFileEnd;
            if (snOffsetInSection && snOffset >= oldMetadataEnd)
                snOffset += sizeDiff;

            output.Position = snOffset;
            for (int i = 0; i < _source.StrongNameSize; i++)
                output.WriteByte(0);
        }
    }

    private uint GetFileAlignment()
    {
        var buffer = new byte[4];
        _source.ReadAt(_source.OptionalHeaderOffset + 36, buffer, 0, 4);
        return BitConverter.ToUInt32(buffer, 0);
    }

    private long GetFirstSectionOffset()
    {
        long minOffset = long.MaxValue;
        foreach (var section in _source.Sections)
        {
            if (section.PointerToRawData > 0 && section.PointerToRawData < minOffset)
                minOffset = section.PointerToRawData;
        }
        return minOffset;
    }

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private static ushort ReadUInt16(Stream stream)
    {
        var buffer = new byte[2];
        var totalRead = 0;
        while (totalRead < 2)
        {
            var read = stream.Read(buffer, totalRead, 2 - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return BitConverter.ToUInt16(buffer, 0);
    }

    private static uint ReadUInt32(Stream stream)
    {
        var buffer = new byte[4];
        var totalRead = 0;
        while (totalRead < 4)
        {
            var read = stream.Read(buffer, totalRead, 4 - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return BitConverter.ToUInt32(buffer, 0);
    }

    private static ulong ReadUInt64(Stream stream)
    {
        var buffer = new byte[8];
        var totalRead = 0;
        while (totalRead < 8)
        {
            var read = stream.Read(buffer, totalRead, 8 - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return BitConverter.ToUInt64(buffer, 0);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 32) & 0xFF));
        stream.WriteByte((byte)((value >> 40) & 0xFF));
        stream.WriteByte((byte)((value >> 48) & 0xFF));
        stream.WriteByte((byte)((value >> 56) & 0xFF));
    }

    private void PatchImportTableEntries(Stream output, long tableFileOffset, int entrySize,
        uint metadataSectionRvaStart, uint metadataSectionRvaEnd, uint oldMetadataRvaEnd, int sizeDiff)
    {
        for (int i = 0; i < 100; i++)  // Safety limit
        {
            output.Position = tableFileOffset + i * entrySize;
            var entry = _source.IsPE64 ? ReadUInt64(output) : ReadUInt32(output);
            if (entry == 0)
                break;  // Null terminator

            // Check if it's an ordinal import (high bit set) - skip those
            bool isOrdinal = _source.IsPE64 ? (entry & 0x8000000000000000UL) != 0 : (entry & 0x80000000U) != 0;
            if (isOrdinal)
                continue;

            // It's a hint/name RVA
            uint hintNameRva = (uint)(entry & 0x7FFFFFFF);
            bool hnInSection = hintNameRva >= metadataSectionRvaStart && hintNameRva < metadataSectionRvaEnd;
            if (hnInSection && hintNameRva >= oldMetadataRvaEnd)
            {
                output.Position = tableFileOffset + i * entrySize;
                if (_source.IsPE64)
                    WriteUInt64(output, (ulong)(hintNameRva + sizeDiff));
                else
                    WriteUInt32(output, (uint)(hintNameRva + sizeDiff));
            }
        }
    }

    /// <summary>
    /// Applies in-place patches directly to a file.
    /// Used when modifications don't change sizes.
    /// </summary>
    public static void ApplyInPlacePatches(string filePath, IReadOnlyList<(long offset, byte[] data)> patches)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
        foreach (var (offset, data) in patches)
        {
            fs.Position = offset;
            fs.Write(data, 0, data.Length);
        }
    }
}
