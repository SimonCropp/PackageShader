// This file contains code snippets for documentation
// ReSharper disable UnusedVariable
#pragma warning disable CS8321

static class UsageExamples
{
    static void ShaderUsage()
    {
        // begin-snippet: ShaderUsage
        var assemblies = new List<SourceTargetInfo>
        {
            new(
                SourceName: "Newtonsoft.Json",
                SourcePath: @"C:\libs\Newtonsoft.Json.dll",
                TargetName: "Newtonsoft.Json_Shaded",
                TargetPath: @"C:\output\Newtonsoft.Json_Shaded.dll",
                IsShaded: true),
            new(
                SourceName: "MyApp",
                SourcePath: @"C:\libs\MyApp.dll",
                TargetName: "MyApp",
                TargetPath: @"C:\output\MyApp.dll",
                IsShaded: false)
        };

        // Optional: provide a strong name key
        var key = StrongNameKey.FromFile("mykey.snk");

        Shader.Run(
            infos: assemblies,
            // Make shaded assembly types internal
            internalize: true,
            // null if strong naming is not required
            key: key);
        // end-snippet
    }

    static void LowLevelUsage()
    {
        StrongNameKey key = null!;

        // begin-snippet: LowLevelUsage
        using var modifier = StreamingAssemblyModifier.Open("MyAssembly.dll");

        modifier.SetAssemblyName("MyAssembly_Shaded");
        modifier.SetAssemblyPublicKey(key.PublicKey);
        modifier.RedirectAssemblyRef("Newtonsoft.Json", "Newtonsoft.Json_Shaded", key.PublicKeyToken);
        modifier.MakeTypesInternal();
        modifier.AddInternalsVisibleTo("MyApp", key.PublicKey);

        modifier.Save("MyAssembly_Shaded.dll", key);
        // end-snippet
    }
}
