#pragma warning disable xUnit1051 // Test method uses cancellable operations

/// <summary>
/// This test verifies that modified assemblies can actually be LOADED by the runtime.
/// The round-trip test only checks metadata, but doesn't try to load the assembly.
/// </summary>
public class AssemblyLoadTest
{
    [Fact]
    public async Task ModifiedAssemblyWithInternalization_CanBeLoaded()
    {
        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir, "TestProject");
        Directory.CreateDirectory(projectDir);

        // Create a simple test assembly
        var sourceCode = """
namespace TestAssembly;

public class TestClass
{
    public string GetMessage() => "Hello World";
    public int Add(int a, int b) => a + b;
}

public class AnotherClass
{
    public void DoSomething() { }
}
""";

        await File.WriteAllTextAsync(Path.Combine(projectDir, "Class.cs"), sourceCode);

        var projectContent = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
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

        // Get test key for strong naming
        var keyPath = Path.Combine(ProjectFiles.ProjectDirectory.Path, "test.snk");
        var key = StrongNameKey.FromFile(keyPath);

        // Modify the assembly: internalize types, add InternalsVisibleTo, and sign
        // This should trigger metadata growth and the rebuild path
        using (var modifier = StreamingAssemblyModifier.Open(sourcePath))
        {
            modifier.SetAssemblyPublicKey(key.PublicKey);
            modifier.MakeTypesInternal();

            // Add many IVT attributes to force metadata growth
            for (var i = 0; i < 10; i++)
            {
                modifier.AddInternalsVisibleTo($"TestFriend{i}", key.PublicKey);
            }

            modifier.Save(outputPath, key);
        }

        // Try to LOAD the modified assembly - this is the real test!
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
            Assert.True(types.Length >= 2);

            Console.WriteLine($"SUCCESS: Loaded assembly with {types.Length} types");
            foreach (var type in types)
            {
                Console.WriteLine($"  - {type.Name}");
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }
}
