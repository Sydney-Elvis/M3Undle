# Changelog

All notable changes to M3Undle are documented here. Newest release at the top.

---

## [v1.0.0-beta.3] — 2026-06-30

Beta 3 is a streaming compatibility release focused on modern burst-buffering clients. It moves Roku, Android TV, ExoPlayer/Media3, IPTV Smarters, and similar clients onto generated HLS when needed, makes direct MPEG-TS delivery less fragile for clients that still use it, and fixes several Xtream compatibility issues found during real device testing.

### Client playback compatibility

- Added automatic HLS delivery for burst-buffering clients, including Roku, IPTV Smarters, ExoPlayer, Media3, Dalvik, and okhttp-based Android apps
- Added stable external HLS manifest routes for both M3U-style stream keys and Xtream path-auth stream IDs, backed by the existing generated-HLS session manager
- Redirected compatible clients from raw MPEG-TS stream URLs to generated HLS while preserving endpoint credentials and shared session behavior
- Honored explicit `.m3u8` Xtream stream requests even when endpoint security is disabled, so HLS requests no longer fall through to TS delivery
- Fixed Roku Xtream playback by allowing TS requests from burst-buffering clients to be upgraded to HLS. Fixes #122

### Direct MPEG-TS delivery

- Reworked slow-client handling so transient backpressure no longer disconnects a subscriber on first queue overflow
- Added bounded resync behavior for backed-up TS subscribers: M3Undle now waits for the next clean TS boundary instead of dropping arbitrary bytes that corrupt the stream
- Added a write-stall timeout to detect dead or wedged client sockets separately from healthy buffering behavior
- Started TS subscribers from a safe PAT/PMT plus IDR boundary when available, reducing startup and resync corruption risk
- Increased default subscriber queue capacity and added explicit slow-client grace and write-stall settings

### Xtream compatibility

- Rebuilt Xtream stream ID assignment as a collision-free, Brightscript-safe mapping below 10,000,000 so Roku clients do not render stream IDs in scientific notation
- Preserved backward-compatible resolution for older cached Xtream stream IDs so clients can keep playing until they refresh their channel list
- Fixed Xtream account-info responses to echo the submitted URL credentials when appropriate, without exposing internal M3Undle account passwords
- Fixed path-credential handling when endpoint security is disabled so route-embedded Xtream username and password remain available to downstream Xtream logic
- Added empty EPG envelopes for Xtream short-EPG actions instead of requiring a rendered lineup

### Stream monitor

- Added per-client delivery method visibility, distinguishing Raw TS, Remux TS, and HLS delivery
- Added connected client channel, source, and delivery chips so M3U, Xtream, HDHomeRun, generated HLS, and auto-upgraded clients are easier to identify
- Improved client detection for Smarters Pro, Roku, ExoPlayer, Media3, Android TV, Android apps, and HLS segment fetchers
- Marked clients that requested MPEG-TS but were automatically upgraded to HLS for compatibility

### Generated HLS

- Increased the default generated-HLS playlist window from 6 to 30 segments and retained more old segments to better match ExoPlayer's long buffer window
- Improved generated-HLS client tracking so app-level user agents can replace generic Dalvik/okhttp segment-fetch agents in the stream monitor
- Added FFmpeg `dump_extra` bitstream filtering to improve SPS/PPS header cadence for HLS output

### Testing

- Added coverage for burst-buffering user-agent detection, subscriber resync state, slow-client grace handling, Xtream authentication behavior, and Xtream stream ID assignment

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.3
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.2] — 2026-06-26

Beta 2 is a focused compatibility and stream-recovery release. It improves HDHomeRun-style discovery behavior, adds the Xtream Codes XMLTV compatibility endpoint expected by more clients, and tightens the adaptive stream health policy around unstable MPEG-TS channels.

### Xtream compatibility

- Added `/xmltv.php` as an Xtream Codes-style XMLTV EPG endpoint with the same query-string and form credential handling used by `/player_api.php` and `/get.php`
- Included `/xmltv.php` in the media-surface routing rules so clients can reach the endpoint without UI authentication
- Added endpoint coverage for both GET query-string credentials and POST form credentials

### HDHomeRun compatibility

- Stopped generating and publishing a random HDHomeRun `DeviceAuth` value when one is not needed
- Omitted empty `DeviceAuth` values from `discover.json` and SiliconDust discovery replies, matching client expectations more closely
- Kept existing HDHomeRun device identity validation while allowing identity files with no auth token

### Adaptive stream recovery

- Fixed Unstable channel policy so Unstable always requires a controlled downstream retune instead of waiting for additional abort thresholds
- Restored Auto relay behavior so Stable and Cautious channels use direct relay, while only Unstable channels select clean remux automatically
- Improved downstream retune diagnostics with the full health summary, including upstream failures, recovery counts, aborts after recovery, forced retunes, and TS sync loss
- Added regression coverage for the Unstable retune policy and the Cautious Auto relay decision

### MPEG-TS safe start

- Added regression coverage for a reconnect scenario where a channel stalls after the first MPEG-TS safe start, reconnects with no active subscribers, and must still emit a second safe-start event before a late subscriber attaches
- Verified late subscribers after reconnect receive TS-aligned data from the current stream generation instead of stale or empty startup bytes

### Build and dependencies

- Fixed build revision display so invalid or missing source revision values are ignored instead of shown as hashes
- Added `SOURCE_REVISION` build-arg support to the local compose build path
- Updated GitHub Actions workflows to `actions/checkout@v7`
- Updated `Scalar.AspNetCore` to 2.16.5
- Pinned `SQLitePCLRaw.lib.e_sqlite3` to 3.50.3

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.2
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.1] — 2026-06-16

Beta 1 is the first beta release following Alpha 7. It fixes two playback bugs that broke HLS-sourced channels for every client, refines the adaptive stream recovery introduced in Alpha 7 to stop a class of channels from retuning far more than necessary, and makes provider onboarding faster and more resilient for large or slow catalogs.

### HLS playback

Two separate issues combined to break every HLS-sourced channel (iptv-org, Samsung FAST, PBS, and similar), then persisted as an Electron-only failure after the first was fixed.

- Fixed HLS channels producing no video at all. An HTTP reconnect setting meant for continuous streams was being applied to HLS as well, where every playlist and segment fetch ends in a normal end-of-file; M3Undle treated each one as a failure and looped on the playlist instead of advancing through it
- Fixed Electron-based players, such as IPTVnator, failing once the above was fixed. They were being redirected to a generated HLS stream meant for browsers instead of receiving the raw MPEG-TS stream their player pipeline expects

### Adaptive stream recovery

- Fixed a stream-health tracking bug that caused some noisy channels to retune far more often than necessary — one channel that the recovery engine was already handling correctly still retuned 20 times in under three hours. Fixes #96
- Auto relay policy now also selects clean relay for Cautious channels, not just Unstable, catching more problem channels before they need a hard retune

### Provider onboarding

- Fixed slow or very large providers timing out before they finished loading. Providers now only time out when no data is being received rather than on total elapsed time, so large catalogs complete instead of failing. Fixes #105
- Series catalogs for Xtream providers now load in the background after a provider is added, making the initial add noticeably faster for large catalogs

### Channel mapping

- Fixed a provider group named "undefined" causing channels to disappear from the mapping page entirely instead of just showing as unmapped — affected iptv-org and similar providers
- Fixed a malformed M3U entry swallowing the channel listed after it, silently dropping that channel from the lineup. Fixes #107

### Dashboard

- Added a copy link for the Xtream endpoint URL to match the other published endpoint URLs. Fixes #104

### Docker and health

- Fixed a newly deployed, not-yet-configured container reporting itself unhealthy to Docker; the dedicated health endpoint still correctly reports degraded until setup is complete, so monitoring isn't fooled, but compose no longer errors out on first boot. Fixes #103
- Fixed HDHomeRun tuner count defaulting to 1 when tuner count isn't tied to stream tracking

### Documentation

- Refreshed README screenshots for the dashboard, add-provider flow, channel search, filters, events, and stream health pages

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.1
ghcr.io/sydney-elvis/m3undle:beta
```

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

[v1.0.0-beta.3]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.2...v1.0.0-beta.3
[v1.0.0-beta.2]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.1...v1.0.0-beta.2
[v1.0.0-beta.1]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.7...v1.0.0-beta.1
[v1.0.0-alpha.7]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.6...v1.0.0-alpha.7
[v1.0.0-alpha.6]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.5...v1.0.0-alpha.6
