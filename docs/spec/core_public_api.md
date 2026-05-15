# M3Undle Core Public API Surface

This document defines the intended public surface for `M3Undle.Core` as a private NuGet package. Web stays in this repo with a `ProjectReference`; external consumers such as the extracted CLI and the planned M3U Analyzer consume this surface through `PackageReference`.

## Ownership Rules

Core owns deterministic, product-neutral IPTV behavior:

- M3U parsing, playlist models, live/VOD/series classification, group discovery, group filtering, and group-file merge/validation
- XMLTV parsing, EPG catalogue records, EPG coverage helpers, EPG channel matching, and event/PPV classification
- provider channel normalization, stream URL normalization, Xtream URL construction, provider header parsing, and URL redaction
- shared config schema loading and reusable environment placeholder utilities

Core does not own:

- CLI command parsing, terminal rendering, progress UI, stdout/stderr formatting, exit-code mapping, file output adapters, or fetch orchestration
- Web persistence, ASP.NET endpoint behavior, auth, CORS, OpenAPI, EF Core, Blazor UI, streaming sessions, HLS/proxy runtime behavior, or runtime caches

## Supported API Areas

These namespaces are the accepted package surface for the first private Core package line.

| Namespace | Public types | Purpose |
| --- | --- | --- |
| `M3Undle.Core` | `AppBuildInfo`, `CoreException`, `ExitCodes` | Shared build metadata and current error contract used by existing adapters. |
| `M3Undle.Core.Configuration` | `ConfigLoader`, `MediaConfig`, `ProfileConfig`, `InputsConfig`, `EndpointConfig`, `FiltersConfig`, `OutputConfig` | Shared YAML/JSON config schema used by CLI-style entry points and service configuration docs. |
| `M3Undle.Core.Env` | `EnvFileLoader`, `UrlSubstitutor` | `.env` loading and `%VAR%` placeholder substitution helpers. |
| `M3Undle.Core.M3u` | `M3uEntry`, `PlaylistDocument`, `PlaylistParser`, `LiveClassifier` | Playlist model, parser, and content classification. |
| `M3Undle.Core.Groups` | `PlaylistGroupDiscovery`, `GroupsFileMerge`, `GroupsFileMergeResult` | Group discovery and merge behavior for curated group files. |
| `M3Undle.Core.Filtering` | `PlaylistGroupFilter`, `PlaylistGroupFilterResult` | Group-based playlist filtering. |
| `M3Undle.Core.IO` | `GroupSelectionFile`, `GroupSelectionFile.GroupSelection`, `GroupsFileValidator`, `GroupsFileValidator.ValidationResult` | Group selection file parsing and validation. |
| `M3Undle.Core.Providers` | `ProviderRequestHeader`, `ProviderRequestHeaders`, `NormalizedProviderChannel`, `ProviderChannelNormalizer`, `XtreamProviderUrls` | Provider channel normalization, request header parsing, stream URL normalization, and Xtream URL construction. |
| `M3Undle.Core.Net` | `UrlRedactor` | URL redaction helpers for logs and diagnostics. |
| `M3Undle.Core.Epg` | `XmltvParser`, `EpgCatalogue`, `EpgChannelRecord`, `EpgProgrammeRecord`, `EpgChannelIndex`, `EpgChannelMatch`, `EpgChannelMatchCandidate`, `EpgChannelMatcher`, `EpgCoverageAnalyzer` | XMLTV parsing, EPG catalogue modeling, channel matching, and coverage checks. |
| `M3Undle.Core.Events` | `EventChannelClassifier`, `EventChannelClassification` | Event and PPV channel classification. |
| `M3Undle.Core.MpegTs` | `MpegTsBoundaryScanner`, `MpegTsPacketBatch`, `MpegTsStartupKind` | MPEG-TS startup/boundary detection helpers. |

## Excluded From Core Package Surface

The following behavior is intentionally not Core public API:

- source fetching and HTTP response validation for command execution
- default CLI `HttpClient` construction
- atomic output file writing for CLI commands
- terminal progress rendering and diagnostics formatting
- unused channel catalogue abstractions that have no current Web/CLI/Core tests

The CLI owns the command fetch/write adapters. They live in the separate `m3undle-cli` repo and are internal implementation details there.

## Compatibility Policy

- Treat every `public` type and member in `src/M3Undle.Core` as package contract.
- Prefer additive changes over signature changes.
- Patch releases must not break public API.
- Minor releases may add public APIs but must remain backwards compatible.
- Major releases are required for intentional breaking public API changes after the first stable package.
- During alpha, reduce accidental surface before enabling package validation against an accepted baseline.
- `CoreException` and `ExitCodes` are retained for existing adapter compatibility. New Core APIs should prefer product-neutral failures and let adapters map those failures to product-specific exit codes where practical.

## Validation Expectations

- `tests/M3Undle.Core.Tests` must reference only `M3Undle.Core`.
- `M3Undle.Core` must not reference Web, CLI, ASP.NET, EF Core, Blazor, MudBlazor, Spectre.Console, or terminal UI types.
- `dotnet pack src/M3Undle.Core/M3Undle.Core.csproj --configuration Release` must produce a package containing only the intended Core API surface.
- Once the first accepted baseline package is published, enable package validation so accidental public API breaks fail CI.
