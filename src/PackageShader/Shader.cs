namespace PackageShader;

public static class Shader
{
    public static void Run(
        IEnumerable<SourceTargetInfo> infos,
        bool internalize,
        StrongNameKey? key)
    {
        var infoList = infos.ToList();

        // Validate configuration before processing
        ValidateConfiguration(infoList);

        // Process each assembly using streaming modifier (memory efficient)
        foreach (var info in infoList)
        {
            using var modifier = StreamingAssemblyModifier.Open(info.SourcePath);

            // Rename assembly
            modifier.SetAssemblyName(info.TargetName);

            // Set or clear strong name
            if (key != null)
            {
                modifier.SetAssemblyPublicKey(key.PublicKey);
            }
            else
            {
                modifier.ClearStrongName();
            }

            // If this is an aliased assembly and internalize is enabled
            if (info.IsShaded && internalize)
            {
                // Add InternalsVisibleTo for all other assemblies in the list
                foreach (var otherInfo in infoList)
                {
                    if (otherInfo.TargetName != info.TargetName)
                    {
                        modifier.AddInternalsVisibleTo(otherInfo.TargetName, key?.PublicKey);
                    }
                }

                // Make types internal
                modifier.MakeTypesInternal();
            }

            // Redirect assembly references
            foreach (var refInfo in infoList)
            {
                modifier.RedirectAssemblyRef(refInfo.SourceName, refInfo.TargetName, key?.PublicKeyToken);
            }

            modifier.Save(info.TargetPath, key);
        }
    }

    static void ValidateConfiguration(List<SourceTargetInfo> infos)
    {
        // Build set of shaded assembly names
        var shadedNames = new HashSet<string>(
            infos.Where(_ => _.IsShaded).Select(_ => _.SourceName),
            StringComparer.OrdinalIgnoreCase);

        if (shadedNames.Count == 0)
        {
            return; // No shaded assemblies, nothing to validate
        }

        // Check each non-root, unshaded assembly for references to shaded assemblies
        foreach (var info in infos)
        {
            // Skip if this is shaded (will be renamed) or root (allowed to reference shaded deps)
            if (info.IsShaded || info.IsRootAssembly)
            {
                continue;
            }

            if (!File.Exists(info.SourcePath))
            {
                continue; // Skip if file doesn't exist
            }

            // Read assembly references
            using var peFile = StreamingPEFile.Open(info.SourcePath);
            using var reader = new StreamingMetadataReader(peFile);

            var problematicRefs = new List<string>();

            // Check each assembly reference
            var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
            for (uint rid = 1; rid <= refCount; rid++)
            {
                var found = reader.FindAssemblyRefByRid(rid);
                if (found != null)
                {
                    var refName = found.Value.name;

                    // Check if this unshaded assembly references a shaded assembly
                    if (shadedNames.Contains(refName))
                    {
                        problematicRefs.Add(refName);
                    }
                }
            }

            if (problematicRefs.Count > 0)
            {
                var refList = string.Join(", ", problematicRefs);
                throw new InvalidOperationException(
                    $"Invalid shading configuration detected: Assembly '{info.SourceName}' references {problematicRefs.Count} assembly(ies) " +
                    $"that are being shaded: {refList}. " +
                    $"This will create broken references in the output. " +
                    $"Solution: Either add '{info.SourceName}' to the list of assemblies to shade, " +
                    $"or remove {refList} from the list of assemblies to shade.");
            }
        }
    }
}
