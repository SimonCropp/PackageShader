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

            if (ShadedNameMappings == null || ShadedNameMappings.Length == 0)
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
                                        // rt.Key is like "lib/netstandard2.0/Newtonsoft.Json.dll"
                                        if (rt.Key.IndexOf(libName, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            runtimeToRemove.Add(rt.Key);
                                            var newRtKey = ReplaceIgnoreCase(rt.Key, libName, shadedName);
                                            runtimeToAdd[newRtKey] = rt.Value?.DeepClone();
                                        }
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

    static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
    {
        var index = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return input;
        return input.Substring(0, index) + newValue + input.Substring(index + oldValue.Length);
    }
}
