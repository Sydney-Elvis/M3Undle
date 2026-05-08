## M3Undle v1.0.0-alpha.6 (Alpha)

M3Undle is a self-hosted lineup manager for large streaming provider catalogs, focused on explicit control, stable output, and DVR-friendly publishing.

This alpha milestone focuses on practical reliability: stronger stream handling for unstable providers, better browser/HLS behavior, first-class observability, Xtream provider detection, and documentation that better matches how people are actually running M3Undle.

---

## What's in this release

### Streaming and HLS reliability
- Hardened shared live stream handling so regular MPEG-TS clients and Generated HLS clients can coexist more predictably
- Improved HLS session accounting, cleanup, retune behavior, and stream monitor visibility
- Added stronger internal relay handling for Generated HLS playback, including MPEG-TS relay paths and safer fallback behavior
- Improved handling for shaky providers with upstream cooldowns, content-stall detection, clean relay support, and MPEG-TS startup boundary handling
- Fixed cases where HLS or relay sessions could hold stale client counts or make a parent shared stream look idle while playback was still active

### Provider workflow and Xtream compatibility
- Added Xtream-capable endpoint detection when adding providers, with clearer mode guidance in the UI
- Added provider account and playlist-expiration visibility where the upstream exposes it
- Improved support for Xtream/Roku-style endpoint behavior and stream URL handling
- Added per-provider refresh scheduling so provider cadence can be tuned without forcing every profile to follow the same timing

### Observability and diagnostics
- Added Prometheus-compatible metrics with configurable access modes, local CIDR controls, and token-based scraping
- Added liveness, readiness, and JSON health endpoints for containers, reverse proxies, and uptime checks
- Added authenticated diagnostics APIs for provider refreshes, streams, lineup state, and EPG behavior
- Added system event tracking and UI badging so stream/provider problems are easier to see without digging through logs first

### Lineup, guide, and data reliability
- Added the consolidated alpha.6 schema migration with new observability, system event, provider, and scheduling data
- Improved EPG matching, coverage analysis, and guide handling around refresh/build workflows
- Fixed guide carry-forward behavior so build-only refreshes do not keep serving an empty guide when cached EPG data is available
- Added database indexes and service-side cleanup in areas expected to matter more as catalogs grow

### Documentation, UI, and project hygiene
- Reworked the README with current alpha status, Docker guidance, screenshots, endpoint examples, and troubleshooting notes
- Added dedicated observability documentation covering metrics, probes, diagnostics APIs, labels, and Prometheus examples
- Updated Docker and GUI documentation around generated HLS storage, endpoint security, refresh schedules, stream proxy settings, and HDHomeRun behavior
- Continued the CLI/Core split so shared parsing, filtering, provider, EPG, and stream utilities live in the core project instead of the web layer

### Testing
- Expanded focused coverage around stream sharing, Generated HLS, upstream connector behavior, MPEG-TS boundary scanning, observability, refresh scheduling, system events, Xtream/provider handling, and EPG matching
- Added regression coverage for several alpha.6 stream lifecycle and guide refresh fixes
- Fixed a timing-sensitive CI failure in the HDHR-only subscriber keepalive test caused by a shared cancellation token being used for both subscriber lifetime and an intentional observation delay

---

## Container Images

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-alpha.6
ghcr.io/sydney-elvis/m3undle:alpha
```

> `alpha` is a rolling tag and points to the latest alpha release.

---

## Known alpha limitations

Still alpha: streaming, HLS, Xtream, HDHomeRun, and observability behavior are much stronger in this release, but broader beta validation across DVR and player clients is still ongoing. Plex and Emby Live TV/DVR testing remain limited by their paid subscription requirements, and provider-specific stream quirks may still need follow-up tuning before beta.

---

## Contributor

@jake1164

**Full Changelog:** https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.5...v1.0.0-alpha.6
