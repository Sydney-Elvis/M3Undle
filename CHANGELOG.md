# Changelog

All notable changes to M3Undle are documented here. Newest release at the top.

---

## [v1.0.0-alpha.7] — 2026-06-05

Alpha 7 is the final alpha milestone. It delivers adaptive live stream recovery for noisy provider channels, a first-class relay policy per provider, significant interface polish across nearly every page, and the consolidated schema migration that closes out all alpha database changes. Beta testing begins after this release.

### Adaptive stream recovery

M3Undle now tracks per-channel stream health across sessions and uses that history to make smarter relay decisions.

- Channels are classified as **Stable**, **Cautious**, or **Unstable** based on observed disconnect and reconnect events
- Health classification persists in the database — it survives restarts and informs the next session startup
- Clean-watch duration is tracked after the last adverse event; a channel relaxes one health level after 30 minutes of clean playback
- At session startup, channel health is loaded before the first upstream connection so the relay decision is already informed before a single byte arrives
- When clean recovery from an MPEG-TS/H.264 boundary is not safe, M3Undle issues a hard controlled retune instead of continuing with corrupt data
- Cooldown policy improved to be less aggressive and avoid thrash after a single bad event

### Provider relay policy

Replaced the hidden per-provider "clean remux" toggle with an explicit, user-facing **Relay policy** setting.

- **Auto** — M3Undle decides: direct relay for Stable and Cautious channels, clean relay when channel health is Unstable
- **On** — always use clean relay regardless of health
- **Off** — always use direct relay regardless of health

Existing providers migrate automatically: prior `off` rows become `Auto`, prior legacy remux rows become `On`.

The relay policy, startup health classification, and relay decision reason are now visible on the stream monitor for every active session.

### Stream monitor improvements

- Transfer rates (bytes/sec) displayed per session and per connected client
- Startup health and relay decision reason shown for each active session
- Sessions display more detailed subscriber information

### Retune compatibility fix

A controlled downstream retune is now suppressed when an internal or Generated HLS relay subscriber is attached to the shared session. A Jellyfin or NextPVR retune no longer kills a concurrent IPTVnator or browser HLS stream that is still active.

### HDHomeRun page

New dedicated HDHomeRun page showing device identity, discovery endpoint, tuning endpoint, and configured tuner count. Makes manual client setup straightforward without hunting through settings.

### About page

New About page with application version, build date, and project links. Fixes #97.

### Dashboard and navigation

- Dashboard reorganized: active profiles are the primary focus, all published endpoint URLs grouped in one place
- Always-on side drawer for fast navigation between pages without reopening a menu
- Navigation highlights the currently active page
- Setup guidance built into the navigation flow to help new users move through provider add, group mapping, and publish in the right order
- Settings moved to the navigation bar for direct access

### Channel mapping UX

- New providers no longer mark all channels as needing review — only genuinely new channels are flagged
- Channel tracking progress shown clearly during the mapping workflow
- Group filter chips normalized with consistent colors and shapes for Pending, Include, Exclude, and New states
- Group counts and mapped/unmapped states clearer at a glance
- Fixed a case where adding a provider marked all existing channels and groups as needing review

### Profiles

- Fixed inactive provider issues incorrectly marking active providers as degraded on the Profiles page

### Settings

- Settings page reorganized to make each section easier to understand
- Authentication and Xtream endpoint settings fixed

### Logs

- Log search and type-based filtering added
- Actual error text now shown instead of the .NET array type name

### Database and performance

- Slow database load mitigations — the UI detects and displays when the database is not responding
- UI performance improvements between page transitions
- Fixed a bug where saving a snapshot could fail under certain conditions
- All alpha migrations (Alpha 1 through Alpha 7) consolidated into a single baseline migration, removing the startup migration repair path

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-alpha.7
ghcr.io/sydney-elvis/m3undle:alpha
```

---

## [v1.0.0-alpha.6] — 2026-04-26

Alpha 6 focuses on practical reliability: stronger stream handling for unstable providers, better browser/HLS behavior, first-class observability, Xtream provider detection, and documentation that better matches how people are actually running M3Undle.

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

- Added Prometheus-compatible metrics with configurable access modes (Disabled, LocalOnly, Token, Public), local CIDR controls, and token-based scraping
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
- Fixed a timing-sensitive CI failure in the HDHR-only subscriber keepalive test

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-alpha.6
ghcr.io/sydney-elvis/m3undle:alpha
```

---

[v1.0.0-alpha.7]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.6...v1.0.0-alpha.7
[v1.0.0-alpha.6]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.5...v1.0.0-alpha.6
