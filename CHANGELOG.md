# Changelog

All notable changes to M3Undle are documented here. Newest release at the top.

---

## [v1.0.0-beta.9] — 2026-08-16

Fixes a startup hang that could leave M3Undle permanently unable to start after a hard restart, with no error to explain it — if your container has been sitting at "unhealthy" and never coming up, this release very likely fixes it, and your data was never damaged. Also cleans up provider categories that vanish for good — a genre a provider stopped serving, a dropped package — which used to linger forever in Channel Mapping as 0-channel "missing" rows, while protecting groups you've asked to be notified about (like NFL between seasons) from being deleted out from under you.

### Fixed

- **M3Undle could hang forever on startup after being force-stopped.** Before applying database migrations, Entity Framework records a lock in an internal table. If the container is killed while that lock is held — for example, restarting during a long provider or series import — the lock is left behind, and this is a documented limitation of the SQLite provider. Every later start then waited *indefinitely* for a lock that would never be released: no error, no log output, the container up but never listening. M3Undle now detects a lock older than five minutes and clears it automatically at startup. Locks newer than that are left alone, so the protection against two instances migrating at once still works
- Databases affected by this were never corrupted, only blocked — nothing needs to be rebuilt or re-imported

### Startup diagnostics

- Migrations that run longer than 30 seconds now report progress every 30 seconds, naming the database and the likely cause, instead of going silent

### Channel group cleanup

- A provider group that's been inactive with zero channels for 14+ days is now removed automatically during the next refresh, along with its filter settings, channel selections, and custom-group links
- A group with notifications turned on is never auto-removed — instead it shows an "empty — remove?" prompt in Channel Mapping, so seasonal groups (e.g. NFL, due back next season) aren't deleted without a chance to keep them
- Root-caused a related but distinct gap: the earlier "mixed" legacy group upgrade (beta.8) correctly re-links a group to its live successor when one appears, but had no path for groups that never get one — this release closes that gap

### Testing

- Added regression coverage for the abandoned migration lock, including a stale lock, a lock young enough to still be live, an undated lock, and a full migration run that must complete rather than stall
- Added coverage for the empty-group grace-period boundary, automatic-vs-tracked pruning behavior, and the cascade delete on manual removal

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.9
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.8.1] — 2026-08-08

A diagnostic release prompted by an install that started but never finished coming up, leaving nothing in the log to explain why. It also corrects the storage figures on the System Resources page, which were measuring the wrong filesystem on Linux.

### Startup diagnostics

- M3Undle now writes a three-line environment summary at startup — runtime and architecture, then the filesystem type, writability and free space behind `/data`, the log directory and `/config`, then the database and write-ahead log sizes. It runs *before* the database is opened, so a container that never finishes starting still reports where it got to and what it was working with
- Database migrations are now logged as they run — how many are pending, which ones, and how long they took. Previously this step was completely silent, so a slow or stalled upgrade was indistinguishable from a container that had failed somewhere else entirely

### Fixed

- The System Resources page reported free space for the wrong filesystem on Linux. It measured the container's root (overlay) filesystem rather than the volume actually holding your data, logs, or generated HLS — so anyone with `/data` on a separate disk, a network share, or a Docker named volume was reading figures for a completely different device. The critically-low-space warning was being evaluated against those wrong numbers too
- The System Resources page now tracks the database's volume as well. It previously went unmonitored entirely if the log directory had been pointed somewhere other than `/data`
- A missing `.env` file is no longer reported as a warning. The file is optional, and the old message claimed environment substitution was unavailable when in fact process environment variables continue to work — and take priority over `.env` regardless

### Testing

- Added coverage for mount-point resolution, including the nested-path and shared-name-prefix cases behind the storage fix

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.8.1
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.8] — 2026-08-06

Beta 8 adds a Movies & Series Catalog you can browse straight from a provider, a live System Resources page for diagnosing CPU/memory/storage pressure, and closes out a run of channel-mapping bugs — duplicate channel numbers, an auto-picker blind to unopened groups, and unbuilt changes that were easy to miss before leaving the page.

### Movies & Series Catalog

- New Catalog page (Channels → Catalog) lets you browse an Xtream provider's Movies and Series categories — titles, poster artwork, plot, cast, and season/episode breakdowns for series — before deciding what's worth including; it's inspection only and doesn't change what's published
- Search titles across categories and filter by Movies/Series chips; open a series to see its full episode list
- Series episode data for M3U-sourced providers is now persisted the same way Xtream series already were, fixing series/VOD content that wouldn't reliably persist or resume
- Hardened series sync against concurrent-write database lockups, capped how often the lineup refreshes during large series imports (previously every 2,500 inserts), and added a hard timeout ceiling to background metadata fetches so a stalled request can no longer wedge an import indefinitely

### Channel Mapping build safety

- The Build Output button now turns amber with a marker, and the Mapped panel shows a warning icon, whenever there are unbuilt changes; navigating away in-app or closing the tab now prompts you to build, leave anyway, or cancel
- Fixed duplicate channel numbers shipping in build output when two channels ended up pinned to the same number
- Fixed the channel-number auto-picker to check numbers assigned anywhere in the profile, not just in groups already expanded during the current session
- The Channels page now sorts by channel number instead of build order
- Fixed profile chips and a delete dialog that looked hung but was actually still working

### System Resources page

- New System Resources page (linked from the footer) shows CPU, memory, storage, and streaming-capacity readings with a best-effort diagnosis of what's constraining M3Undle, plus rolling graphs and, on Linux hosts with cgroup v2, an advanced-signals card
- Fixed a regression introduced during development and cleaned up process-handle and cancellation-token-source leaks in the resource sampler

### Security

- Artwork fetching now resolves the image URL's host via DNS and rejects it if any resolved address falls in a loopback, private, link-local, multicast, or CGNAT range, closing an SSRF path
- Catalog browsing now respects a provider group's Active flag consistently across item listing, item detail, and artwork endpoints

### Other fixes

- Legacy "mixed" content-type groups (from before Live/VOD/Series were split) are now treated as live until the next refresh upgrades them, instead of disappearing from snapshot and EPG output
- Container images now also publish for arm64

### Testing

- Added coverage for the Catalog page service, Linux resource-fact parsing, resource-constraint diagnosis, the resource-facts service, channel numbering, and the legacy "mixed" group upgrade path

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.8
ghcr.io/sydney-elvis/m3undle:beta
```

---

## [v1.0.0-beta.7] — 2026-07-28

Beta 7 lets you back up and restore your entire M3Undle configuration, fixes a subtler kind of playback glitch on reconnect, launches a full documentation site, and makes it much clearer on first run when a profile has no output because setup isn't finished yet.

### Backup and restore

- You can now create a full backup of your M3Undle configuration — providers, channel mappings, users, and credentials — as a single encrypted file, and restore it later, including onto a fresh install
- Restoring is safe by design: M3Undle checks a backup thoroughly before touching anything, keeps a rollback point, and automatically reverts if anything goes wrong partway through
- Manage everything from a new Backup & Restore section in Settings: create, download, upload, delete, and restore backups, plus an optional automatic weekly backup that cleans up old copies on its own
- A restore can also be triggered without opening the UI — for example on container startup — which is useful for automated or headless recovery
- Backups now show Validate and View Report actions in Settings, so you can confirm a backup is good without actually restoring it
- Restoring signs everyone out afterward, since the restored data may include different credentials than what was previously active
- Uploaded backup files are checked as soon as they're uploaded and are kept separate from your regular backups, so they can't be accidentally cleaned up
- Backup and restore can no longer be started while another backup, restore, or key rotation is already in progress, and a failed restore is now visible through M3Undle's health status

### More reliable playback on reconnect

- Some providers silently restart their stream in the background and replay a few seconds of already-shown video without ever actually dropping the connection to M3Undle. Previously this kind of hidden replay could slip past M3Undle's recovery logic and cause a brief rewind or glitch in playback. M3Undle now recognizes this pattern as well and holds the stream until it catches back up, so viewers no longer see the replay
- Fixed two related edge cases that could still let a replayed segment through under certain conditions

### Upgrading between releases

- In-place upgrades between beta releases (beta.1 through beta.7) are now fully supported and tested, thanks to a change in how M3Undle applies database updates internally
- Installs from the earlier alpha releases (alpha.7 and before) still cannot be upgraded in place — back up your configuration, wipe the data directory, and set up fresh
- When restoring a backup, M3Undle now double-checks that the backup's data matches what the current version expects, and will refuse the restore rather than risk leaving your database in a broken state

### Documentation

- Published a full documentation site covering getting started, everyday guides, client-specific setup notes, troubleshooting, and background concepts
- Simplified the README to a quick overview, with everything else now living on the new site

### Dashboard and setup guidance

- The dashboard now clearly explains when there's no output because no provider has been set up yet, instead of showing confusing empty playlist/guide links
- Deleting a profile now updates the dashboard right away instead of leaving stale information displayed
- Made the "Setup Required" banner more visible so it's not mistaken for routine background text
- Fixed a mismatched label on the What's On This Week page's empty state

**Container images**

```text
ghcr.io/sydney-elvis/m3undle:v1.0.0-beta.7
ghcr.io/sydney-elvis/m3undle:beta
```

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

[v1.0.0-beta.9]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.8.1...v1.0.0-beta.9
[v1.0.0-beta.8.1]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.8...v1.0.0-beta.8.1
[v1.0.0-beta.8]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.7...v1.0.0-beta.8
[v1.0.0-beta.7]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.6...v1.0.0-beta.7
[v1.0.0-beta.3]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.2...v1.0.0-beta.3
[v1.0.0-beta.2]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-beta.1...v1.0.0-beta.2
[v1.0.0-beta.1]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.7...v1.0.0-beta.1
[v1.0.0-alpha.7]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.6...v1.0.0-alpha.7
[v1.0.0-alpha.6]: https://github.com/Sydney-Elvis/M3Undle/compare/v1.0.0-alpha.5...v1.0.0-alpha.6
