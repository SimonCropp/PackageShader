# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PackageShader is a .NET assembly shading/aliasing tool that resolves dependency conflicts by renaming assemblies and patching all references. It's designed as a modern alternative to Costura, ILMerge, and ILRepack for plugin/extension scenarios where multiple versions of the same assembly need to coexist.

## Code Style

Follow `.editorconfig` for formatting and code style conventions. Respect `.gitattributes` for line endings and file handling.

**Lambda/Delegate Parameters**: For single-parameter delegates, always use `_` as the parameter name instead of a descriptive name:
- ✅ Good: `.OrderBy(_ => _.Property)` or `.Select(_ => _.Transform())`
- ❌ Bad: `.OrderBy(x => x.Property)` or `.Select(item => item.Transform())`

## Build Commands

**Solution file**: `src/PackageShader.slnx`

```bash
# Build the solution
dotnet build src --configuration Debug

# Run tests
dotnet run --project src/Tests --configuration Debug

# Build specific project
dotnet build src/PackageShader/PackageShader.csproj --configuration Debug
```

**Requirements**: .NET SDK 10.0.102+ (see `src/global.json`)

## Test Framework

- **Framework**: xunit.v3 with Verify.XunitV3 for snapshot testing
- **Snapshot files**: `*.verified.txt` files in `src/Tests/` are baseline comparisons
- **Test project**: `src/Tests/Tests.csproj` (targets net10.0)

## Architecture

### Three-Layer API

1. **High-level**: `Shader.Run()` in `src/PackageShader/Shader.cs` - batch processing entry point
2. **Mid-level**: `StreamingAssemblyModifier` in `src/PackageShader/StreamingAssemblyModifier.cs` - per-assembly control with Open/Save pattern
3. **Low-level**: Direct metadata table manipulation in `src/PackageShader/Metadata/` and `src/PackageShader/Modification/`

### Key Components

| Component | Location | Purpose |
|-----------|----------|---------|
| Core library | `src/PackageShader/` | Assembly modification engine |
| CLI tool | `src/PackageShaderTool/` | Command-line interface |
| MSBuild task | `src/PackageShader.MsBuild/` | Build-time integration |

### Streaming Architecture

The codebase emphasizes memory efficiency through streaming I/O:
- `StreamingPEFile` - Opens PE files without loading entire contents
- `StreamingMetadataReader` - Reads only required metadata headers
- `StreamingPEWriter` - Writes modified assemblies with minimal memory footprint

### Two-Strategy Modification Approach

`ModificationPlan` (in `src/PackageShader/Modification/ModificationPlan.cs`) determines which strategy to use:
- **InPlacePatch**: For simple modifications (rename, ref redirect) - copies file and applies binary patches
- **MetadataRebuild**: For complex modifications (new attributes, internalization) - rebuilds entire metadata sections

## Project Structure

```
src/
├── PackageShader/           # Core library
│   ├── Metadata/            # IL metadata reading/writing
│   ├── Modification/        # Modification planning
│   ├── PE/                  # PE file handling
│   └── Signing/             # Strong name signing
├── PackageShaderTool/       # CLI tool
├── PackageShader.MsBuild/   # MSBuild task
├── Tests/                   # Test suite
└── AssemblyToProcess/       # Test assemblies (various scenarios)
```

## NuGet Packages

Three packages are published:
- `PackageShader` - Core library for programmatic use
- `PackageShaderTool` - .NET global tool (CLI)
- `PackageShader.MsBuild` - MSBuild task for automatic shading at build time
