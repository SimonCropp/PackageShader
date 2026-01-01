using Alias.Lib;
using Alias.Lib.Signing;

namespace Alias;

public static class Aliaser
{
    public static void Run(
        IEnumerable<string> references,
        IEnumerable<SourceTargetInfo> infos,
        bool internalize,
        StrongNameKey? key)
    {
        var infoList = infos.ToList();

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
            if (info.IsAlias && internalize)
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

    /// <summary>
    /// Overload that accepts a StrongNameKeyPair path for backwards compatibility.
    /// </summary>
    public static void Run(
        IEnumerable<string> references,
        IEnumerable<SourceTargetInfo> infos,
        bool internalize,
        string? keyPath)
    {
        StrongNameKey? key = null;
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            key = StrongNameKey.FromFile(keyPath!);
        }

        Run(references, infos, internalize, key);
    }
}
