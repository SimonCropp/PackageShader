using System.IO.Compression;
using System.Xml.Linq;
using Task = Microsoft.Build.Utilities.Task;

namespace PackageShader;

/// <summary>
/// Patches the nupkg file to remove dependencies for shaded packages.
/// This runs after Pack to automatically exclude shaded packages from NuGet dependencies.
/// </summary>
public class NupkgPatcherTask : Task
{
    [Required]
    public string NupkgPath { get; set; } = null!;

    /// <summary>
    /// List of shaded package names to remove from dependencies.
    /// </summary>
    [Required]
    public ITaskItem[] ShadedPackageNames { get; set; } = null!;

    public override bool Execute()
    {
        try
        {
            if (!File.Exists(NupkgPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"NupkgPatcher: nupkg not found at {NupkgPath}, skipping");
                return true;
            }

            if (ShadedPackageNames.Length == 0)
            {
                Log.LogMessage(MessageImportance.Normal, "NupkgPatcher: No shaded package names provided, skipping");
                return true;
            }

            // Build set of shaded package names (case-insensitive)
            var shadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ShadedPackageNames)
            {
                shadedNames.Add(item.ItemSpec);
                Log.LogMessage(MessageImportance.Normal, $"NupkgPatcher: Will remove dependency on {item.ItemSpec}");
            }

            // Open the nupkg (which is a zip file)
            using var archive = ZipFile.Open(NupkgPath, ZipArchiveMode.Update);

            // Find the nuspec entry
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry == null)
            {
                Log.LogWarning($"NupkgPatcher: No nuspec found in {NupkgPath}");
                return true;
            }

            // Read and parse the nuspec
            XDocument doc;
            using (var stream = nuspecEntry.Open())
            {
                doc = XDocument.Load(stream);
            }

            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var modified = false;
            var dependenciesToRemove = new List<XElement>();

            // Find all <dependency> elements
            var dependencies = doc.Descendants(ns + "dependency");
            foreach (var dep in dependencies)
            {
                var id = dep.Attribute("id")?.Value;
                if (id != null && shadedNames.Contains(id))
                {
                    dependenciesToRemove.Add(dep);
                    Log.LogMessage(MessageImportance.Normal, $"NupkgPatcher: Removing dependency on {id}");
                    modified = true;
                }
            }

            if (!modified)
            {
                Log.LogMessage(MessageImportance.Normal, "NupkgPatcher: No shaded dependencies found to remove");
                return true;
            }

            // Remove the dependencies
            foreach (var dep in dependenciesToRemove)
            {
                dep.Remove();
            }

            // Clean up empty <group> elements
            var emptyGroups = doc.Descendants(ns + "group")
                .Where(g => !g.Elements().Any())
                .ToList();
            foreach (var group in emptyGroups)
            {
                group.Remove();
            }

            // Clean up empty <dependencies> elements
            var emptyDeps = doc.Descendants(ns + "dependencies")
                .Where(d => !d.Elements().Any())
                .ToList();
            foreach (var deps in emptyDeps)
            {
                deps.Remove();
            }

            // Write the modified nuspec back to the archive
            // Delete the old entry and create a new one
            var entryName = nuspecEntry.FullName;
            nuspecEntry.Delete();

            var newEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = newEntry.Open())
            {
                doc.Save(stream);
            }

            Log.LogMessage(MessageImportance.High, $"NupkgPatcher: Removed {dependenciesToRemove.Count} shaded dependencies from {NupkgPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"NupkgPatcher: {ex.Message}");
            return false;
        }
    }
}
