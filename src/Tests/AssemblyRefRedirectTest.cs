#pragma warning disable xUnit1051

/// <summary>
/// This test mimics the MarkdownSnippets scenario: redirecting assembly references
/// to longer names, which triggers metadata growth and the MethodDef RVA bug.
/// </summary>
public class AssemblyRefRedirectTest
{
    [Fact]
    public async Task RedirectingAssemblyRefs_AssemblyCanBeLoaded()
    {
        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir, "TestProject");
        Directory.CreateDirectory(projectDir);

        // Create a test assembly that references System.Collections.Immutable
        var sourceCode = """
using System.Collections.Immutable;

namespace TestAssembly;

public class TestClass
{
    public ImmutableArray<string> GetData()
    {
        return ImmutableArray.Create("test1", "test2");
    }

    public string ProcessData(ImmutableArray<int> data)
    {
        return $"Count: {data.Length}";
    }
}
""";

        await File.WriteAllTextAsync(Path.Combine(projectDir, "Class.cs"), sourceCode);

        var projectContent = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Collections.Immutable" Version="10.0.2" />
  </ItemGroup>
</Project>
""";

        await File.WriteAllTextAsync(Path.Combine(projectDir, "TestProject.csproj"), projectContent);

        // Build the project
        var result = await Cli.Wrap("dotnet")
            .WithArguments(["build", Path.Combine(projectDir, "TestProject.csproj"), "-c", "Release", "--nologo", "-v", "quiet"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Build failed: {result.StandardError}");
        }

        var sourcePath = Path.Combine(projectDir, "bin", "Release", "netstandard2.0", "TestProject.dll");
        var outputPath = Path.Combine(tempDir, "Modified.dll");

        // Get test key for strong naming (just like MarkdownSnippets does)
        var keyPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
        var key = StrongNameKey.FromFile(keyPath);

        // Redirect assembly reference from "System.Collections.Immutable"
        // to "TestProject.System.Collections.Immutable" (longer name = metadata growth)
        // This is EXACTLY what PackageShader does in the MarkdownSnippets scenario
        using (var modifier = StreamingAssemblyModifier.Open(sourcePath))
        {
            modifier.SetAssemblyPublicKey(key.PublicKey);

            // Redirect to a MUCH longer name to force significant metadata growth
            var redirected = modifier.RedirectAssemblyRef(
                "System.Collections.Immutable",
                "TestProject.System.Collections.Immutable",
                key.PublicKeyToken);

            Console.WriteLine($"Redirected System.Collections.Immutable: {redirected}");

            modifier.Save(outputPath, key);
        }

        Console.WriteLine($"Modified assembly saved to: {outputPath}");
        Console.WriteLine($"File size: {new FileInfo(outputPath).Length} bytes");

        // Try to LOAD the modified assembly - if MethodDef RVAs aren't patched,
        // this will fail with "Bad IL format" just like MarkdownSnippets.MsBuild.dll
        var loadContext = new AssemblyLoadContext($"LoadTest_{Guid.NewGuid()}", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(outputPath);
            using var stream = new MemoryStream(bytes);

            var assembly = loadContext.LoadFromStream(stream);

            Assert.NotNull(assembly);
            Assert.Equal("TestProject", assembly.GetName().Name);

            // Verify we can actually get types (this will fail if IL is corrupt)
            var types = assembly.GetTypes();
            Assert.True(types.Length >= 1);

            Console.WriteLine($"SUCCESS: Loaded assembly with {types.Length} types");
            foreach (var type in types)
            {
                Console.WriteLine($"  - {type.Name}");

                // Try to get methods to verify IL is readable
                var methods = type.GetMethods();
                Console.WriteLine($"    Methods: {methods.Length}");
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }
}
