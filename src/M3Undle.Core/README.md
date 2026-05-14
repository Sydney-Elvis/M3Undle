# M3Undle Core

`M3Undle.Core` contains product-neutral IPTV logic shared by M3Undle Web, the CLI, and external consumers such as the planned M3U Analyzer.

## What Core Owns

- M3U parsing, playlist models, live/VOD/series classification, group discovery, group filtering, and group-file merge/validation
- XMLTV parsing, EPG catalogue records, EPG coverage helpers, EPG channel matching, and event/PPV classification
- provider channel normalization, stream URL normalization, Xtream URL construction, provider header parsing, and URL redaction
- shared config schema loading and reusable environment placeholder utilities

## What Core Does Not Own

- Web persistence, ASP.NET endpoints, auth, CORS, OpenAPI, EF Core, Blazor UI, streaming sessions, HLS/proxy runtime behavior, or runtime caches
- CLI command parsing, terminal rendering, progress UI, stdout/stderr formatting, file output adapters, or fetch orchestration
- public NuGet distribution workflows

## Target Framework

`M3Undle.Core` targets `net10.0`.

## Package Feed

This package is intended for the private GitHub Packages feed owned by `Sydney-Elvis`.

External consumers should pin exact `M3Undle.Core` versions. The Web project remains in the main M3Undle repo and references Core with a `ProjectReference`.

## API Compatibility

Every public type and member in `M3Undle.Core` is treated as package contract.

- Patch releases should not break public API.
- Minor releases may add backwards-compatible APIs.
- Major releases are required for intentional breaking changes after the first stable package.
- Package validation is enabled during packing. Baseline comparison should be configured after the first accepted package baseline is published.

The accepted public surface is documented in `docs/spec/core_public_api.md` in the main repository.
