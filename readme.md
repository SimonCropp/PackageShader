# <img src='/src/icon.png' height='30px'> PackageShader

[![Build status](https://ci.appveyor.com/api/projects/status/s3agb6fiax7pgwls/branch/main?svg=true)](https://ci.appveyor.com/project/SimonCropp/package-shader)
[![NuGet Status](https://img.shields.io/nuget/v/PackageShader.svg?label=PackageShader%20nuget)](https://www.nuget.org/packages/PackageShader/)
[![NuGet Status](https://img.shields.io/nugetRename assemblies and fixes references/v/PackageShaderTool.svg?label=PackageShaderTool%20nuget)](https://www.nuget.org/packages/PackageShaderTool/)

. Designed as an alternative to [Costura](https://github.com/Fody/Costura), [ILMerge](https://github.com/dotnet/ILMerge), and [ILRepack](https://github.com/gluck/il-repack).

**See [Milestones](../../milestones?state=closed) for release notes.**

Designed to mitigate scenarios where an assembly is run in a plugin scenario. For example Unity extensions, MSBuild tasks, or SharePoint extensions. In these scenarios an assembly, and all its references, are loaded into a shared AppDomain. So dependencies operate as "first on wins". So, for example, if two addins assemblies use different versions of Newtonsoft, the first addin that is loaded defines what version of Newtonsoft is used by all subsequent addins assemblies.

This project works around this problem by renaming references and preventing name conflicts.


## dotnet tool

https://www.nuget.org/packages/PackageShaderTool/

**[.net 10](https://dotnet.microsoft.com/download/dotnet/10.0) or higher is required to run this tool.**

For a given directory and a subset of assemblies:

 * Changes the assembly name of each "shaded" assembly.
 * Renames "shaded" assemblies on disk.
 * For all assemblies, fixes the references to point to the new shaded assemblies.


### Installation

Ensure [dotnet CLI is installed](https://docs.microsoft.com/en-us/dotnet/core/tools/).

Install [PackageShaderTool](https://nuget.org/packages/PackageShaderTool/)

```ps
dotnet tool install --global PackageShaderTool
```


### Usage

```ps
packageshader --target-directory "C:/Code/TargetDirectory"
              --suffix _Shaded
              --assemblies-to-shade "Microsoft*;System*;EmptyFiles"
```


### Arguments


#### Target Directory

`-t` or `--target-directory`

Optional. If no directory is passed the current directory will be used.


#### Internalize

`-i` or `--internalize`

Optional. To internalize all types in the shaded assemblies. Defaults to false.


#### Prefix/Suffix

Either a prefix or suffix must be defined.


##### Prefix

`-p` or `--prefix`

The prefix to use when renaming assemblies.


##### Suffix

`-s` or `--suffix`

The suffix to use when renaming assemblies.


#### Assemblies to shade

`-a` or `--assemblies-to-shade`

Required. A semi-colon separated list of assembly names to shade. Names ending in `*` are treated as wildcards.


#### Assemblies to exclude

`-e` or `--assemblies-to-exclude`

Optional. A semi-colon separated list of assembly names to exclude.


#### Key

`-k` or `--key`

Path to an snk file.

Optional. If no key is passed, strong naming will be removed from all assemblies.
