# Version Management Guide

M3Undle now uses separate version files for the independently released pieces of the system.

## Version Files

`src/Directory.Build.props` imports one version file per source project:

| Project | Version file | Release identity |
| --- | --- | --- |
| `M3Undle.Core` | `src/CoreVersion.props` | Private Core NuGet package version |
| `M3Undle.Web` | `src/WebVersion.props` | Web/container product version |

The initial values may match, but they should not be treated as one shared version. Core can ship package updates without forcing a Web/container version bump, and Web can ship product updates without publishing a new Core package.

Common build metadata still lives in `src/Directory.Build.props`:

- `Company`
- `Product`
- `RepositoryUrl`
- `BuildDateUtc`
- `BuildNumber`
- `IncludeSourceRevisionInInformationalVersion`

## Core Package Version

Edit `src/CoreVersion.props` when publishing a new `M3Undle.Core` package.

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0-alpha.7</Version>
    <PackageVersion>$(Version)</PackageVersion>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
  </PropertyGroup>
</Project>
```

Rules:

- `Version` and `PackageVersion` identify the NuGet package.
- Keep `AssemblyVersion` stable within a major release line to reduce binary binding churn.
- Use `core-v*` tags for Core package releases, for example `core-v1.0.0-alpha.7`.
- External consumers such as `m3undle-cli` should pin exact Core package versions.
- Web does not pin a Core package version because it uses an in-repo `ProjectReference`.

## Web Product Version

Edit `src/WebVersion.props` when releasing the Web/container product.

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0-alpha.7</Version>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
  </PropertyGroup>
</Project>
```

Rules:

- Use Web/container release tags such as `v1.0.0-alpha.7`.
- Web continues to reference Core source directly through `ProjectReference`.
- Web releases can include Core source changes without publishing a Core package unless external consumers need those changes.

## CLI Product Version

The CLI now lives in the separate `m3undle-cli` repo. That repo owns its CLI version file and product release tags, and consumes Core through a pinned `PackageReference` to `M3Undle.Core`.

## Semantic Versioning

Use SemVer for Core package versions:

- `PATCH`: bug fixes only, no public API breaks.
- `MINOR`: backwards-compatible additions.
- `MAJOR`: breaking public API or behavior changes.
- Prerelease labels: `alpha.N`, `beta.N`, `rc.N`.

Web product versions can follow the same format, but their bump cadence is product-driven rather than tied to Core package releases.

Examples:

- `1.0.0-alpha.7`: next alpha package or product release.
- `1.0.0-beta.1`: beta candidate.
- `1.0.0`: first stable release.
- `1.1.0`: additive Core API release or product feature release.
- `2.0.0`: breaking Core API release or product breaking release.

## CI And Build Overrides

Local and CI builds may still override version properties explicitly:

```bash
dotnet build src/M3Undle.Core/M3Undle.Core.csproj -p:Version=1.0.0-alpha.8 -p:PackageVersion=1.0.0-alpha.8
dotnet publish src/M3Undle.Web/M3Undle.Web.csproj -p:Version=1.0.0-alpha.8
```

Use overrides sparingly. The checked-in props file should reflect intentional release versions before tagging.

## Checking Versions

Core package:

```bash
dotnet pack src/M3Undle.Core/M3Undle.Core.csproj --configuration Release --no-build --output artifacts/packages
```

The package filename and nuspec should reflect `src/CoreVersion.props`.

## Related Files

- `src/Directory.Build.props` - Common metadata and conditional version imports.
- `src/CoreVersion.props` - Core NuGet package version.
- `src/WebVersion.props` - Web/container product version.
- `docs/spec/core_public_api.md` - Core package surface and compatibility policy.
