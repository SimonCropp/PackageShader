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
}
