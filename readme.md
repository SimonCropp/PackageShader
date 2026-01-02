# <img src='/src/icon.png' height='30px'> PackageShader

[![Build status](https://ci.appveyor.com/api/projects/status/s3agb6fiax7pgwls/branch/main?svg=true)](https://ci.appveyor.com/project/SimonCropp/package-shader)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.svg?label=PackageShader)](https://www.nuget.org/packages/PackageShader/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShaderTool.svg?label=PackageShaderTool)](https://www.nuget.org/packages/PackageShaderTool/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.MsBuild.svg?label=PackageShader.MsBuild)](https://www.nuget.org/packages/PackageShader.MsBuild/)

Rename assemblies and fix references. Designed as an alternative to [Costura](https://github.com/Fody/Costura), [ILMerge](https://github.com/dotnet/ILMerge), and [ILRepack](https://github.com/gluck/il-repack).

This project is a fork of [Alias](https://github.com/getsentry/dotnet-assembly-alias). Credit goes to [Sentry](https://sentry.io/) for producing the original Alias project. See their blog post [Alias: An approach to .NET assembly conflict resolution](https://blog.sentry.io/alias-an-approach-to-net-assembly-conflict-resolution/) for background on the approach.

**See [Milestones](../../milestones?state=closed) for release notes.**


## The Problem

.NET plugin-based applications load assemblies into a single shared context, making it impossible to load multiple versions of the same assembly simultaneously. When plugins depend on different versions of a library (like Newtonsoft.Json), conflicts arise based on load order - whichever version loads first is used by all subsequent plugins, causing failures.

This is particularly common in:
 * **Unity extensions** - Unity Package Manager packages bundle System DLLs
 * **MSBuild tasks** - Tasks run in a shared AppDomain
 * **SharePoint/Office extensions** - Plugins share the host's assembly context
 * **Visual Studio extensions** - Extensions share the VS process


## How It Works

PackageShader resolves conflicts by:

1. **Renaming assemblies** - Both the filename and IL assembly name are changed with a unique prefix/suffix
2. **Patching references** - All assembly references are updated to point to the renamed assemblies
3. **Fixing strong names** - Re-signs assemblies if a key is provided, or removes strong naming
4. **Optionally internalizing** - Makes types internal and adds `InternalsVisibleTo` to maintain access

The result is a group of files that will not conflict with any assemblies loaded in the plugin context.


## Alternatives

| Tool | Approach | Limitation |
|------|----------|------------|
| [Costura](https://github.com/Fody/Costura) | Embeds dependencies as resources | Doesn't rename assemblies, conflicts persist |
| [ILMerge](https://github.com/dotnet/ILMerge) | Merges IL into single assembly | Unmaintained, known bugs in .NET Core |
| [ILRepack](https://github.com/gluck/il-repack) | Merges IL into single assembly | Not suitable for plugin deployment scenarios |
| **PackageShader** | Renames and patches references | Produces multiple files (by design) |


## Packages

| Package | Description |
|---------|-------------|
| [PackageShader](https://www.nuget.org/packages/PackageShader/) | Core library for programmatic assembly shading |
| [PackageShaderTool](https://www.nuget.org/packages/PackageShaderTool/) | .NET CLI tool for command-line usage |
| [PackageShader.MsBuild](https://www.nuget.org/packages/PackageShader.MsBuild/) | MSBuild integration for automatic shading at build time |


---


## PackageShader (Library)

https://www.nuget.org/packages/PackageShader/

The core library provides programmatic access to assembly shading functionality.


### Installation

```
dotnet add package PackageShader
```


### API

The main entry point is `Shader.Run()`:

```cs
using PackageShader;

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
StrongNameKey? key = StrongNameKey.Read("mykey.snk");

Shader.Run(
    infos: assemblies,
    internalize: true,  // Make shaded assembly types internal
    key: key);          // null to remove strong naming
```


### SourceTargetInfo

```cs
public record SourceTargetInfo(
    string SourceName,   // Original assembly name
    string SourcePath,   // Path to source assembly
    string TargetName,   // New assembly name
    string TargetPath,   // Path to write modified assembly
    bool IsShaded);      // True if this assembly should be renamed/internalized
```


### Low-Level API

For fine-grained control, use `StreamingAssemblyModifier` directly:

```cs
using var modifier = StreamingAssemblyModifier.Open("MyAssembly.dll");

modifier.SetAssemblyName("MyAssembly_Shaded");
modifier.SetAssemblyPublicKey(key.PublicKey);
modifier.RedirectAssemblyRef("Newtonsoft.Json", "Newtonsoft.Json_Shaded", key.PublicKeyToken);
modifier.MakeTypesInternal();
modifier.AddInternalsVisibleTo("MyApp", key.PublicKey);

modifier.Save("MyAssembly_Shaded.dll", key);
```


---


## PackageShaderTool (CLI)

https://www.nuget.org/packages/PackageShaderTool/

**.NET 10 or higher is required to run this tool.**

For a given directory and a subset of assemblies:

 * Changes the assembly name of each "shaded" assembly.
 * Renames "shaded" assemblies on disk.
 * For all assemblies, fixes the references to point to the new shaded assemblies.


### Installation

```
dotnet tool install --global PackageShaderTool
```


### Usage

```
packageshader --target-directory "C:/Code/TargetDirectory"
              --suffix _Shaded
              --assemblies-to-shade "Microsoft*;System*;EmptyFiles"
```


### Arguments

| Argument | Short | Description |
|----------|-------|-------------|
| `--target-directory` | `-t` | Directory containing assemblies. Defaults to current directory. |
| `--prefix` | `-p` | Prefix for renamed assemblies. |
| `--suffix` | `-s` | Suffix for renamed assemblies. |
| `--assemblies-to-shade` | `-a` | **Required.** Semi-colon separated list. Names ending in `*` are wildcards. |
| `--assemblies-to-exclude` | `-e` | Semi-colon separated list of assemblies to exclude. |
| `--internalize` | `-i` | Make all types in shaded assemblies internal. Defaults to false. |
| `--key` | `-k` | Path to .snk file. If omitted, strong naming is removed. |

Either `--prefix` or `--suffix` must be specified.


### Examples

Shade all Microsoft and System assemblies with a suffix:

```
packageshader -t "C:/MyApp/bin" -s "_Shaded" -a "Microsoft*;System*"
```

Shade specific assemblies with a prefix and internalize:

```
packageshader -t "C:/MyApp/bin" -p "Shaded_" -a "Newtonsoft.Json;Serilog" -i
```

Shade with strong name signing:

```
packageshader -t "C:/MyApp/bin" -s "_Shaded" -a "Newtonsoft*" -k "mykey.snk"
```


---


## PackageShader.MsBuild

https://www.nuget.org/packages/PackageShader.MsBuild/

Automatically shade assemblies at build time via MSBuild integration.


### Installation

```
dotnet add package PackageShader.MsBuild
```


### Configuration

Configure shading via MSBuild properties in your project file:

```xml
<PropertyGroup>
  <!-- Prefix or suffix for shaded assemblies (one required) -->
  <Shader_Prefix>Shaded_</Shader_Prefix>
  <!-- OR -->
  <Shader_Suffix>_Shaded</Shader_Suffix>

  <!-- Make shaded types internal (optional, default: false) -->
  <Shader_Internalize>true</Shader_Internalize>
</PropertyGroup>

<!-- Assemblies to skip renaming (optional) -->
<ItemGroup>
  <Shader_AssembliesToSkipRename Include="MyAssembly" />
  <Shader_AssembliesToSkipRename Include="AnotherAssembly" />
</ItemGroup>
```


### How It Works

The MSBuild package:

1. Runs after the `AfterCompile` target
2. Processes the intermediate assembly and its `ReferenceCopyLocalPaths`
3. Renames assemblies matching the pattern (all except those in `Shader_AssembliesToSkipRename`)
4. Fixes assembly references
5. Optionally internalizes types and adds `InternalsVisibleTo` attributes
6. Signs with the project's `AssemblyOriginatorKeyFile` if `SignAssembly` is true


### Full Example

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Shader_Suffix>_Shaded</Shader_Suffix>
    <Shader_Internalize>true</Shader_Internalize>

    <!-- Optional: strong name signing -->
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>mykey.snk</AssemblyOriginatorKeyFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PackageShader.MsBuild" PrivateAssets="all" />
    <PackageReference Include="Newtonsoft.Json" />
  </ItemGroup>

  <!-- Don't shade these assemblies -->
  <ItemGroup>
    <Shader_AssembliesToSkipRename Include="$(AssemblyName)" />
  </ItemGroup>
</Project>
```

This configuration will:
- Shade all referenced assemblies except the project's own assembly
- Add `_Shaded` suffix to shaded assembly names
- Make all types in shaded assemblies internal
- Add `InternalsVisibleTo` attributes so your code can still access internal types
- Sign all assemblies with `mykey.snk`
