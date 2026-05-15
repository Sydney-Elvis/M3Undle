# M3Undle Core NuGet Release Guide

How to publish a new `M3Undle.Core` package version to GitHub Packages.

## When to publish a new package

Publish a new Core package only when external consumers (`m3undle-cli`, future M3U Analyzer) need the change. Web does not consume the package — it uses a `ProjectReference` to the in-repo Core source and never needs a package bump.

Do not publish a package just because a Web-only change landed in main.

## Version bump rules

Edit `src/CoreVersion.props`:

| Change type | Bump |
| --- | --- |
| Bug fix, no public API change | `PATCH` (e.g. `alpha.6` → `alpha.6.1` or `alpha.7`) |
| New public type or member, backwards-compatible | `MINOR` (e.g. `1.0.0` → `1.1.0`) |
| Removed or renamed public type/member, signature change | `MAJOR` (e.g. `1.0.0` → `2.0.0`) |
| Alpha/beta iteration | increment prerelease label (e.g. `alpha.6` → `alpha.7`) |

Keep `AssemblyVersion` stable within a major line (`1.0.0.0` throughout all `1.x` releases) to reduce binding churn in consuming repos.

## Step-by-step release

### 1. Bump the version

Edit `src/CoreVersion.props`:

```xml
<Version>1.0.0-alpha.7</Version>
<PackageVersion>$(Version)</PackageVersion>
```

Commit the bump to a branch and merge it into main before tagging.

### 2. Run local validation

```powershell
dotnet restore
dotnet build src/M3Undle.Core/M3Undle.Core.csproj --configuration Release --no-restore
dotnet test tests/M3Undle.Core.Tests/M3Undle.Core.Tests.csproj --configuration Release --no-restore
dotnet pack src/M3Undle.Core/M3Undle.Core.csproj --configuration Release --no-build --output artifacts/packages
```

Verify the output filename matches the intended version (e.g. `M3Undle.Core.1.0.0-alpha.7.nupkg`).

### 3. Tag the release

Use the `core-v` prefix. The workflow validates that the tag version matches `CoreVersion.props` before publishing.

```bash
git tag core-v1.0.0-alpha.7
git push origin core-v1.0.0-alpha.7
```

Pushing the tag triggers the `Publish Core NuGet` workflow automatically.

### 4. Verify the publish and compatibility check

Check the `Publish Core NuGet` workflow run in GitHub Actions. Confirm these steps passed:

- `Pack Core` — should emit `M3Undle.Core.<version>.nupkg` and `.snupkg`.
- `Push Core package` — should exit without error.
- `Trigger CLI compatibility check` — fires `compatibility.yml` in `m3undle-cli` via `CLI_DISPATCH_TOKEN`.

Then verify the package appears in the GitHub Packages registry under the `Sydney-Elvis` org at:
`https://github.com/Sydney-Elvis/M3Undle/packages`

Check the `m3undle-cli` Actions tab for the `Core Compatibility Check` run. It restores, builds, and tests the CLI against the candidate Core version automatically. Wait for it to pass before proceeding.

### 5. Bump the pinned version in `m3undle-cli`

Once the compatibility run is green, open a PR in `m3undle-cli` to update the pinned default in `src/CliVersion.props`:

```xml
<M3UndleCoreVersion Condition="'$(M3UndleCoreVersion)' == ''">1.0.0-alpha.7</M3UndleCoreVersion>
```

The standard CI run will restore and test against the newly pinned version. Do not merge until it passes. Do not allow Dependabot to auto-merge Core package bumps.

### 6. Add the baseline version (first stable release only)

Once the first accepted package is published and the API surface is stable, add this to `src/M3Undle.Core/M3Undle.Core.csproj`:

```xml
<PackageValidationBaselineVersion>1.0.0-alpha.7</PackageValidationBaselineVersion>
```

After this, `dotnet pack` will fail if the new package breaks the established public API without a major version bump.

## Tag naming

| Tag prefix | Workflow triggered |
| --- | --- |
| `core-v*` | Publishes a Core NuGet package only |
| `v*` | Publishes a Web/container Docker image only |

These are intentionally separate so Core and Web releases can ship at different cadences.

## Manual dry run (build/test/pack without publish)

Use `workflow_dispatch` in GitHub Actions and set `publish: false`. This builds, tests, packs, and uploads the package artifact without pushing to GitHub Packages. The CLI compatibility check is not triggered in dry-run mode. Use it to verify the package shape before tagging.

## Private feed authentication for local development

To restore `M3Undle.Core` locally in `m3undle-cli` without CI:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/Sydney-Elvis/index.json" \
  --name github \
  --username "<github-username>" \
  --password "<token-with-read-packages>" \
  --store-password-in-clear-text
```

Do not commit tokens. Grant `m3undle-cli` repository-level package access in GitHub package settings so its CI can restore using its own `GITHUB_TOKEN`.

## Related files

- `src/CoreVersion.props` — Core NuGet package version.
- `src/WebVersion.props` — Web/container product version (independent).
- `.github/workflows/publish-core-nuget.yml` — Publish workflow; requires `CLI_DISPATCH_TOKEN` secret (PAT with `workflow` scope on `m3undle-cli`).
- `docs/spec/core_public_api.md` — Public API surface and compatibility policy.
- `docs/spec/version_management.md` — Version files and SemVer rules.
- `m3undle-cli/.github/workflows/compatibility.yml` — CLI compatibility check triggered after each Core package publish.
