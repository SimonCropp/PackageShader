# <img src='/src/icon.png' height='30px'> PackageShader

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/packageshader)](https://ci.appveyor.com/project/SimonCropp/packageshader)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.svg?label=PackageShader)](https://www.nuget.org/packages/PackageShader/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShaderTool.svg?label=PackageShaderTool)](https://www.nuget.org/packages/PackageShaderTool/)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.MsBuild.svg?label=PackageShader.MsBuild)](https://www.nuget.org/packages/PackageShader.MsBuild/)

Avoid dependency conflicts in assemblies changing the name of references. Designed as an alternative to [Costura](https://github.com/Fody/Costura), [ILMerge](https://github.com/dotnet/ILMerge), and [ILRepack](https://github.com/gluck/il-repack).

This project is inspired by [Alias](https://github.com/getsentry/dotnet-assembly-alias). Credit goes to [Sentry](https://sentry.io/) for producing the original Alias project. See their blog post [Alias: An approach to .NET assembly conflict resolution](https://blog.sentry.io/alias-an-approach-to-net-assembly-conflict-resolution/) for background on the approach.

**See [Milestones](../../milestones?state=closed) for release notes.**


## The Problem

In .NET plugin/extension based applications, all assemblies are loaded into a single shared context, making it impossible to load multiple versions of the same assembly simultaneously. When an assemblies depend on different versions of a library (like Newtonsoft.Json), conflicts arise based on load order - whichever version loads first is used by all subsequent assemblies, causing unexpected behavior or exceptions.

This is particularly common in:


### The .net build pipeline.

The following all run in a shared AppDomain launched by either msbuild or the IDE

 * **C# source generators**
 * **C# Analyzers**
 * **MSBuild tasks**


### Extension scenarios

 * **Unity extensions** - Unity Package Manager packages bundle System DLLs which can result in conflicts
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
| [ILRepack](https://github.com/gluck/il-repack) | Merges IL into single assembly | |
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
    bool IsShaded,
    bool IsRootAssembly = false);
```
<sup><a href='/src/PackageShader/SourceTargetInfo.cs#L3-L11' title='Snippet source file'>snippet source</a> | <a href='#snippet-SourceTargetInfo' title='Start of snippet'>anchor</a></sup>
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
<!-- Mark references to shade with Shade="true" -->
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
  <ProjectReference Include="..\MyLibrary\MyLibrary.csproj" Shade="true" />
</ItemGroup>

<!-- Optional MSBuild properties -->
<PropertyGroup>
  <!-- Make shaded types internal (default: false) -->
  <Shader_Internalize>true</Shader_Internalize>
</PropertyGroup>
```
<sup><a href='/src/msbuild-config.include.xml#L1-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-MsBuildConfig' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### How It Works

The MSBuild package:

1. Runs after the `AfterCompile` target
2. Identifies `PackageReference` and `ProjectReference` items marked with `Shade="true"`
3. Matches those references to assemblies in `ReferenceCopyLocalPaths`
4. Renames the matched assemblies with the specified prefix/suffix (default: `_Shaded`)
5. Updates all assembly references to point to the renamed assemblies
6. Optionally internalizes types and adds `InternalsVisibleTo` attributes
7. Signs assemblies with the project's `AssemblyOriginatorKeyFile` if `SignAssembly` is true
8. Excludes shaded dependencies from the NuGet package dependency list (sets `PrivateAssets="all"`)
9. Includes shaded assemblies in the NuGet package output, automatically co-located with the primary assembly


### Full Example

<!-- snippet: MsBuildFull -->
<a id='snippet-MsBuildFull'></a>
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Shader_Internalize>true</Shader_Internalize>

    <!-- Optional: strong name signing -->
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>mykey.snk</AssemblyOriginatorKeyFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PackageShader.MsBuild" PrivateAssets="all" />

    <!-- Mark dependencies to shade with Shade="true" -->
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
    <PackageReference Include="Serilog" Version="3.1.0" Shade="true" />

    <!-- Dependencies without Shade="true" are not shaded -->
    <PackageReference Include="Microsoft.Extensions.Logging" Version="8.0.0" />
  </ItemGroup>
</Project>
```
<sup><a href='/src/msbuild-full.include.xml#L1-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-MsBuildFull' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This configuration will:

- Shade `Newtonsoft.Json` and `Serilog` (marked with `Shade="true"`)
- Leave `Microsoft.Extensions.Logging` unchanged
- Make all types in shaded assemblies internal
- Add `InternalsVisibleTo` attributes so the main assembly can access shaded types
- Sign all assemblies with `mykey.snk`
- Exclude shaded dependencies from the NuGet package dependency list


### NuGet Package Path Behavior

By default, shaded assemblies are placed in `lib/$(TargetFramework)` in the NuGet package.

**Automatic Co-location**: If the project uses custom `TfmSpecificPackageFile` entries to place the primary assembly in a non-standard location (e.g., `task/$(TargetFramework)` for MSBuild task packages), shaded assemblies are automatically co-located with the primary assembly.

Example for MSBuild task package:

```xml
<PropertyGroup>
  <IncludeBuildOutput>false</IncludeBuildOutput>
</PropertyGroup>

<ItemGroup>
  <!-- Place primary DLL in task/ folder -->
  <TfmSpecificPackageFile Include="$(OutputPath)$(TargetFileName)">
    <PackagePath>task/$(TargetFramework)</PackagePath>
    <Pack>true</Pack>
  </TfmSpecificPackageFile>

  <!-- Shaded assemblies will automatically go to task/$(TargetFramework) -->
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" Shade="true" />
</ItemGroup>
```

**Manual Override**: To explicitly specify the package path for shaded assemblies, use the `ShadedAssembliesPackagePath` property:

```xml
<PropertyGroup>
  <!-- Manually specify where shaded assemblies should be placed -->
  <ShadedAssembliesPackagePath>tools/$(TargetFramework)</ShadedAssembliesPackagePath>
</PropertyGroup>
```


### Building an MSBuild Task Package

When authoring an MSBuild task as a NuGet package, all task dependencies are loaded into the host MSBuild process (or IDE). This makes version conflicts almost inevitable since the host already has its own versions of common libraries loaded. PackageShader solves this by renaming your task's dependencies so they don't collide with the host's assemblies.

Here's how to structure an MSBuild task package that uses PackageShader, based on [MarkdownSnippets](https://github.com/SimonCropp/MarkdownSnippets):


#### Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Target both netstandard2.0 (for .NET Framework MSBuild)
         and net10.0 (for .NET Core MSBuild / dotnet build) -->
    <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>

    <!-- Mark as build-time only dependency -->
    <DevelopmentDependency>true</DevelopmentDependency>

    <!-- Prevent the default lib/ output; we use task/ instead -->
    <IncludeBuildOutput>false</IncludeBuildOutput>

    <!-- Ensure all transitive dependencies are copied to output -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>

    <!-- PackageShader: don't internalize since MSBuild needs
         to load the task's public types -->
    <Shader_Internalize>false</Shader_Internalize>

    <!-- Optional: strong name signing -->
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>..\key.snk</AssemblyOriginatorKeyFile>
  </PropertyGroup>

  <ItemGroup>
    <!-- Place the task DLL in a task/ subfolder in the NuGet package -->
    <TfmSpecificPackageFile Include=".\bin\$(Configuration)\$(TargetFramework)\MyTask.dll">
      <Pack>true</Pack>
      <PackagePath>task\$(TargetFramework)</PackagePath>
    </TfmSpecificPackageFile>

    <!-- Include the .targets file so consuming projects
         automatically run the task -->
    <Content Include="MyTask.targets">
      <Pack>true</Pack>
      <PackagePath>build</PackagePath>
    </Content>

    <!-- PackageShader.MsBuild does the shading at build time -->
    <PackageReference Include="PackageShader.MsBuild" PrivateAssets="all" />

    <!-- MSBuild SDK reference (not shaded, provided by the host) -->
    <PackageReference Include="Microsoft.Build.Tasks.Core" PrivateAssets="All" />

    <!-- Shade your own library and its transitive dependencies -->
    <ProjectReference Include="..\MyLibrary\MyLibrary.csproj"
                      Shade="true" PrivateAssets="All" />

    <!-- Shade system packages that may conflict with the host,
         only for frameworks where they aren't built-in -->
    <PackageReference Include="System.Collections.Immutable"
                      Shade="true"
                      Condition="'$(TargetFramework)' != 'net10.0'" />
    <PackageReference Include="System.Memory"
                      Shade="true"
                      Condition="'$(TargetFramework)' == 'netstandard2.0'" />
  </ItemGroup>
</Project>
```


#### Targets File

The `.targets` file is included in the NuGet package and tells MSBuild how to load and run the task:

```xml
<Project ToolsVersion="4.0"
         xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <!-- Select the correct TFM based on the MSBuild runtime -->
    <MyTaskAssembly
        Condition="$(MSBuildRuntimeType) == 'Core'">
      $(MSBuildThisFileDirectory)..\task\net10.0\MyTask.dll
    </MyTaskAssembly>
    <MyTaskAssembly
        Condition="$(MSBuildRuntimeType) != 'Core'">
      $(MSBuildThisFileDirectory)..\task\netstandard2.0\MyTask.dll
    </MyTaskAssembly>
  </PropertyGroup>

  <UsingTask TaskName="MyNamespace.MyTask"
             AssemblyFile="$(MyTaskAssembly)" />

  <Target Name="MyTaskTarget"
          AfterTargets="AfterCompile"
          Condition="$(DesignTimeBuild) != true">
    <MyNamespace.MyTask ProjectDirectory="$(MSBuildProjectDirectory)" />
  </Target>
</Project>
```


#### Key Points

 * **Dual-target `netstandard2.0` and a modern TFM** - The `netstandard2.0` build runs in .NET Framework MSBuild (Visual Studio), while the modern TFM runs in `dotnet build`. The `.targets` file selects the right one at runtime via `MSBuildRuntimeType`.
 * **Set `IncludeBuildOutput` to false** - Standard `lib/` output is not needed. Instead use `TfmSpecificPackageFile` to place the task DLL in a `task/` folder. Shaded assemblies are automatically co-located.
 * **Set `CopyLocalLockFileAssemblies` to true** - Ensures all transitive dependencies are available in the output directory for PackageShader to process.
 * **Set `DevelopmentDependency` to true** - Prevents the package from appearing as a runtime dependency in consuming projects.
 * **Conditionally shade system packages** - Packages like `System.Memory` and `System.Buffers` are only needed (and should only be shaded) for `netstandard2.0`. Modern TFMs include them in the framework.
 * **Don't shade `Microsoft.Build.*`** - These are provided by the MSBuild host process and must not be shaded.
 * **Consider `Shader_Internalize`** - Set to `false` for MSBuild tasks since the host must be able to load the task type by name. Use `true` if the task class itself doesn't expose shaded types in its public API and you want maximum isolation.


## Icon

[Shade](https://thenounproject.com/icon/shade-7850642/) designed by [Kim Naces](https://thenounproject.com/creator/kim2262/) from [The Noun Project](https://thenounproject.com).
