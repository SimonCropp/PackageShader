public class FinderTests
{
    [Fact]
    public void SingleExactMatch_YieldsOneShadedEntry()
    {
        var results = Finder.FindAssemblyInfos(
            ["Foo"],
            ["dir/Foo.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var shaded = results.Where(_ => _.IsShaded).ToList();
        Assert.Single(shaded);
        Assert.Equal("Foo", shaded[0].SourceName);
        Assert.Equal("Shaded_Foo", shaded[0].TargetName);
    }

    [Fact]
    public void SingleWildcardMatch_YieldsOneShadedEntry()
    {
        var results = Finder.FindAssemblyInfos(
            ["Foo*"],
            ["dir/FooBar.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var shaded = results.Where(_ => _.IsShaded).ToList();
        Assert.Single(shaded);
        Assert.Equal("FooBar", shaded[0].SourceName);
        Assert.Equal("Shaded_FooBar", shaded[0].TargetName);
    }

    [Fact]
    public void NoMatch_YieldsSingleRootEntry()
    {
        var results = Finder.FindAssemblyInfos(
            ["Bar"],
            ["dir/Foo.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var single = Assert.Single(results);
        Assert.False(single.IsShaded);
        Assert.True(single.IsRootAssembly);
        Assert.Equal("Foo", single.SourceName);
        Assert.Equal("Foo", single.TargetName);
    }

    [Fact]
    public void OverlappingExactAndWildcard_YieldsOnce()
    {
        // Both "Foo" (exact) and "Foo*" (wildcard) match "Foo.dll". Prior behavior
        // yielded the SourceTargetInfo twice; the fix breaks after the first match.
        var results = Finder.FindAssemblyInfos(
            ["Foo", "Foo*"],
            ["dir/Foo.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var shaded = results.Where(_ => _.IsShaded).ToList();
        Assert.Single(shaded);
        Assert.Equal("Foo", shaded[0].SourceName);
    }

    [Fact]
    public void OverlappingWildcards_YieldsOnce()
    {
        // Two wildcard patterns both match the same file
        var results = Finder.FindAssemblyInfos(
            ["Foo*", "F*"],
            ["dir/FooBar.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var shaded = results.Where(_ => _.IsShaded).ToList();
        Assert.Single(shaded);
        Assert.Equal("FooBar", shaded[0].SourceName);
    }

    [Fact]
    public void MultipleFilesWithOverlappingPatterns_YieldsEachOnce()
    {
        // Verifies the inner-loop break doesn't prematurely exit the outer-loop iteration
        var results = Finder.FindAssemblyInfos(
            ["Foo", "Foo*", "Bar"],
            ["dir/Foo.dll", "dir/FooHelper.dll", "dir/Bar.dll", "dir/Unrelated.dll"],
            prefix: "Shaded_",
            suffix: null).ToList();

        var shaded = results.Where(_ => _.IsShaded).OrderBy(_ => _.SourceName).ToList();
        Assert.Equal(3, shaded.Count);
        Assert.Equal("Bar", shaded[0].SourceName);
        Assert.Equal("Foo", shaded[1].SourceName);
        Assert.Equal("FooHelper", shaded[2].SourceName);

        // Unrelated.dll should come through as a non-shaded root
        var unrelated = results.Where(_ => !_.IsShaded).ToList();
        var single = Assert.Single(unrelated);
        Assert.Equal("Unrelated", single.SourceName);
    }

    [Fact]
    public void NullPrefixAndSuffix_Throws() =>
        Assert.Throws<ErrorException>(() =>
            Finder.FindAssemblyInfos(["Foo"], ["dir/Foo.dll"], prefix: null, suffix: null).ToList());
}
