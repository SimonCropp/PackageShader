using System.IO.Compression;

namespace Alias.Lib.Pdb;

/// <summary>
/// Handles PDB file operations for assembly modifications.
/// </summary>
public static class PdbHandler
{
    /// <summary>
    /// Copies an external PDB file alongside the target assembly.
    /// </summary>
    public static void CopyExternalPdb(string sourceDll, string targetDll)
    {
        var sourcePdb = Path.ChangeExtension(sourceDll, ".pdb");
        var targetPdb = Path.ChangeExtension(targetDll, ".pdb");

        // Skip if source and target are the same file
        if (string.Equals(Path.GetFullPath(sourcePdb), Path.GetFullPath(targetPdb), StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(sourcePdb))
        {
            // Copy the PDB file - no modifications needed since we don't change method tokens
            File.Copy(sourcePdb, targetPdb, overwrite: true);
        }
    }

    /// <summary>
    /// Checks if an assembly has symbols (either external or embedded).
    /// </summary>
    public static bool HasSymbols(string dllPath)
    {
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (File.Exists(pdbPath))
            return true;

        // Check for embedded PDB
        var data = File.ReadAllBytes(dllPath);
        return HasEmbeddedPdb(data);
    }

    /// <summary>
    /// Checks if PE data contains an embedded PDB.
    /// </summary>
    public static bool HasEmbeddedPdb(byte[] data)
    {
        // Find debug directory
        var debugEntries = FindDebugDirectoryEntries(data);

        foreach (var entry in debugEntries)
        {
            // Type 17 = EmbeddedPortablePdb
            if (entry.type == 17)
                return true;
        }

        return false;
    }

    private static List<(int type, int offset, int size)> FindDebugDirectoryEntries(byte[] data)
    {
        var entries = new List<(int type, int offset, int size)>();

        if (data.Length < 64)
            return entries;

        // Find PE header
        var peOffset = BitConverter.ToInt32(data, 60);
        if (peOffset + 24 > data.Length)
            return entries;

        // Determine PE32 or PE32+
        var optionalHeaderOffset = peOffset + 24;
        var magic = BitConverter.ToUInt16(data, optionalHeaderOffset);
        var isPE64 = magic == 0x20b;

        // Find debug data directory (index 6)
        var dataDirectoriesOffset = optionalHeaderOffset + (isPE64 ? 112 : 96);
        var debugDirOffset = dataDirectoriesOffset + 6 * 8;

        if (debugDirOffset + 8 > data.Length)
            return entries;

        var debugRva = BitConverter.ToUInt32(data, debugDirOffset);
        var debugSize = BitConverter.ToUInt32(data, debugDirOffset + 4);

        if (debugRva == 0 || debugSize == 0)
            return entries;

        // Resolve debug directory file offset
        var debugFileOffset = ResolveRva(data, peOffset, debugRva);
        if (debugFileOffset == 0)
            return entries;

        // Each debug directory entry is 28 bytes
        var entryCount = (int)(debugSize / 28);

        for (int i = 0; i < entryCount; i++)
        {
            var entryOffset = (int)debugFileOffset + i * 28;
            if (entryOffset + 28 > data.Length)
                break;

            var type = BitConverter.ToInt32(data, entryOffset + 12);
            var dataSize = BitConverter.ToInt32(data, entryOffset + 16);
            var dataOffset = BitConverter.ToInt32(data, entryOffset + 24);

            if (dataOffset > 0 && dataSize > 0)
            {
                entries.Add((type, dataOffset, dataSize));
            }
        }

        return entries;
    }

    private static uint ResolveRva(byte[] data, int peOffset, uint rva)
    {
        var numberOfSections = BitConverter.ToUInt16(data, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(data, peOffset + 20);
        var sectionHeadersOffset = peOffset + 24 + optionalHeaderSize;

        for (int i = 0; i < numberOfSections; i++)
        {
            var sectionOffset = sectionHeadersOffset + i * 40;
            if (sectionOffset + 40 > data.Length)
                break;

            var virtualAddress = BitConverter.ToUInt32(data, sectionOffset + 12);
            var sizeOfRawData = BitConverter.ToUInt32(data, sectionOffset + 16);
            var pointerToRawData = BitConverter.ToUInt32(data, sectionOffset + 20);

            if (rva >= virtualAddress && rva < virtualAddress + sizeOfRawData)
            {
                return rva - virtualAddress + pointerToRawData;
            }
        }

        return 0;
    }
}
