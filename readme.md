# <img src='/src/icon.png' height='30px'> PackageShader

[![Build status](https://img.shields.io/appveyor/build/packageshader)](https://ci.appveyor.com/project/SimonCropp/packageshader)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.svg?label=PackageShader)](https://www.nuget.org/packages/PackageShader/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShaderTool.svg?label=PackageShaderTool)](https://www.nuget.org/packages/PackageShaderTool/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.MsBuild.svg?label=PackageShader.MsBuild)](https://www.nuget.org/packages/PackageShader.MsBuild/)

Avoid dependency conflicts in assemblies change changing the name of references. Designed as an alternative to [Costura](https://github.com/Fody/Costura), [ILMerge](https://github.com/dotnet/ILMerge), and [ILRepack](https://github.com/gluck/il-repack).

This project is a fork of [Alias](https://github.com/getsentry/dotnet-assembly-alias). Credit goes to [Sentry](https://sentry.io/) for producing the original Alias project. See their blog post [Alias: An approach to .NET assembly conflict resolution](https://blog.sentry.io/alias-an-approach-to-net-assembly-conflict-resolution/) for background on the approach.

**See [Milestones](../../milestones?state=closed) for release notes.**


## The Problem

In .NET plugin/extension based applications, all assemblies are loaded into a single shared context, making it impossible to load multiple versions of the same assembly simultaneously. When an assemblies depend on different versions of a library (like Newtonsoft.Json), conflicts arise based on load order - whichever version loads first is used by all subsequent assemblies, causing unexpected behavior or exceptions.

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


## PackageShader (Library)

https://www.nuget.org/packages/PackageShader/

The core library provides programmatic access to assembly shading functionality.


### Installation

```
dotnet add package PackageShader
```


### API

The main entry point is `Shader.Run()`:

<!-- snippet: ShaderUsage -->
<a id='snippet-ShaderUsage'></a>
```cs
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
```
<sup><a href='/src/Tests/UsageExamples.cs#L9-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-ShaderUsage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### SourceTargetInfo

<!-- snippet: SourceTargetInfo -->
<a id='snippet-SourceTargetInfo'></a>
```cs
public record SourceTargetInfo(
    string SourceName,
    string SourcePath,
    string TargetName,
    string TargetPath,
    bool IsShaded);
```
<sup><a href='/src/PackageShader/SourceTargetInfo.cs#L3-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-SourceTargetInfo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Low-Level API

For fine-grained control, use `StreamingAssemblyModifier` directly:

<!-- snippet: LowLevelUsage -->
<a id='snippet-LowLevelUsage'></a>
```cs
using var modifier = StreamingAssemblyModifier.Open("MyAssembly.dll");

modifier.SetAssemblyName("MyAssembly_Shaded");
modifier.SetAssemblyPublicKey(key.PublicKey);
modifier.RedirectAssemblyRef("Newtonsoft.Json", "Newtonsoft.Json_Shaded", key.PublicKeyToken);
modifier.MakeTypesInternal();
modifier.AddInternalsVisibleTo("MyApp", key.PublicKey);

modifier.Save("MyAssembly_Shaded.dll", key);
```
<sup><a href='/src/Tests/UsageExamples.cs#L42-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-LowLevelUsage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


---


## PackageShaderTool (CLI)

https://www.nuget.org/packages/PackageShaderTool/

**.NET  SDK 10 or higher is required to run this tool.**

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

Configure shading via MSBuild properties in the project file:

<!-- snippet: MsBuildConfig -->
<a id='snippet-MsBuildConfig'></a>
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
<sup><a href='/src/msbuild-config.include.xml#L1-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-MsBuildConfig' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### How It Works

The MSBuild package:

1. Runs after the `AfterCompile` target
1. Processes the intermediate assembly and its `ReferenceCopyLocalPaths`
1. Renames assemblies matching the pattern (all except those in `Shader_AssembliesToSkipRename`)
1. Fixes assembly references
1. Optionally internalizes types and adds `InternalsVisibleTo` attributes
1. Signs with the project's `AssemblyOriginatorKeyFile` if `SignAssembly` is true


### Full Example

<!-- snippet: MsBuildFull -->
<a id='snippet-MsBuildFull'></a>
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
<sup><a href='/src/msbuild-full.include.xml#L1-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-MsBuildFull' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This configuration will:

- Shade all referenced assemblies except the project's own assembly
- Add `_Shaded` suffix to shaded assembly names
- Make all types in shaded assemblies internal
- Add `InternalsVisibleTo` attributes so the main assembly can still access internal types
- Sign all assemblies with `mykey.snk`


## Icon

[Shade](https://thenounproject.com/icon/shade-7850642/) designed by [Kim Naces](https://thenounproject.com/creator/kim2262/) from [The Noun Project](https://thenounproject.com).
