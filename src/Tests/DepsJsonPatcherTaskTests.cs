using System.Text.Json.Nodes;
using Microsoft.Build.Utilities;

public class DepsJsonPatcherTaskTests
{
    // -------------------------------------------------------------------------
    // Positive cases — libName appears at a filename-stem boundary and is replaced
    // -------------------------------------------------------------------------

    [Fact]
    public void TryReplace_LibNameIsFilenameStem_Replaces()
    {
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/netstandard2.0/Newtonsoft.Json.dll",
            "Newtonsoft.Json",
            "MyApp.Newtonsoft.Json");

        Assert.Equal("lib/netstandard2.0/MyApp.Newtonsoft.Json.dll", result);
    }

    [Fact]
    public void TryReplace_XmlExtension_Replaces()
    {
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/netstandard2.0/Newtonsoft.Json.xml",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Equal("lib/netstandard2.0/Shaded.xml", result);
    }

    [Fact]
    public void TryReplace_SatelliteAssembly_Replaces()
    {
        // de/Newtonsoft.Json.resources.dll — libName "Newtonsoft.Json" followed by ".resources.dll"
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/netstandard2.0/de/Newtonsoft.Json.resources.dll",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Equal("lib/netstandard2.0/de/Shaded.resources.dll", result);
    }

    [Fact]
    public void TryReplace_CaseInsensitiveMatch_Replaces()
    {
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/net8.0/NEWTONSOFT.JSON.dll",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Equal("lib/net8.0/Shaded.dll", result);
    }

    [Fact]
    public void TryReplace_FilenameOnly_Replaces()
    {
        // No leading path — libName is at the very start of the key
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "Newtonsoft.Json.dll",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Equal("Shaded.dll", result);
    }

    // -------------------------------------------------------------------------
    // Negative cases — libName appears as a substring but NOT at a stem boundary
    // -------------------------------------------------------------------------

    [Fact]
    public void TryReplace_LibNameIsPrefix_DoesNotMatch()
    {
        // libName "Foo" should NOT match inside "FooBar.dll"
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/net8.0/FooBar.dll",
            "Foo",
            "Shaded");

        Assert.Null(result);
    }

    [Fact]
    public void TryReplace_LibNameIsSuffixOfStem_DoesNotMatch()
    {
        // libName "Json" should NOT match inside "Newtonsoft.Json.dll" because it's
        // preceded by '.', not '/' or start. (The followed-by-'.' boundary IS satisfied.)
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/net8.0/Newtonsoft.Json.dll",
            "Json",
            "Shaded");

        Assert.Null(result);
    }

    [Fact]
    public void TryReplace_LibNameInDirectoryName_DoesNotMatch()
    {
        // libName "Foo" appears at a path-segment boundary (preceded by '/', followed by '/'),
        // but it is NOT a filename stem because the follow char is '/' not '.'. Rejected.
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/Foo/Bar.dll",
            "Foo",
            "Shaded");

        Assert.Null(result);
    }

    [Fact]
    public void TryReplace_LibNameAsInfix_DoesNotMatch()
    {
        // libName "Json" appears mid-word in "Fxjsondll" (artificial)
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/net8.0/MyJsonWrapper.dll",
            "Json",
            "Shaded");

        Assert.Null(result);
    }

    [Fact]
    public void TryReplace_LibNameNotPresent_DoesNotMatch()
    {
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/net8.0/CompletelyDifferent.dll",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Null(result);
    }

    [Fact]
    public void TryReplace_LibNameLongerThanKey_DoesNotMatch()
    {
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "x.dll",
            "Newtonsoft.Json",
            "Shaded");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Multiple-candidate case — skip spurious match and find the real boundary match
    // -------------------------------------------------------------------------

    [Fact]
    public void TryReplace_SpuriousPrefixThenRealMatch_ReplacesOnlyRealMatch()
    {
        // "Foo" appears inside "FooBar" (not a boundary) AND as a filename stem at the end.
        // Should replace only the real match.
        var result = DepsJsonPatcherTask.TryReplaceLibNameAtFilenameBoundary(
            "lib/FooBar.other/Foo.dll",
            "Foo",
            "Shaded");

        Assert.Equal("lib/FooBar.other/Shaded.dll", result);
    }

    // -------------------------------------------------------------------------
    // Execute — what the host reads to find the shaded assembly
    // -------------------------------------------------------------------------

    const string appDepsJson =
        """
        {
          "targets": {
            ".NETCoreApp,Version=v11.0": {
              "App/1.0.0": {
                "dependencies": {
                  "Newtonsoft.Json": "13.0.4"
                },
                "runtime": {
                  "App.dll": {}
                }
              },
              "Newtonsoft.Json/13.0.4": {
                "runtime": {
                  "lib/net6.0/Newtonsoft.Json.dll": {
                    "assemblyVersion": "13.0.0.0",
                    "localPath": "Newtonsoft.Json.dll"
                  }
                }
              }
            }
          },
          "libraries": {
            "App/1.0.0": {
              "type": "project"
            },
            "Newtonsoft.Json/13.0.4": {
              "type": "package",
              "path": "newtonsoft.json/13.0.4"
            }
          }
        }
        """;

    // Since .NET 9 the host finds an app-local asset by its localPath. Left naming the original
    // file, it pointed at a file shading had renamed, and the app failed to load the assembly.
    [Fact]
    public void Execute_RenamesLocalPathOfShadedAsset()
    {
        var target = Patch(appDepsJson)["targets"]![".NETCoreApp,Version=v11.0"]!;

        var asset = target["App.Newtonsoft.Json/13.0.4"]!["runtime"]!["lib/net6.0/App.Newtonsoft.Json.dll"]!;
        Assert.Equal("App.Newtonsoft.Json.dll", asset["localPath"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_RenamesDependencyOnShadedLibrary()
    {
        var target = Patch(appDepsJson)["targets"]![".NETCoreApp,Version=v11.0"]!;

        var dependencies = target["App/1.0.0"]!["dependencies"]!.AsObject();
        Assert.Equal("13.0.4", dependencies["App.Newtonsoft.Json"]!.GetValue<string>());
        Assert.False(dependencies.ContainsKey("Newtonsoft.Json"));
    }

    // A publish that reuses an up-to-date deps.json runs the patch over a file it already patched.
    [Fact]
    public void Execute_OnPatchedFile_ChangesNothing()
    {
        var once = Patch(appDepsJson).ToJsonString();

        Assert.Equal(once, Patch(once).ToJsonString());
    }

    static JsonNode Patch(string json)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "App.deps.json");
        File.WriteAllText(path, json);

        var mapping = new TaskItem("App.Newtonsoft.Json");
        mapping.SetMetadata("OriginalName", "Newtonsoft.Json");
        var task = new DepsJsonPatcherTask
        {
            BuildEngine = new TaskTests.MockBuildEngine(),
            DepsJsonPath = path,
            ShadedNameMappings = [mapping]
        };

        Assert.True(task.Execute());
        return JsonNode.Parse(File.ReadAllText(path))!;
    }
}
