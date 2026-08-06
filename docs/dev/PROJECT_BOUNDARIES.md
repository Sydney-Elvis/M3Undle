# Architecture and Project Boundaries

## Project Context

- Runtime: .NET 10, C# 13, ASP.NET Core 10
- UI: Blazor Server with Interactive Server rendering and MudBlazor 8.x
- Database: SQLite via EF Core 10, with migrations in `src/M3Undle.Web`
- Architecture: single process hosting the Blazor UI, REST API, compatibility endpoints, and background services

## Project Boundaries

Code and test changes should preserve the project boundaries established for the CLI/Core/Web split.

### Core

`src/M3Undle.Core` owns product-neutral logic that can be reused by M3Undle Web, the CLI, and the planned M3U Analyzer.

Behavior belongs in Core when it is deterministic and does not depend on UI, persistence, ASP.NET, terminal rendering, or Web runtime state. Examples include:

- M3U parsing, playlist models, group discovery, group filtering, and group-file merge/validation
- live/VOD/series classification
- provider channel normalization, stream URL normalization, Xtream URL construction, header parsing, and URL redaction
- XMLTV parsing, EPG catalogue records, EPG coverage helpers, EPG channel matching, and event/PPV classification
- reusable environment/URL utilities

Core must not reference:

- `M3Undle.Web`
- `M3Undle.Cli`
- ASP.NET Core endpoint/request/response types
- EF Core entities, DbContext, migrations, or persistence services
- Blazor/MudBlazor UI types
- Spectre.Console or terminal UI types

### Web

`src/M3Undle.Web` owns the web product and must remain stable and secure.

Behavior belongs in Web when it involves:

- Blazor UI and page services
- ASP.NET endpoints, request/response behavior, auth, CORS, OpenAPI, cookies, endpoint credentials, or security filters
- EF Core entities, migrations, DbContext queries, persistence, and database cleanup
- provider secret encryption/decryption and Web configuration services
- snapshot orchestration, refresh scheduling, streaming sessions, HLS generation, proxying, and runtime caching
- mapping Core models to database entities or API contracts

Do not move Web persistence, endpoint, auth, or streaming runtime behavior into Core unless there is an explicit design decision first.

### CLI

`src/M3Undle.Cli` is a separable command-line adapter and should stay thin.

CLI owns:

- command-line parsing and option validation
- stdout/stderr rendering, usage text, diagnostics, and exit-code mapping
- terminal progress/status UI
- file and network orchestration for commands

CLI should call Core for reusable playlist, provider, group, XMLTV, EPG, and classification behavior.

### Tests

Tests follow the same boundaries:

- `tests/M3Undle.Core.Tests` tests Core APIs only and must not reference Web or CLI projects.
- `tests/M3Undle.Web.Tests` tests Web behavior, integration with persistence/endpoints/security/UI-facing services, and Web adapters around Core.
- `tests/M3Undle.Cli.Tests` tests CLI command behavior, diagnostics, option parsing, and adapter wiring. Pure algorithms belong in Core tests.

When moving behavior into Core, move or add the corresponding pure tests to `M3Undle.Core.Tests`, then keep Web/CLI tests focused on integration and wiring.

### Dependency Strategy

During the split, projects may use direct `ProjectReference` dependencies on Core. The longer-term direction is to publish Core as a private GitHub Packages NuGet package for the separated CLI repo and Analyzer (see `docs/spec/nuget_release_guide.md`).

Do not introduce a public NuGet publishing workflow or broader package ecosystem assumptions unless explicitly requested/discussed first.

## Product Invariants

These are load-bearing facts about the running system. Breaking one silently is the kind of change that looks fine in a diff and breaks in-place upgrades, security guarantees, or existing client compatibility.

- EF Core migrations are append-only. `20260605114014_Alpha_Schema` is a frozen baseline (shipped in v1.0.0-beta.1); never edit an already-shipped migration in place — databases that recorded it as applied would silently never receive the change, breaking in-place upgrades and backup restore. Every schema change gets a new migration via `dotnet ef migrations add`. `MigrationBaselineTests` enforces this with a pinned operation count on the baseline.
- Stream relay is a security contract: `/stream/<streamKey>` must relay and must never HTTP 302 redirect to a provider URL, because provider URLs embed credentials.
- Only one profile is active at a time (the default profile used for unqualified/legacy routes) — enforced by a partial unique index on `profiles.is_active = 1`, with `ProfilesPageService`/`ProviderApiEndpoints` clearing other profiles when one is activated. Providers have no such constraint: `Provider.Enabled` can be true for multiple providers at once, and `ProfileProvider` (with a `Priority` column) already supports binding multiple providers to one profile.
- Compatibility endpoints (`/m3u/`, `/xmltv/`, `/stream/`, `/health`, `/status`) are always anonymous.
- Snapshot files live at `{ContentRootPath}/Data/snapshots/m3undle/{snapshotId}/`.
- Output name is locked to `m3undle` in Core. Do not add code paths that change this.
- Snapshot refresh synchronizes discovered groups into `provider_groups` and live channels into `provider_channels`; VOD and series items remain in-memory and are not persisted as provider channels. Preview builds from in-memory `ParsedProviderChannel` data and does not mutate those discovery tables.
- Stream keys are derived from stable channel properties: `tvg-id` when present, otherwise `displayName`, a unit separator, and `streamUrl`. The input is SHA-256 hashed with `profileId` and truncated to 16 base64url characters. Do not use database-assigned IDs as key inputs.
- Refresh and preview (`RefreshPreviewAsync`) builds the preview from in-memory `ParsedProviderChannel` data. It does not upsert channels to the database; only the `fetch_runs` record is written.
- Importing a provider auto-creates a profile with the same name. Use `GetUniqueProfileNameAsync` if the name is taken.
- `profile_catalog_group_filters` decisions (`CatalogPageService.UpdateDecisionAsync`) are persisted but **not enforced**. `SnapshotBuilder.BuildChannelIndex` gates VOD/series passthrough only on the provider's `IncludeVod`/`IncludeSeries` switches (`excludedCatalogGroups` is accepted but currently unused) — do not assume saving a catalog group decision changes published output until the build-loop check is wired up.
- `CatalogPageService.GetArtworkAsync` is a security contract like the stream proxy: it resolves the artwork URL's host via DNS and rejects loopback/private/link-local/multicast/CGNAT-range addresses (`IsSafeRemoteHostAsync`/`IsBlockedAddress`) before fetching, caps response size at 5 MB, requires an `image/*` content type, and is scoped to catalog items linked to the requesting profile. Any change to this fetch path must preserve that SSRF guard — the endpoint takes a catalog item ID, never an arbitrary URL, specifically so a raw provider/attacker URL can never reach the fetch.
