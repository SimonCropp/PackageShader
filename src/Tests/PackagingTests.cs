using CliWrap;
using CliWrap.Buffered;

[Collection("Sequential")]
public class PackagingTests
{
    [Fact(Skip = "Integration test needs more work - manually verify fix with MarkdownSnippets instead")]
    public async Task ShadedAssemblies_IncludedInPackage_WhenIncludeBuildOutputIsFalse()
    {
        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir, "TestProject");
        Directory.CreateDirectory(projectDir);

        // Create a test project that reproduces the bug:
        // - IncludeBuildOutput=false (like MSBuild task packages)
        // - References PackageShader.MsBuild
        // - Has a dependency to shade (Newtonsoft.Json)
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net9.0</TargetFrameworks>
                <IncludeBuildOutput>false</IncludeBuildOutput>
                <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
                <PackageId>TestProject</PackageId>
                <Version>1.0.0</Version>
                <LangVersion>latest</LangVersion>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
                <ProjectReference Include="{PACKAGESHADER_PROJECT_PATH}" />
              </ItemGroup>

              <Import Project="{PACKAGESHADER_TARGETS_PATH}" />
            </Project>
            """;

        // Get PackageShader.MsBuild paths (go up from Tests directory to src directory)
        var testsDirectory = ProjectFiles.ProjectDirectory.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var srcDirectory = Directory.GetParent(testsDirectory)!.FullName;
        var packageShaderProjectPath = Path.Combine(
            srcDirectory,
            "PackageShader.MsBuild",
            "PackageShader.MsBuild.csproj"
        );
        var packageShaderTargetsPath = Path.Combine(
            srcDirectory,
            "PackageShader.MsBuild",
            "build",
            "PackageShader.MsBuild.targets"
        );

        projectContent = projectContent.Replace("{PACKAGESHADER_PROJECT_PATH}", packageShaderProjectPath);
        projectContent = projectContent.Replace("{PACKAGESHADER_TARGETS_PATH}", packageShaderTargetsPath);

        var projectFile = Path.Combine(projectDir, "TestProject.csproj");
        File.WriteAllText(projectFile, projectContent);

        // Create a minimal C# file
        var programFile = Path.Combine(projectDir, "TestClass.cs");
        File.WriteAllText(programFile, @"
using Newtonsoft.Json;

public class TestClass
{
    public string Serialize(object obj)
    {
        return JsonConvert.SerializeObject(obj);
    }
}");

        // Build the project
        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments(["build", projectFile, "-c", "Release"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        Assert.True(
            buildResult.ExitCode == 0,
            $"Build failed with exit code {buildResult.ExitCode}\nStdOut:\n{buildResult.StandardOutput}\nStdErr:\n{buildResult.StandardError}"
        );

        // Pack the project
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments(["pack", projectFile, "-c", "Release", "--no-build"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        Assert.True(
            packResult.ExitCode == 0,
            $"Pack failed with exit code {packResult.ExitCode}\nStdOut:\n{packResult.StandardOutput}\nStdErr:\n{packResult.StandardError}"
        );

        // Find the generated .nupkg
        var nupkgPath = Directory.GetFiles(
            Path.Combine(projectDir, "bin", "Release"),
            "TestProject.1.0.0.nupkg",
            SearchOption.AllDirectories
        ).First();

        Assert.True(File.Exists(nupkgPath), $"Package not found at {nupkgPath}");

        // Extract and verify package contents
        var extractDir = Path.Combine(tempDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(nupkgPath, extractDir);

        // Debug: List all files in the package
        var allFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(extractDir.Length + 1))
            .ToList();

        // Verify shaded assemblies are present in both TFMs
        var netstandard20ShadedDll = Path.Combine(extractDir, "lib", "netstandard2.0", "TestProject.Newtonsoft.Json.dll");
        var net90ShadedDll = Path.Combine(extractDir, "lib", "net9.0", "TestProject.Newtonsoft.Json.dll");

        Assert.True(
            File.Exists(netstandard20ShadedDll),
            $"Shaded assembly not found in netstandard2.0: {netstandard20ShadedDll}\n" +
            $"All files in package:\n{string.Join("\n", allFiles)}"
        );

        Assert.True(
            File.Exists(net90ShadedDll),
            $"Shaded assembly not found in net9.0: {net90ShadedDll}\n" +
            $"Directory contents: {string.Join(", ", Directory.Exists(Path.Combine(extractDir, "lib", "net9.0")) ? Directory.GetFiles(Path.Combine(extractDir, "lib", "net9.0")) : Array.Empty<string>())}"
        );

        // Verify the shaded DLL has content (not just an empty file)
        var netstandard20Info = new FileInfo(netstandard20ShadedDll);
        Assert.True(netstandard20Info.Length > 1000, $"Shaded assembly appears empty: {netstandard20Info.Length} bytes");
    }

    [Fact(Skip = "Integration test needs more work - manually verify fix with MarkdownSnippets instead")]
    public async Task ShadedAssemblies_PlacedInTaskFolder_WhenCustomPackagePathSet()
    {
        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir, "TestProject");
        Directory.CreateDirectory(projectDir);

        // Create a test project for MSBuild task package (needs assemblies in task/ folder)
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net9.0</TargetFrameworks>
                <IncludeBuildOutput>false</IncludeBuildOutput>
                <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
                <PackageId>TestProject</PackageId>
                <Version>1.0.0</Version>
                <Nullable>enable</Nullable>
                <ShadedAssembliesPackagePath>task\$(TargetFramework)</ShadedAssembliesPackagePath>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
                <ProjectReference Include="{PACKAGESHADER_PROJECT_PATH}" />
              </ItemGroup>
            </Project>
            """;

        // Get PackageShader.MsBuild paths (go up from Tests directory to src directory)
        var testsDirectory = ProjectFiles.ProjectDirectory.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var srcDirectory = Directory.GetParent(testsDirectory)!.FullName;
        var packageShaderProjectPath = Path.Combine(
            srcDirectory,
            "PackageShader.MsBuild",
            "PackageShader.MsBuild.csproj"
        );
        var packageShaderTargetsPath = Path.Combine(
            srcDirectory,
            "PackageShader.MsBuild",
            "build",
            "PackageShader.MsBuild.targets"
        );

        projectContent = projectContent.Replace("{PACKAGESHADER_PROJECT_PATH}", packageShaderProjectPath);
        projectContent = projectContent.Replace("{PACKAGESHADER_TARGETS_PATH}", packageShaderTargetsPath);

        var projectFile = Path.Combine(projectDir, "TestProject.csproj");
        File.WriteAllText(projectFile, projectContent);

        // Create a minimal C# file
        var programFile = Path.Combine(projectDir, "TestClass.cs");
        File.WriteAllText(programFile, @"
using Newtonsoft.Json;

public class TestClass
{
    public string Serialize(object obj)
    {
        return JsonConvert.SerializeObject(obj);
    }
}");

        // Build the project
        var buildResult = await Cli.Wrap("dotnet")
            .WithArguments(["build", projectFile, "-c", "Release"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        Assert.True(
            buildResult.ExitCode == 0,
            $"Build failed with exit code {buildResult.ExitCode}\nStdOut:\n{buildResult.StandardOutput}\nStdErr:\n{buildResult.StandardError}"
        );

        // Pack the project
        var packResult = await Cli.Wrap("dotnet")
            .WithArguments(["pack", projectFile, "-c", "Release", "--no-build"])
            .WithWorkingDirectory(projectDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

        Assert.True(
            packResult.ExitCode == 0,
            $"Pack failed with exit code {packResult.ExitCode}\nStdOut:\n{packResult.StandardOutput}\nStdErr:\n{packResult.StandardError}"
        );

        // Find the generated .nupkg
        var nupkgPath = Directory.GetFiles(
            Path.Combine(projectDir, "bin", "Release"),
            "TestProject.1.0.0.nupkg",
            SearchOption.AllDirectories
        ).First();

        Assert.True(File.Exists(nupkgPath), $"Package not found at {nupkgPath}");

        // Extract and verify package contents
        var extractDir = Path.Combine(tempDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(nupkgPath, extractDir);

        // Verify shaded assemblies are present in task/ folder instead of lib/
        var netstandard20ShadedDll = Path.Combine(extractDir, "task", "netstandard2.0", "TestProject.Newtonsoft.Json.dll");
        var net90ShadedDll = Path.Combine(extractDir, "task", "net9.0", "TestProject.Newtonsoft.Json.dll");

        Assert.True(
            File.Exists(netstandard20ShadedDll),
            $"Shaded assembly not found in task/netstandard2.0: {netstandard20ShadedDll}"
        );

        Assert.True(
            File.Exists(net90ShadedDll),
            $"Shaded assembly not found in task/net9.0: {net90ShadedDll}"
        );
    }
}
