using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

public static class DiagnosticTest
{
    public static void Main(string[] args)
    {
        // Path to a test assembly
        var binDir = Path.GetDirectoryName(typeof(DiagnosticTest).Assembly.Location)!;
        var sourcePath = Path.Combine(binDir, "DummyAssembly.dll");

        Console.WriteLine($"Source: {sourcePath}");
        Console.WriteLine($"Exists: {File.Exists(sourcePath)}");

        var tempDir = Path.Combine(Path.GetTempPath(), "diagtest_" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(tempDir);
        Console.WriteLine($"Temp dir: {tempDir}");

        try
        {
            RunTest(sourcePath, tempDir);
        }
        finally
        {
            // Don't delete temp dir so we can examine files
            Console.WriteLine($"\nTemp files kept in: {tempDir}");
        }
    }

    static void RunTest(string sourcePath, string tempDir)
    {
        // Test 1: In-place patching (no heap growth)
        var inPlacePath = Path.Combine(tempDir, "InPlace.dll");
        Console.WriteLine("\n=== Test 1: In-place patching (MakeTypesInternal only) ===");
        using (var modifier = Alias.Lib.StreamingAssemblyModifier.Open(sourcePath))
        {
            modifier.MakeTypesInternal();
            modifier.Save(inPlacePath);
        }
        Console.WriteLine($"In-place modified file size: {new FileInfo(inPlacePath).Length}");
        TryLoad(inPlacePath);

        // Test 2: Metadata rebuild (with name change)
        var modifiedPath = Path.Combine(tempDir, "Modified.dll");
        Console.WriteLine("\n=== Test 2: SetAssemblyName (triggers metadata rebuild) ===");
        using (var modifier = Alias.Lib.StreamingAssemblyModifier.Open(sourcePath))
        {
            modifier.SetAssemblyName("ModifiedAssembly");
            modifier.MakeTypesInternal();
            modifier.Save(modifiedPath);
        }

        Console.WriteLine($"Modified file size: {new FileInfo(modifiedPath).Length}");
        Console.WriteLine($"Original file size: {new FileInfo(sourcePath).Length}");

        // Validate original
        Console.WriteLine("\n=== Original File Validation ===");
        ValidateWithPEReader(sourcePath);

        // Compare headers
        Console.WriteLine("\n=== Comparing PE Headers ===");
        ComparePEHeaders(sourcePath, modifiedPath);

        // Try to validate with PEReader/MetadataReader
        Console.WriteLine("\n=== PEReader Validation ===");
        ValidateWithPEReader(modifiedPath);

        // Try to load
        Console.WriteLine("\n=== Load Test ===");
        TryLoad(modifiedPath);
    }

    static void ComparePEHeaders(string original, string modified)
    {
        using var origStream = new FileStream(original, FileMode.Open, FileAccess.Read);
        using var modStream = new FileStream(modified, FileMode.Open, FileAccess.Read);

        using var origReader = new PEReader(origStream);
        using var modReader = new PEReader(modStream);

        var origPE = origReader.PEHeaders;
        var modPE = modReader.PEHeaders;
        var textSection = origPE.SectionHeaders.First(s => s.Name == ".text");

        Console.WriteLine($"Original SizeOfImage: 0x{origPE.PEHeader!.SizeOfImage:X}");
        Console.WriteLine($"Modified SizeOfImage: 0x{modPE.PEHeader!.SizeOfImage:X}");

        Console.WriteLine($"Original SizeOfHeaders: 0x{origPE.PEHeader.SizeOfHeaders:X}");
        Console.WriteLine($"Modified SizeOfHeaders: 0x{modPE.PEHeader.SizeOfHeaders:X}");

        Console.WriteLine($"Original Checksum: 0x{origPE.PEHeader.CheckSum:X}");
        Console.WriteLine($"Modified Checksum: 0x{modPE.PEHeader.CheckSum:X}");

        // CLI Header
        var origCli = origPE.CorHeader!;
        var modCli = modPE.CorHeader!;

        Console.WriteLine($"\nOriginal CLI Flags: 0x{origCli.Flags:X}");
        Console.WriteLine($"Modified CLI Flags: 0x{modCli.Flags:X}");

        Console.WriteLine($"\nOriginal Entry Point Token: 0x{origCli.EntryPointTokenOrRelativeVirtualAddress:X}");
        Console.WriteLine($"Modified Entry Point Token: 0x{modCli.EntryPointTokenOrRelativeVirtualAddress:X}");

        // Show all data directories - compare original vs modified
        Console.WriteLine("\n--- Data Directories Comparison (entries that should change) ---");
        Console.WriteLine("Dir 1 (Import): Orig RVA=0x{0:X}, Mod RVA=0x{1:X}",
            origPE.PEHeader!.ImportTableDirectory.RelativeVirtualAddress,
            modPE.PEHeader!.ImportTableDirectory.RelativeVirtualAddress);
        Console.WriteLine("Dir 6 (Debug):  Orig RVA=0x{0:X}, Mod RVA=0x{1:X}",
            origPE.PEHeader.DebugTableDirectory.RelativeVirtualAddress,
            modPE.PEHeader.DebugTableDirectory.RelativeVirtualAddress);

        Console.WriteLine($"\nOriginal MetadataRVA: 0x{origCli.MetadataDirectory.RelativeVirtualAddress:X}");
        Console.WriteLine($"Modified MetadataRVA: 0x{modCli.MetadataDirectory.RelativeVirtualAddress:X}");

        // Compare CLI header bytes (72 bytes at RVA 0x2008)
        Console.WriteLine("\n--- CLI Header Byte Comparison ---");
        var cliHeaderFileOffset = textSection.PointerToRawData + (0x2008 - textSection.VirtualAddress);
        origStream.Position = cliHeaderFileOffset;
        var origCliBytes = new byte[72];
        _ = origStream.Read(origCliBytes, 0, 72);
        modStream.Position = cliHeaderFileOffset;
        var modCliBytes = new byte[72];
        _ = modStream.Read(modCliBytes, 0, 72);

        Console.WriteLine($"CLI Header bytes match: {origCliBytes.SequenceEqual(modCliBytes)}");
        if (!origCliBytes.SequenceEqual(modCliBytes))
        {
            Console.WriteLine("Orig CLI:");
            for (int i = 0; i < 72; i += 16)
            {
                var line = origCliBytes.Skip(i).Take(16).ToArray();
                Console.WriteLine($"  {i:X2}: {BitConverter.ToString(line)}");
            }
            Console.WriteLine("Mod  CLI:");
            for (int i = 0; i < 72; i += 16)
            {
                var line = modCliBytes.Skip(i).Take(16).ToArray();
                Console.WriteLine($"  {i:X2}: {BitConverter.ToString(line)}");
            }
        }

        // Compare Import Directory bytes
        Console.WriteLine("\n--- Import Directory Comparison ---");
        var importDirOffset = textSection.PointerToRawData + (0x2502 - textSection.VirtualAddress);
        origStream.Position = importDirOffset;
        var origImport = new byte[40];
        _ = origStream.Read(origImport, 0, 40);
        // Modified import dir is at +0x14 due to shift
        modStream.Position = importDirOffset + 0x14;
        var modImport = new byte[40];
        _ = modStream.Read(modImport, 0, 40);
        Console.WriteLine($"Orig Import Dir (at 0x{importDirOffset:X}): {BitConverter.ToString(origImport.Take(20).ToArray())}");
        Console.WriteLine($"Mod  Import Dir (at 0x{importDirOffset + 0x14:X}): {BitConverter.ToString(modImport.Take(20).ToArray())}");

        // Compare IAT bytes (at RVA 0x2000, file offset 0x200)
        Console.WriteLine("\n--- IAT Comparison ---");
        origStream.Position = 0x200;
        var origIat = new byte[16];
        _ = origStream.Read(origIat, 0, 16);
        modStream.Position = 0x200;
        var modIat = new byte[16];
        _ = modStream.Read(modIat, 0, 16);
        Console.WriteLine($"Orig IAT: {BitConverter.ToString(origIat)}");
        Console.WriteLine($"Mod  IAT: {BitConverter.ToString(modIat)}");

        // Compare metadata header bytes
        Console.WriteLine("\n--- Metadata Header Comparison ---");
        var metadataFileOffset = textSection.PointerToRawData + (0x2090 - textSection.VirtualAddress);
        origStream.Position = metadataFileOffset;
        var origMeta = new byte[32];
        _ = origStream.Read(origMeta, 0, 32);
        modStream.Position = metadataFileOffset;
        var modMeta = new byte[32];
        _ = modStream.Read(modMeta, 0, 32);
        Console.WriteLine($"Orig Metadata: {BitConverter.ToString(origMeta)}");
        Console.WriteLine($"Mod  Metadata: {BitConverter.ToString(modMeta)}");

        Console.WriteLine($"Original MetadataSize: 0x{origCli.MetadataDirectory.Size:X}");
        Console.WriteLine($"Modified MetadataSize: 0x{modCli.MetadataDirectory.Size:X}");

        // Sections
        Console.WriteLine("\n--- Sections ---");
        foreach (var section in origPE.SectionHeaders)
        {
            Console.WriteLine($"Orig {section.Name}: VA=0x{section.VirtualAddress:X}, VSize=0x{section.VirtualSize:X}, RawPtr=0x{section.PointerToRawData:X}, RawSize=0x{section.SizeOfRawData:X}");
        }
        Console.WriteLine("---");
        foreach (var section in modPE.SectionHeaders)
        {
            Console.WriteLine($"Mod  {section.Name}: VA=0x{section.VirtualAddress:X}, VSize=0x{section.VirtualSize:X}, RawPtr=0x{section.PointerToRawData:X}, RawSize=0x{section.SizeOfRawData:X}");
        }

        // Compare bytes at key offsets
        Console.WriteLine("\n--- Raw Byte Comparison ---");

        // DOS Header
        origStream.Position = 0;
        modStream.Position = 0;
        var origDos = new byte[64];
        var modDos = new byte[64];
        _ = origStream.Read(origDos, 0, 64);
        _ = modStream.Read(modDos, 0, 64);
        Console.WriteLine($"DOS Header match: {origDos.SequenceEqual(modDos)}");

        // Compare method body bytes
        Console.WriteLine("\n--- Method Body Comparison ---");
        var method1FileOffset = textSection.PointerToRawData + (0x2050 - textSection.VirtualAddress);
        var method2FileOffset = textSection.PointerToRawData + (0x2087 - textSection.VirtualAddress);

        origStream.Position = method1FileOffset;
        var origMethod1 = new byte[55];
        _ = origStream.Read(origMethod1, 0, 55);

        modStream.Position = method1FileOffset;
        var modMethod1 = new byte[55];
        _ = modStream.Read(modMethod1, 0, 55);

        Console.WriteLine($"Method1 bytes match: {origMethod1.SequenceEqual(modMethod1)}");
        if (!origMethod1.SequenceEqual(modMethod1))
        {
            Console.WriteLine($"Method1 orig: {BitConverter.ToString(origMethod1[..16])}...");
            Console.WriteLine($"Method1 mod:  {BitConverter.ToString(modMethod1[..16])}...");
        }

        origStream.Position = method2FileOffset;
        var origMethod2 = new byte[9];
        _ = origStream.Read(origMethod2, 0, 9);

        modStream.Position = method2FileOffset;
        var modMethod2 = new byte[9];
        _ = modStream.Read(modMethod2, 0, 9);

        Console.WriteLine($"Method2 bytes match: {origMethod2.SequenceEqual(modMethod2)}");
        if (!origMethod2.SequenceEqual(modMethod2))
        {
            Console.WriteLine($"Method2 orig: {BitConverter.ToString(origMethod2)}");
            Console.WriteLine($"Method2 mod:  {BitConverter.ToString(modMethod2)}");
        }

        // Check entry point stub bytes
        var origEntryRva = origPE.PEHeader.AddressOfEntryPoint;
        var entryPointFileOffset = textSection.PointerToRawData + (origEntryRva - textSection.VirtualAddress);
        Console.WriteLine($"\nOriginal entry point RVA: 0x{origEntryRva:X}, file offset: 0x{entryPointFileOffset:X}");

        origStream.Position = entryPointFileOffset;
        var origEntryBytes = new byte[16];
        _ = origStream.Read(origEntryBytes, 0, 16);
        Console.WriteLine($"Orig entry stub: {BitConverter.ToString(origEntryBytes)}");

        var modEntryRva = modPE.PEHeader.AddressOfEntryPoint;
        var modTextSection = modPE.SectionHeaders.First(s => s.Name == ".text");
        // Need to account for shift - if entry point moved, need to find new location
        var sizeDiff = modPE.CorHeader!.MetadataDirectory.Size - origPE.CorHeader!.MetadataDirectory.Size;
        var modEntryFileOffset = modTextSection.PointerToRawData + (modEntryRva - modTextSection.VirtualAddress);
        Console.WriteLine($"Modified entry point RVA: 0x{modEntryRva:X}, file offset: 0x{modEntryFileOffset:X}");

        modStream.Position = modEntryFileOffset;
        var modEntryBytes = new byte[16];
        _ = modStream.Read(modEntryBytes, 0, 16);
        Console.WriteLine($"Mod  entry stub: {BitConverter.ToString(modEntryBytes)}");

        // Decode the JMP instruction if it's FF 25
        if (origEntryBytes[0] == 0xFF && origEntryBytes[1] == 0x25)
        {
            var origDisp = BitConverter.ToInt32(origEntryBytes, 2);
            var origRip = origEntryRva + 6;
            var origTarget = origRip + origDisp;
            Console.WriteLine($"Orig JMP target: RIP(0x{origRip:X}) + disp(0x{origDisp:X}) = 0x{origTarget:X}");
        }

        if (modEntryBytes[0] == 0xFF && modEntryBytes[1] == 0x25)
        {
            var modDisp = BitConverter.ToInt32(modEntryBytes, 2);
            var modRip = modEntryRva + 6;
            var modTarget = modRip + modDisp;
            Console.WriteLine($"Mod  JMP target: RIP(0x{modRip:X}) + disp(0x{modDisp:X}) = 0x{modTarget:X}");
        }

        // Check .reloc section content
        Console.WriteLine("\n--- .reloc Section Analysis ---");
        var relocSection = origPE.SectionHeaders.FirstOrDefault(s => s.Name == ".reloc");
        if (relocSection.Name == ".reloc")
        {
            origStream.Position = relocSection.PointerToRawData;
            var relocBytes = new byte[Math.Min(64, relocSection.SizeOfRawData)];
            _ = origStream.Read(relocBytes, 0, relocBytes.Length);
            Console.WriteLine($"Orig .reloc: {BitConverter.ToString(relocBytes)}");

            var modRelocSection = modPE.SectionHeaders.First(s => s.Name == ".reloc");
            modStream.Position = modRelocSection.PointerToRawData;
            var modRelocBytes = new byte[Math.Min(64, modRelocSection.SizeOfRawData)];
            _ = modStream.Read(modRelocBytes, 0, modRelocBytes.Length);
            Console.WriteLine($"Mod  .reloc: {BitConverter.ToString(modRelocBytes)}");

            // Parse first relocation block
            var pageRva = BitConverter.ToUInt32(relocBytes, 0);
            var blockSize = BitConverter.ToUInt32(relocBytes, 4);
            Console.WriteLine($"Reloc block: PageRVA=0x{pageRva:X}, BlockSize={blockSize}");
            if (blockSize > 8)
            {
                var entryCount = (blockSize - 8) / 2;
                for (int i = 0; i < entryCount && i < 10; i++)
                {
                    var entry = BitConverter.ToUInt16(relocBytes, 8 + i * 2);
                    var type = entry >> 12;
                    var offset = entry & 0xFFF;
                    var targetRva = pageRva + offset;
                    Console.WriteLine($"  Reloc[{i}]: type={type}, offset=0x{offset:X}, targetRVA=0x{targetRva:X}");
                }
            }
        }

        // Check ILT (Import Lookup Table) at the original and modified locations
        Console.WriteLine("\n--- ILT Comparison ---");
        var iltOrigOffset = textSection.PointerToRawData + (0x252A - textSection.VirtualAddress);
        var iltModOffset = textSection.PointerToRawData + (0x253E - textSection.VirtualAddress);
        origStream.Position = iltOrigOffset;
        var origIlt = new byte[8];
        _ = origStream.Read(origIlt, 0, 8);
        modStream.Position = iltModOffset;
        var modIlt = new byte[8];
        _ = modStream.Read(modIlt, 0, 8);
        Console.WriteLine($"Orig ILT at 0x{iltOrigOffset:X} (RVA 0x252A): {BitConverter.ToString(origIlt)}");
        Console.WriteLine($"Mod  ILT at 0x{iltModOffset:X} (RVA 0x253E): {BitConverter.ToString(modIlt)}");

        // Check if hint/name strings are at correct locations
        Console.WriteLine("\n--- Hint/Name String Comparison ---");
        var hintOrigRva = BitConverter.ToUInt32(origIlt, 0) & 0x7FFFFFFF;
        var hintModRva = BitConverter.ToUInt32(modIlt, 0) & 0x7FFFFFFF;
        var hintOrigOffset = textSection.PointerToRawData + (hintOrigRva - textSection.VirtualAddress);
        var hintModOffset = textSection.PointerToRawData + (hintModRva - textSection.VirtualAddress);
        origStream.Position = hintOrigOffset;
        var origHint = new byte[32];
        _ = origStream.Read(origHint, 0, 32);
        modStream.Position = hintModOffset;
        var modHint = new byte[32];
        _ = modStream.Read(modHint, 0, 32);
        Console.WriteLine($"Orig Hint/Name at RVA 0x{hintOrigRva:X}: {System.Text.Encoding.ASCII.GetString(origHint.Skip(2).TakeWhile(b => b != 0).ToArray())}");
        Console.WriteLine($"Mod  Hint/Name at RVA 0x{hintModRva:X}: {System.Text.Encoding.ASCII.GetString(modHint.Skip(2).TakeWhile(b => b != 0).ToArray())}");

        // Find first difference
        origStream.Position = 0;
        modStream.Position = 0;
        int diffCount = 0;
        long pos = 0;
        while (diffCount < 10)
        {
            int origByte = origStream.ReadByte();
            int modByte = modStream.ReadByte();
            if (origByte == -1 || modByte == -1)
            {
                if (origByte != modByte)
                    Console.WriteLine($"File length differs at 0x{pos:X}");
                break;
            }
            if (origByte != modByte)
            {
                Console.WriteLine($"Diff at 0x{pos:X}: orig=0x{origByte:X2} mod=0x{modByte:X2}");
                diffCount++;
            }
            pos++;
        }
    }

    static void ValidateWithPEReader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var peReader = new PEReader(stream);

            var metadata = peReader.GetMetadataReader();

            Console.WriteLine($"Assembly name: {metadata.GetString(metadata.GetAssemblyDefinition().Name)}");
            Console.WriteLine($"TypeDef count: {metadata.TypeDefinitions.Count}");

            // Check method body RVAs
            Console.WriteLine("\nMethod body RVAs:");
            foreach (var methodHandle in metadata.MethodDefinitions)
            {
                var methodDef = metadata.GetMethodDefinition(methodHandle);
                var name = metadata.GetString(methodDef.Name);
                var rva = methodDef.RelativeVirtualAddress;

                if (rva > 0)
                {
                    try
                    {
                        var body = peReader.GetMethodBody(rva);
                        Console.WriteLine($"  {name}: RVA=0x{rva:X}, Size={body.Size}, MaxStack={body.MaxStack}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {name}: RVA=0x{rva:X}, ERROR: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"  {name}: abstract/extern (RVA=0)");
                }
            }

            Console.WriteLine("MetadataReader validation: PASSED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MetadataReader validation: FAILED - {ex.Message}");
        }
    }

    static void TryLoad(string path)
    {
        var loadContext = new AssemblyLoadContext("DiagTest", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var assembly = loadContext.LoadFromStream(stream);
            Console.WriteLine($"Load: SUCCESS - {assembly.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load: FAILED - {ex.GetType().Name}: {ex.Message}");

            // Try to get more details
            if (ex is BadImageFormatException bife && !string.IsNullOrEmpty(bife.FusionLog))
            {
                Console.WriteLine($"Fusion log: {bife.FusionLog}");
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }
}
