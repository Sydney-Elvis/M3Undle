# Version Management Guide

This document explains how build versions are managed in the M3Undle project.

## Current Approach: Centralized Versioning

All projects in the solution share a common version defined in `src/Version.props` (imported by `src/Directory.Build.props`).

### Version Properties

- **Version**: NuGet package version (e.g., `0.40.0`)
- **AssemblyVersion**: Strong-name assembly version (e.g., `0.40.0.0`)
- **FileVersion**: File version for Windows Explorer (e.g., `0.40.0.0`)

### How to Update Version

1. **Edit `src/Version.props`**
2. Update the `<Version>` property (and optionally `AssemblyVersion` and `FileVersion`)
3. Build or publish - all projects will use the new version

```xml
<PropertyGroup>
  <Version>0.41.0</Version>
  <AssemblyVersion>0.41.0.0</AssemblyVersion>
  <FileVersion>0.41.0.0</FileVersion>
</PropertyGroup>
```

## Alternative Approaches

### Option 1: Automatic Git-Based Versioning (Advanced)

Use a tool like **MinVer** or **GitVersion** to automatically generate versions from Git tags.

#### MinVer Example

1. Add package to your projects:
```bash
dotnet add package MinVer --version 6.0.0
```

2. Update the version properties:
```xml
<PropertyGroup>
  <MinVerTagPrefix>v</MinVerTagPrefix>
  <MinVerMinimumMajorMinor>0.40</MinVerMinimumMajorMinor>
  <MinVerVerbosity>minimal</MinVerVerbosity>
</PropertyGroup>
```

3. Create a Git tag:
```bash
git tag v0.41.0
```

Version will be automatically calculated from tags and commits.

### Option 2: CI/CD Version Injection

Pass version as a build parameter in your CI/CD pipeline:

```bash
dotnet build -p:Version=0.41.0-alpha.1
dotnet publish -p:Version=0.41.0 -p:FileVersion=0.41.0.$(Build.BuildId)
```

Example for GitHub Actions:
```yaml
- name: Build
  run: dotnet build -c Release -p:Version=${{ github.ref_name }}
```

### Option 3: Manual Version Per Project

If you need different versions per project, add version properties back to individual `.csproj` files.
They will override the version values imported from `src/Version.props`.

```xml
<!-- src/M3Undle.Cli/M3Undle.Cli.csproj -->
<PropertyGroup>
  <Version>1.0.0</Version>
</PropertyGroup>
```

## Checking Current Version

### In Code

```csharp
using System.Reflection;

var version = Assembly.GetExecutingAssembly()
    .GetName()
    .Version;
    
Console.WriteLine($"Version: {version}");
```

### From Command Line

```bash
# Check assembly version
dotnet exec src/M3Undle.Cli/bin/Release/net10.0/bndl.dll --version

# Or inspect DLL properties
dotnet --info
```

### From Published Binary

```bash
# Windows
bndl.exe --version

# Linux/macOS
./bndl --version
```

## Semantic Versioning

We follow [Semantic Versioning 2.0.0](https://semver.org/):

- **MAJOR** version: Incompatible API changes
- **MINOR** version: Add functionality (backwards-compatible)
- **PATCH** version: Bug fixes (backwards-compatible)

Examples:
- `0.40.0` ? `0.41.0` (new features added)
- `0.41.0` ? `0.41.1` (bug fixes)
- `0.41.0` ? `1.0.0` (stable release with breaking changes)

### Pre-release Versions

For pre-release builds, append a suffix:
- `0.41.0-alpha`
- `0.41.0-beta.1`
- `0.41.0-rc.2`

```xml
<Version>0.41.0-beta.1</Version>
```

## Best Practices

1. **Update version before release** - Don't forget to bump the version in `src/Version.props`
2. **Tag Git commits** - Tag releases in Git for traceability:
   ```bash
   git tag -a v0.41.0 -m "Release version 0.41.0"
   git push origin v0.41.0
   ```
3. **Keep versions synchronized** - All projects should generally share the same version
4. **Document changes** - Update CHANGELOG or release notes when bumping version
5. **Automate in CI/CD** - Consider automated version bumping in your build pipeline

## Troubleshooting

### Version not updating after build

1. Clean the solution:
   ```bash
   dotnet clean
   ```
2. Rebuild:
   ```bash
   dotnet build
   ```

### Different projects showing different versions

Check for version overrides in individual `.csproj` files. Remove them to use the centralized version.

### Assembly version conflicts

If using strong-named assemblies, consider only updating `Version` and `FileVersion`, keeping `AssemblyVersion` constant for binary compatibility.

## Related Files

- `src/Version.props` - Central version configuration
- `src/Directory.Build.props` - Imports `Version.props` for all projects under `src/`
- `src/*/**.csproj` - Individual project files (versions inherited)
- `.git/refs/tags/` - Git version tags
