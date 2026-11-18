using System.Text.Json;
using System.Text.Json.Nodes;
using Task = Microsoft.Build.Utilities.Task;

namespace PackageShader;

/// <summary>
/// Patches the deps.json file to update assembly references from original names to shaded names.
/// </summary>
public class DepsJsonPatcherTask : Task
{
    [Required]
    public string DepsJsonPath { get; set; } = null!;

    /// <summary>
    /// Mappings from original assembly names to shaded names.
    /// ItemSpec = shaded name (e.g., "MyApp.Newtonsoft.Json")
    /// OriginalName metadata = original name (e.g., "Newtonsoft.Json")
    /// </summary>
    [Required]
    public ITaskItem[] ShadedNameMappings { get; set; } = null!;

    public override bool Execute()
    {
        try
        {
            if (!File.Exists(DepsJsonPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"DepsJsonPatcher: deps.json not found at {DepsJsonPath}, skipping");
                return true;
            }

            if (ShadedNameMappings.Length == 0)
            {
                Log.LogMessage(MessageImportance.Normal, "DepsJsonPatcher: No shaded mappings provided, skipping");
                return true;
            }

            // Build mapping dictionary: original name -> shaded name
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ShadedNameMappings)
            {
                var shadedName = item.ItemSpec;
                var originalName = item.GetMetadata("OriginalName");
                if (!string.IsNullOrEmpty(originalName))
                {
                    mappings[originalName] = shadedName;
                    Log.LogMessage(MessageImportance.Normal, $"DepsJsonPatcher: Mapping {originalName} -> {shadedName}");
                }
            }

            var json = File.ReadAllText(DepsJsonPath);
            var doc = JsonNode.Parse(json);
            if (doc == null)
            {
                Log.LogWarning("DepsJsonPatcher: Could not parse deps.json");
                return true;
            }

            var modified = false;

            // Patch the "targets" section
            var targets = doc["targets"]?.AsObject();
            if (targets != null)
            {
                foreach (var tfm in targets)
                {
                    var tfmLibraries = tfm.Value?.AsObject();
                    if (tfmLibraries == null) continue;

                    var librariesToRemove = new List<string>();
                    var librariesToAdd = new Dictionary<string, JsonNode?>();

                    foreach (var lib in tfmLibraries)
                    {
                        // lib.Key is like "Newtonsoft.Json/13.0.4"
                        var slashIndex = lib.Key.IndexOf('/');
                        var libName = slashIndex > 0 ? lib.Key.Substring(0, slashIndex) : lib.Key;

                        if (mappings.TryGetValue(libName, out var shadedName))
                        {
                            librariesToRemove.Add(lib.Key);
                            var version = slashIndex > 0 ? lib.Key.Substring(slashIndex + 1) : "1.0.0";
                            var newKey = $"{shadedName}/{version}";

                            // Clone the library entry but update runtime assembly names
                            var libNode = lib.Value?.DeepClone();
                            if (libNode != null)
                            {
                                var runtime = libNode["runtime"]?.AsObject();
                                if (runtime != null)
                                {
                                    var runtimeToRemove = new List<string>();
                                    var runtimeToAdd = new Dictionary<string, JsonNode?>();

                                    foreach (var rt in runtime)
                                    {
                                        // rt.Key is like "lib/netstandard2.0/Newtonsoft.Json.dll".
                                        // We only rename when libName appears as a filename stem
                                        // (preceded by '/' and followed by '.'), so that libName
                                        // "Foo" does not match inside "FooBar.dll" or "Newtonsoft.Foo.dll".
                                        var newRtKey = TryReplaceLibNameAtFilenameBoundary(rt.Key, libName, shadedName);
                                        if (newRtKey == null)
                                        {
                                            continue;
                                        }

                                        runtimeToRemove.Add(rt.Key);
                                        var asset = rt.Value?.DeepClone();
                                        RenameLocalPath(asset, libName, shadedName);
                                        runtimeToAdd[newRtKey] = asset;
                                    }

                                    foreach (var key in runtimeToRemove)
                                        runtime.Remove(key);
                                    foreach (var kv in runtimeToAdd)
                                        runtime[kv.Key] = kv.Value;
                                }

                                librariesToAdd[newKey] = libNode;
                            }

                            modified = true;
                        }
                    }

                    foreach (var key in librariesToRemove)
                        tfmLibraries.Remove(key);
                    foreach (var kv in librariesToAdd)
                        tfmLibraries[kv.Key] = kv.Value;

                    // A library that depends on a shaded one names it in its dependencies, and
                    // the shaded library is now listed under its new name.
                    foreach (var lib in tfmLibraries)
                    {
                        modified |= RenameDependencies(lib.Value?["dependencies"]?.AsObject(), mappings);
                    }
                }
            }

            // Patch the "libraries" section
            var libraries = doc["libraries"]?.AsObject();
            if (libraries != null)
            {
                var librariesToRemove = new List<string>();
                var librariesToAdd = new Dictionary<string, JsonNode?>();

                foreach (var lib in libraries)
                {
                    var slashIndex = lib.Key.IndexOf('/');
                    var libName = slashIndex > 0 ? lib.Key.Substring(0, slashIndex) : lib.Key;

                    if (mappings.TryGetValue(libName, out var shadedName))
                    {
                        librariesToRemove.Add(lib.Key);
                        var version = slashIndex > 0 ? lib.Key.Substring(slashIndex + 1) : "1.0.0";
                        var newKey = $"{shadedName}/{version}";
                        librariesToAdd[newKey] = lib.Value?.DeepClone();
                        modified = true;
                    }
                }

                foreach (var key in librariesToRemove)
                    libraries.Remove(key);
                foreach (var kv in librariesToAdd)
                    libraries[kv.Key] = kv.Value;
            }

            if (modified)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = doc.ToJsonString(options);
                File.WriteAllText(DepsJsonPath, updatedJson);
                Log.LogMessage(MessageImportance.Normal, $"DepsJsonPatcher: Updated {DepsJsonPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"DepsJsonPatcher: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Renames the file an asset's <c>localPath</c> names. Since .NET 9 the host finds an
    /// app-local asset by its <c>localPath</c>, so one still naming the original file points at a
    /// file that shading renamed, and the shaded assembly fails to load.
    /// </summary>
    static void RenameLocalPath(JsonNode? asset, string libName, string shadedName)
    {
        if (asset is not JsonObject assetObject ||
            assetObject["localPath"] is not JsonValue localPathValue ||
            !localPathValue.TryGetValue<string>(out var localPath))
        {
            return;
        }

        var renamed = TryReplaceLibNameAtFilenameBoundary(localPath, libName, shadedName);
        if (renamed != null)
        {
            assetObject["localPath"] = renamed;
        }
    }

    /// <summary>
    /// Renames the shaded libraries a library's <c>dependencies</c> name, keeping their versions.
    /// </summary>
    static bool RenameDependencies(JsonObject? dependencies, Dictionary<string, string> mappings)
    {
        if (dependencies == null)
        {
            return false;
        }

        var renames = new List<(string From, string To)>();
        foreach (var dependency in dependencies)
        {
            if (mappings.TryGetValue(dependency.Key, out var shadedName))
            {
                renames.Add((dependency.Key, shadedName));
            }
        }

        foreach (var (from, to) in renames)
        {
            var version = dependencies[from]?.DeepClone();
            dependencies.Remove(from);
            dependencies[to] = version;
        }

        return renames.Count > 0;
    }

    /// <summary>
    /// Replaces <paramref name="libName"/> inside a deps.json runtime key when it
    /// appears as a filename stem (preceded by '/' or start, followed by '.'). Returns
    /// the rewritten key, or null if no bounded match is found. This prevents spurious
    /// substring matches like libName "Foo" matching inside "FooBar.dll".
    /// </summary>
    internal static string? TryReplaceLibNameAtFilenameBoundary(string key, string libName, string replacement)
    {
        var searchStart = 0;
        while (searchStart <= key.Length - libName.Length)
        {
            var index = key.IndexOf(libName, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var beforeIsBoundary = index == 0 || key[index - 1] == '/';
            var afterPos = index + libName.Length;
            var afterIsBoundary = afterPos < key.Length && key[afterPos] == '.';

            if (beforeIsBoundary && afterIsBoundary)
            {
                return key.Substring(0, index) + replacement + key.Substring(afterPos);
            }

            searchStart = index + 1;
        }

        return null;
    }
}
