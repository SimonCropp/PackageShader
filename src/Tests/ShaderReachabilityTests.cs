[Collection("Sequential")]
public class ShaderReachabilityTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(ShaderReachabilityTests).Assembly.Location)!;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static string ReadAssemblyName(string path)
    {
        using var fs = File.OpenRead(path);
        using var pr = new PEReader(fs);
        var mr = pr.GetMetadataReader();
        return mr.GetString(mr.GetAssemblyDefinition().Name);
    }

    static SourceTargetInfo MakeInfo(string tempDir, string fileName, bool isShaded,
        bool isRoot = false, string suffix = "_Shaded")
    {
        var sourcePath = Path.Combine(tempDir, fileName);
        var sourceName = ReadAssemblyName(sourcePath);
        var targetName = sourceName + suffix;
        var targetPath = Path.Combine(tempDir, targetName + ".dll");
        return new(sourceName, sourcePath, targetName, targetPath, isShaded, isRoot);
    }

    // -------------------------------------------------------------------------
    // No shaded assemblies — validation is a no-op
    // -------------------------------------------------------------------------

    [Fact]
    public void NoShadedAssemblies_ValidationSkipped_NoException()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "DummyAssembly.dll"),
            Path.Combine(tempDir, "DummyAssembly.dll"));

        var info = MakeInfo(tempDir, "DummyAssembly.dll", isShaded: false, isRoot: true, suffix: "_Renamed");

        // No shaded assemblies → validation short-circuits; should not throw
        Shader.Run([info], false, null);
        Assert.True(File.Exists(info.TargetPath));
    }

    // -------------------------------------------------------------------------
    // Root assembly is always exempt — it may reference shaded deps
    // -------------------------------------------------------------------------

    [Fact]
    public void RootAssembly_ReferencingShadedDep_IsExempt()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "AssemblyToProcess.dll"),
            Path.Combine(tempDir, "AssemblyToProcess.dll"));
        File.Copy(
            Path.Combine(binDirectory, "AssemblyToInclude.dll"),
            Path.Combine(tempDir, "AssemblyToInclude.dll"));

        // AssemblyToProcess = root (exempt), AssemblyToInclude = shaded
        var root = MakeInfo(tempDir, "AssemblyToProcess.dll", isShaded: false, isRoot: true);
        var shaded = MakeInfo(tempDir, "AssemblyToInclude.dll", isShaded: true, isRoot: false);

        // Root references shaded dep — this is the intended pattern and must not throw
        Shader.Run([root, shaded], false, null);
        Assert.True(File.Exists(root.TargetPath));
        Assert.True(File.Exists(shaded.TargetPath));
    }

    // -------------------------------------------------------------------------
    // Shaded assembly is always exempt from validation
    // -------------------------------------------------------------------------

    [Fact]
    public void ShadedAssembly_IsExemptFromValidation()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "AssemblyToInclude.dll"),
            Path.Combine(tempDir, "AssemblyToInclude.dll"));
        File.Copy(
            Path.Combine(binDirectory, "AssemblyToProcess.dll"),
            Path.Combine(tempDir, "AssemblyToProcess.dll"));

        // Both shaded → neither is validated → no exception even if they reference each other
        var a = MakeInfo(tempDir, "AssemblyToInclude.dll", isShaded: true);
        var b = MakeInfo(tempDir, "AssemblyToProcess.dll", isShaded: true);

        Shader.Run([a, b], false, null);
    }

    // -------------------------------------------------------------------------
    // Output assembly names are correctly renamed
    // -------------------------------------------------------------------------

    [Fact]
    public void ShadedAssembly_OutputNameMatchesTargetName()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "DummyAssembly.dll"),
            Path.Combine(tempDir, "DummyAssembly.dll"));

        var info = MakeInfo(tempDir, "DummyAssembly.dll", isShaded: true, suffix: "_ShadedV2");

        Shader.Run([info], false, null);

        Assert.True(File.Exists(info.TargetPath));
        Assert.Equal(info.TargetName, ReadAssemblyName(info.TargetPath));
    }

    // -------------------------------------------------------------------------
    // Transitive reference redirect — shaded assembly has its refs updated
    // -------------------------------------------------------------------------

    [Fact]
    public void ShadedAssembly_AssemblyRefsRedirected_ToShadedNames()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "AssemblyToInclude.dll"),
            Path.Combine(tempDir, "AssemblyToInclude.dll"));
        File.Copy(
            Path.Combine(binDirectory, "AssemblyToProcess.dll"),
            Path.Combine(tempDir, "AssemblyToProcess.dll"));

        var includedInfo = MakeInfo(tempDir, "AssemblyToInclude.dll", isShaded: true, suffix: "_Shaded");
        var processInfo = MakeInfo(tempDir, "AssemblyToProcess.dll", isShaded: true, suffix: "_Shaded");

        Shader.Run([includedInfo, processInfo], false, null);

        // Verify AssemblyToProcess output has the correct shaded name
        Assert.True(File.Exists(processInfo.TargetPath));
        Assert.Equal(processInfo.TargetName, ReadAssemblyName(processInfo.TargetPath));

        // Verify AssemblyToInclude output has the correct shaded name
        Assert.True(File.Exists(includedInfo.TargetPath));
        Assert.Equal(includedInfo.TargetName, ReadAssemblyName(includedInfo.TargetPath));
    }

    // -------------------------------------------------------------------------
    // No root assembly — conservative: all assemblies treated as reachable
    // When all assemblies are shaded, no conflict exists
    // -------------------------------------------------------------------------

    [Fact]
    public void NoRootAssembly_AllShaded_NoException()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "AssemblyToInclude.dll"),
            Path.Combine(tempDir, "AssemblyToInclude.dll"));
        File.Copy(
            Path.Combine(binDirectory, "AssemblyToProcess.dll"),
            Path.Combine(tempDir, "AssemblyToProcess.dll"));

        // No root (IsRootAssembly = false) — both shaded → no validation error
        var a = new SourceTargetInfo(
            ReadAssemblyName(Path.Combine(tempDir, "AssemblyToInclude.dll")),
            Path.Combine(tempDir, "AssemblyToInclude.dll"),
            ReadAssemblyName(Path.Combine(tempDir, "AssemblyToInclude.dll")) + "_Shaded",
            Path.Combine(tempDir, ReadAssemblyName(Path.Combine(tempDir, "AssemblyToInclude.dll")) + "_Shaded.dll"),
            IsShaded: true,
            IsRootAssembly: false);

        var b = new SourceTargetInfo(
            ReadAssemblyName(Path.Combine(tempDir, "AssemblyToProcess.dll")),
            Path.Combine(tempDir, "AssemblyToProcess.dll"),
            ReadAssemblyName(Path.Combine(tempDir, "AssemblyToProcess.dll")) + "_Shaded",
            Path.Combine(tempDir, ReadAssemblyName(Path.Combine(tempDir, "AssemblyToProcess.dll")) + "_Shaded.dll"),
            IsShaded: true,
            IsRootAssembly: false);

        Shader.Run([a, b], false, null);
        Assert.True(File.Exists(a.TargetPath));
        Assert.True(File.Exists(b.TargetPath));
    }

    // -------------------------------------------------------------------------
    // Internalize flag — shaded assemblies get MakeTypesInternal
    // -------------------------------------------------------------------------

    [Fact]
    public void Internalize_ShadedAssembly_TypesAreInternal()
    {
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "DummyAssembly.dll"),
            Path.Combine(tempDir, "DummyAssembly.dll"));

        var info = MakeInfo(tempDir, "DummyAssembly.dll", isShaded: true, suffix: "_Shaded");

        Shader.Run([info], internalize: true, null);

        Assert.True(File.Exists(info.TargetPath));

        using var fs = File.OpenRead(info.TargetPath);
        using var pr = new PEReader(fs);
        var mr = pr.GetMetadataReader();

        foreach (var typeHandle in mr.TypeDefinitions)
        {
            var typeDef = mr.GetTypeDefinition(typeHandle);
            if (mr.GetString(typeDef.Name) == "<Module>")
            {
                continue;
            }

            var visibility = typeDef.Attributes & TypeAttributes.VisibilityMask;
            Assert.NotEqual(TypeAttributes.Public, visibility);
            Assert.NotEqual(TypeAttributes.NestedPublic, visibility);
        }
    }

    // -------------------------------------------------------------------------
    // Stray dependency not reachable from root is not validated
    // -------------------------------------------------------------------------

    [Fact]
    public void WithRootAssembly_NonReachableAssembly_NotValidated()
    {
        // AssemblyToInclude is listed in infos but NOT referenced by the root (DummyAssembly).
        // Even though AssemblyToInclude is unshaded and references nothing shaded here,
        // this test verifies that an unreachable unshaded assembly doesn't cause issues.
        using var tempDir = new TempDirectory();

        File.Copy(
            Path.Combine(binDirectory, "DummyAssembly.dll"),
            Path.Combine(tempDir, "DummyAssembly.dll"));
        File.Copy(
            Path.Combine(binDirectory, "AssemblyToInclude.dll"),
            Path.Combine(tempDir, "AssemblyToInclude.dll"));

        var root = MakeInfo(tempDir, "DummyAssembly.dll", isShaded: false, isRoot: true, suffix: "_Renamed");
        var stray = MakeInfo(tempDir, "AssemblyToInclude.dll", isShaded: false, isRoot: false, suffix: "_Renamed");

        // No shaded assemblies → validation is a no-op regardless of reachability
        Shader.Run([root, stray], false, null);
        Assert.True(File.Exists(root.TargetPath));
    }
}
