# Alpha 5 Validation Checklist

Legend: `[ ]` not started | `[x]` passed | `[!]` failed / investigate

## Core Lineup Management

- [x] Channel reorder — Number Manager inline mode on Channels page; ▲ ▼ swap and direct number editing; bulk Apply All
- [x] Custom `tvg-id` override per channel — lock-gated field in channel edit dialog; warning on unlock; stored as `tvg_id_override` in DB; applied at snapshot build time
- [x] Channel number pinning and stable reordering
- [x] Full channel numbering rules — conflict avoidance (global pin skip), skip-and-continue fill, overflow at 9000+, group evaluation order via `sort_override` (see `../design/NUMBERING_RULES.md`)

## Dynamic and Review Workflow

- [x] Dynamic groups for rotating/event feeds
- [x] New channels inbox / review queue

## Refresh / Source Control

- [x] Configurable refresh schedule in Settings UI
- [x] Real-time trigger + manual refresh with status — validate end-to-end: trigger from UI, confirm status updates while running, confirm completion feedback
- [x] Dashboard and `/status` resolve lineup state from the active profile only
- [x] Active profile switching shows requested, refreshing, completed, and failed/last-known-good feedback states
- [x] Multi-profile status regressions covered by tests

## HDHomeRun / DVR Client Scenario

- [x] Ensure HDHomeRun endpoints still valid after lineup changes
- [x] Verify `lineup.json`, `lineup.xml`, `lineup.m3u` in final lineup state
- [x] Tuner admission rules and retune semantics intact

## Compatibility and Behavior

- [x] HLS playback for JavaScript/browser clients (GeneratedHls compatibility layer + HlsManifestRewriter)
- [x] CORS support for external network access
- [x] VOD / Series counts match UI & exported stats
- [x] IPTVnavtor groups and MacOS behavior validated

## Post-Review Hardening (2026-04-10)

- [x] Guard `AddDatabaseDeveloperPageExceptionFilter()` so it only runs in Development
- [x] Fix `GeneratedHlsSessionManager` pump-task exception handling so FFmpeg reader failures are observed and logged
- [x] Fix `GeneratedHlsSessionManager` manifest-timeout cleanup so timed-out FFmpeg processes are stopped immediately
- [x] Distinguish timeout cancellation from user cancellation in `SnapshotRefreshService`
- [x] Move strike-store cooldown checks under the admission lock in `ChannelSessionManager`
- [x] Make `ChannelSessionManager.RemoveIfClosedAsync()` atomic
- [x] Add a transaction around `ProfilesPageService.DeleteProfileAsync()`
- [x] Block disabled profiles from being activated in `ProfilesPageService.SetProfileActiveAsync()`
- [x] Harden active-profile schema handling

## Post-Review Medium Coverage (2026-04-10)

- [x] Decide/document Xtream account-info password behavior (`user_info.password` is now redacted)
- [x] Add baseline tests for `GeneratedHlsSessionManager`
- [x] Add baseline tests for `SnapshotChangeClassifier`
- [x] Add baseline tests for `EventChannelClassifier`
- [x] Expand negative/edge-case coverage for `HlsManifestRewriter`
- [x] Expand failure-path/concurrency coverage for `DownstreamNotificationService`

## Documentation

- [x] Update roadmap & user docs with Alpha 5 scope and status (dashboard redesign, profiles UX, terminology, HLS/CORS)
