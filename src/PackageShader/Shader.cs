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
            if (key == null)
            {
                modifier.ClearStrongName();
            }
            else
            {
                modifier.SetAssemblyPublicKey(key.PublicKey);
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
            infos
                .Where(_ => _.IsShaded)
                .Select(_ => _.SourceName),
            StringComparer.OrdinalIgnoreCase);

        if (shadedNames.Count == 0)
        {
            // No shaded assemblies, nothing to validate
            return;
        }

        // Build set of assemblies reachable from root (these are the only ones that matter)
        var reachableFromRoot = GetAssembliesReachableFromRoot(infos);

        // Check each non-root, unshaded assembly for references to shaded assemblies
        foreach (var info in infos)
        {
            // Skip if this is shaded (will be renamed) or root (allowed to reference shaded deps)
            if (info.IsShaded || info.IsRootAssembly)
            {
                continue;
            }

            // Skip if not reachable from root assembly - these are "stray" dependencies
            // (e.g., from build tools with PrivateAssets="all") that won't affect runtime
            if (!reachableFromRoot.Contains(info.SourceName))
            {
                continue;
            }

            if (!File.Exists(info.SourcePath))
            {
                // Skip if file doesn't exist
                continue;
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
                if (found == null)
                {
                    continue;
                }

                var refName = found.Value.name;

                // Check if this unshaded assembly references a shaded assembly
                if (shadedNames.Contains(refName))
                {
                    problematicRefs.Add(refName);
                }
            }

            if (problematicRefs.Count <= 0)
            {
                continue;
            }

            var refList = string.Join(", ", problematicRefs);
            throw new InvalidOperationException(
                $"""
                 Invalid shading configuration: Assembly '{info.SourceName}' references {problematicRefs.Count} assembly(ies) that are being shaded: {refList}.
                 This will create broken references in the output.
                 Solution: Either add '{info.SourceName}' to the list of assemblies to shade, or remove {refList} from the list of assemblies to shade.
                 """);
        }
    }

    static HashSet<string> GetAssembliesReachableFromRoot(List<SourceTargetInfo> infos)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootInfo = infos.FirstOrDefault(_ => _.IsRootAssembly);

        if (rootInfo == null)
        {
            // No root assembly - consider all assemblies reachable (conservative)
            return new(
                infos.Select(_ => _.SourceName),
                StringComparer.OrdinalIgnoreCase);
        }

        // Group by name to handle duplicates (e.g., IntermediateAssembly may also be in ReferenceCopyLocalPaths)
        var infoByName = infos
            .GroupBy(_ => _.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(_ => _.Key, _ => _.First(), StringComparer.OrdinalIgnoreCase);
        var toProcess = new Queue<SourceTargetInfo>();
        toProcess.Enqueue(rootInfo);
        reachable.Add(rootInfo.SourceName);

        while (toProcess.Count > 0)
        {
            var current = toProcess.Dequeue();

            if (!File.Exists(current.SourcePath))
            {
                continue;
            }

            // Get references from current assembly
            using var peFile = StreamingPEFile.Open(current.SourcePath);
            using var reader = new StreamingMetadataReader(peFile);

            var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
            for (uint rid = 1; rid <= refCount; rid++)
            {
                var found = reader.FindAssemblyRefByRid(rid);
                if (found == null)
                {
                    continue;
                }

                var refName = found.Value.name;
                if (!reachable.Add(refName) || !infoByName.TryGetValue(refName, out var refInfo))
                {
                    continue;
                }

                toProcess.Enqueue(refInfo);
            }
        }

        return reachable;
    }
}
