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

    internal static void ValidateConfiguration(List<SourceTargetInfo> infos)
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

        // Analyze reachability and cache AssemblyRef lists in a single pass so
        // the validation loop below doesn't have to re-open every assembly.
        var (reachableFromRoot, refsBySourceName) = AnalyzeReachability(infos);

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

            if (!refsBySourceName.TryGetValue(info.SourceName, out var refs))
            {
                // File didn't exist during analysis — skip (matches the original File.Exists branch)
                continue;
            }

            var problematicRefs = new List<string>();
            foreach (var refName in refs)
            {
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

    internal static HashSet<string> GetAssembliesReachableFromRoot(List<SourceTargetInfo> infos) =>
        AnalyzeReachability(infos).Reachable;

    static (HashSet<string> Reachable, Dictionary<string, List<string>> RefsBySourceName) AnalyzeReachability(List<SourceTargetInfo> infos)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refsBySourceName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rootInfo = infos.FirstOrDefault(_ => _.IsRootAssembly);

        if (rootInfo == null)
        {
            // No root assembly - consider all assemblies reachable (conservative fallback).
            // Read refs for each so validation can check without re-opening.
            foreach (var info in infos)
            {
                reachable.Add(info.SourceName);
            }

            foreach (var info in infos)
            {
                if (refsBySourceName.ContainsKey(info.SourceName) ||
                    !File.Exists(info.SourcePath))
                {
                    continue;
                }

                using var peFile = StreamingPEFile.Open(info.SourcePath);
                using var reader = new StreamingMetadataReader(peFile);
                refsBySourceName[info.SourceName] = ReadAssemblyRefs(reader);
            }

            return (reachable, refsBySourceName);
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

            // Get references from current assembly and cache them for validation
            using var peFile = StreamingPEFile.Open(current.SourcePath);
            using var reader = new StreamingMetadataReader(peFile);

            var refs = ReadAssemblyRefs(reader);
            refsBySourceName[current.SourceName] = refs;

            foreach (var refName in refs)
            {
                if (!reachable.Add(refName) || !infoByName.TryGetValue(refName, out var refInfo))
                {
                    continue;
                }

                toProcess.Enqueue(refInfo);
            }
        }

        return (reachable, refsBySourceName);
    }

    static List<string> ReadAssemblyRefs(StreamingMetadataReader reader)
    {
        var refs = new List<string>();
        var refCount = reader.GetRowCount(TableIndex.AssemblyRef);
        for (uint rid = 1; rid <= refCount; rid++)
        {
            var found = reader.FindAssemblyRefByRid(rid);
            if (found == null)
            {
                continue;
            }

            refs.Add(found.Value.name);
        }

        return refs;
    }
}
