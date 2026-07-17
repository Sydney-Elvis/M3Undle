# Changelog

All notable changes to M3Undle are documented here. Newest release at the top.

---

## [Unreleased]

### Backup and restore

- Added portable configuration backups: a single checksummed `.m3undle-backup` archive containing all configuration, mappings, users, and credentials (still encrypted under the host's key ring), excluding regenerable history/cache tables
- Added staged restore with automatic rollback: preflight validation (checksum, format/schema version, encryption key ID + fingerprint match), a validated rollback checkpoint before the database is touched, an atomic swap at startup before migrations run, and automatic rollback to the checkpoint if anything fails after that point
- Added a Backup & Restore section under Settings (create, list, download, upload, delete, restore), a `/api/v1/backups` and `/api/v1/restore` admin API, an optional weekly backup schedule with count-based retention, and a headless `M3UNDLE_RESTORE_FILE` recovery path for containers that can't reach the UI
- Added a shared destructive-operation lock so backup, restore, and encryption key rotation can never run concurrently
- Staged restores can be cancelled (UI button and `DELETE /api/v1/restore/stage`) and expire automatically if not confirmed within 15 minutes, so an abandoned staged restore can never fire on an unrelated restart
- Restoring signs all users out immediately: every security stamp is rotated in the restored database and stamps are now validated on every authenticated request
- Hardened archive handling: entry-count and size caps with hard byte limits during extraction (zip-bomb defense), a free-disk-space preflight check covering staging plus the rollback checkpoint, and a per-upload size ceiling enforced at the service, endpoint, and UI layers
- Uploaded archives are validated immediately on upload, retained separately from created backups (uploads can no longer evict real backups), and the archive a staged restore references is always protected from retention cleanup
- Creating a backup is blocked while a restore is staged; backup/restore initiation endpoints are rate limited; the upload endpoint requires an `X-Requested-With` header as CSRF protection
- A failed or rolled-back restore is surfaced as Degraded through the health endpoints, so a failed headless `M3UNDLE_RESTORE_FILE` restore is visible to Docker healthchecks and external monitors
- Stale snapshot and HLS work artifacts from the pre-restore timeline are cleared after a successful restore
- Backups in the Settings list gained Validate and View Report actions

### Database migration strategy

- **Breaking with prior convention:** the `Alpha_Schema` migration baseline is now frozen as shipped in v1.0.0-beta.1, and all schema changes from now on are additive EF Core migrations — starting with `AddBackupSchedule` in this release. Editing the shipped baseline in place silently stranded existing databases (migrations would no-op and the app would fail on missing columns); with this change, in-place container upgrades from beta.1–beta.6 databases now work and are covered by tests
- Alpha-era databases (v1.0.0-alpha.7 and earlier) remain unsupported: back up your configuration details, wipe the data directory, and reconfigure
- Restore now structurally verifies a backup's schema (tables and columns) against the running binary's own migrations after migrating it forward, and refuses the restore before anything is modified if they diverge

### Testing

- Added 46 new tests covering backup pruning/exclusions, manifest checksums, archive tampering, encryption key fingerprint validation, restore rollback via fault injection, environment-driven restore one-shot semantics, the destructive-operation lock, the weekly schedule, the frozen-baseline tripwire, the beta.6-style in-place upgrade path, staged-restore cancel/expiry, archive entry-count limits, security-stamp rotation, retention isolation between uploads and created backups, and the restore health check

---

## [v1.0.0-beta.6] — 2026-07-15

Beta 6 hardens VOD/series direct-relay playback against stale provider state and improves per-client visibility with liveness health chips, better client-type identification, and a cleaner subscriber reconnect/supersede model.

### VOD and series direct-relay hardening

- Hardened stream resolution to reject a request with 503 when a cached snapshot's provider is known but disabled for the active profile, instead of silently degrading to an unmonitored, uncapped direct relay
- Direct relays now use the resolved descriptor's stream URL, so the relayed URL always matches what was used for admission and monitor reporting
- Forwarded the full set of conditional request headers (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`) upstream on direct relays, alongside the existing `If-Range`/`Content-Range`/`Accept-Ranges`/`ETag` byte-range handling
- Added provider identity propagation and snapshot fallback tracking so VOD and series playback appears correctly in active sessions, provider streams, and Recently Ended

### Client status and reconnect handling

- Added per-client liveness (Live/Slow/Stalled) driven by media-segment activity, surfaced as health chips on the Streams page
- Added VOD/series info (`get_vod_info` plus episode metadata) and a configurable Roku AAC-LC audio transcode option for HE-AAC channels
- Added an HLS session restart button to the Streams page
- Fixed client-type detection so fewer clients are reported as "unknown"
- Fixed client IP addresses displaying as IPv4-mapped IPv6 instead of plain IPv4
- Changed subscriber supersession during reconnect to require route, IP, and user agent to all match, so different clients behind the same IP (e.g. NAT/CGNAT) no longer supersede each other; added `Superseded` disconnect diagnostics, extended reconnect state to cover the backoff period, and fixed reconnect-delay cancellation to shut down cleanly
- Fixed `SubscriberConnection` to implement `IDisposable` and release `_serverCompletionCts` on removal, closing a cancellation-token-source handle leak on every subscriber disconnect

### Documentation

- Simplified vulnerability reporting in `SECURITY.md` to go through GitHub

### Testing

- Added 27 new tests covering VOD/series direct-relay range and conditional-header handling, provider-disabled resolution, subscriber supersession and reconnect-backoff diagnostics, client-type resolution, IPv4 display formatting, and per-client liveness health
- Added an integration smoke test covering Xtream VOD/series endpoints

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.6
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.5] — 2026-07-12

Beta 5 adds DTS-aware overlap trimming to provider reconnects, encryption key rotation, and VOD/series visibility on the stream monitor.

### Adaptive recovery overlap trim

- Added wrap-aware 33-bit MPEG-TS timestamp math and video PES DTS extraction to the boundary scanner, giving the recovery path a reliable measure of how far a reconnected stream has rewound
- Added DTS-aware overlap trimming: when a provider reconnect replays 0–180s of already-delivered video (a common behavior on reconnect, e.g. AMC), M3Undle now holds through the replay and resumes at the first IDR at or after the pre-failure position instead of splicing the replayed IDR into the live HLS window and evicting viewer playback position
- Gated the two existing forced-retune fallbacks so they can't fire while a trim is active or mid-replay; abandoning a trim on budget expiry falls back to the standard first-IDR resume, never a retune
- Added `EnableRecoveryOverlapTrim` (on by default), plus configurable trim hold, byte budget, and max rewind limits under `ReconnectOptions`
- Added `RecoveryOverlapTrimmed` and `RecoveryOverlapTrimAbandoned` diagnostic events, and extended the "Recovery resumed" log with trim outcome, rewind seconds, and trimmed byte count
- Fixed a latent boundary-scanner issue where packets still containing old IDR bytes after a positive IDR detection could re-report a stale safe boundary

### Encryption key rotation

- Added the ability to rotate the secret encryption key used to protect stored Xtream and provider credentials, without requiring re-entry of existing secrets
- Added a SQLite backup step ahead of rotation so a rotation can be safely rolled back
- Documented the rotation workflow and previously undocumented Xtream fields in the Docker and database schema docs

### VOD and series stream visibility

- Added tracking of VOD and series streams against subscriber cap limits, alongside existing live-channel tracking
- Fixed VOD and series streams not appearing on the stream monitor page (fixes #134)

### Fixes

- Fixed a validation message mismatch in `StreamingOptionsValidator`: the check allowed any value down to 1 tick through despite the message claiming a 1ms minimum; the check now matches the documented constraint

### Testing

- Added 7 new Core tests covering wrap-aware timestamp math, DTS extraction, PTS-only fallback, malformed-header handling, IDR/DTS association, and the stale-IDR regression
- Added 5 new integration scenarios covering trimmed rewind replay, slow 1× replay abandonment, forward jumps, unrelated timelines, and audio-only streams
- Added regression coverage for encryption rotation, secret encryption, and SQLite backup
- Added regression coverage for VOD/series cap tracking and stream monitor visibility

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.5
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.4] — 2026-07-10

Beta 4 is a focused adaptive stream health release. It removes false and dead signals from the channel health classifier that could push a channel to Unstable and force every viewer off on a benign disconnect, and it retires an eager forced-retune mechanism in favor of the existing evidence-based recovery path.

### Adaptive stream health

- Stopped counting a client disconnect shortly after a recovery as evidence of channel instability. A benign viewer disconnect (channel change, app backgrounding) is not reliable evidence, and it previously drove channels to Unstable and triggered a forced-retune loop
- Stopped miscounting the internal FFmpeg relay's own reconnect-to-ring-buffer as a client abort, a second source of the same false signal
- Removed the eager forced-downstream-retune mechanism that disconnected every viewer on an Unstable channel before a recovery was even attempted. Recovery failures are now handled entirely by the existing evidence-based safe-start path, so an Unstable channel that finds a valid recovery point resumes normally instead of forcing a reconnect
- Removed a dead in-session health-escalation code path left over from the beta.2 Auto-relay change, which had no effect since Cautious and Stable channels have been treated identically
- Added long-term persistence for subscriber slow-client and resync events, giving visibility into resync/slow-client behavior over time
- Removed retune indicators from the stream monitor and session details dialog that no longer reflected real behavior now that the eager retune mechanism is gone

### Testing

- Added regression coverage locking in that client aborts, in any volume, never drive channel health off Stable
- Added coverage for the internal relay subscriber carve-out, the evidence-based recovery-failure path, and persisted slow-client health events

### Build and dependencies

- Updated `Microsoft.OpenApi`, `MSTest`, `MudBlazor`, `Scalar.AspNetCore`, `SQLitePCLRaw.lib.e_sqlite3`, and `YamlDotNet` to their latest minor/patch versions

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.4
ghcr.io/sydney-elvis/m3undle:beta
```

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
