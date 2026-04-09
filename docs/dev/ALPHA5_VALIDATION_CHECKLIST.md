# Alpha 5 Validation Checklist

Legend: `[ ]` not started | `[x]` passed | `[!]` failed / investigate

## Core Lineup Management

- [x] Channel reorder — Number Manager inline mode on Channels page; ▲ ▼ swap and direct number editing; bulk Apply All
- [x] Custom `tvg-id` override per channel — lock-gated field in channel edit dialog; warning on unlock; stored as `tvg_id_override` in DB; applied at snapshot build time
- [x] Channel number pinning and stable reordering
- [x] Full channel numbering rules — conflict avoidance (global pin skip), skip-and-continue fill, overflow at 9000+, group evaluation order via `sort_override` (see `../design/NUMBERING_RULES.md`)

## Dynamic and Review Workflow

- [ ] Dynamic groups for rotating/event feeds
- [x] New channels inbox / review queue

## Refresh / Source Control

- [ ] Configurable refresh schedule in Settings UI
- [ ] Real-time trigger + manual refresh with status
- [ ] Persisted preferred refresh policy per profile
- [ ] Dashboard and `/status` resolve lineup state from the active profile only
- [ ] Active profile switching shows requested, refreshing, completed, and failed/last-known-good feedback states
- [ ] Multi-profile status regressions covered by tests

## HDHomeRun / DVR Client Scenario

- [ ] Ensure HDHomeRun endpoints still valid after lineup changes
- [ ] Verify `lineup.json`, `lineup.xml`, `lineup.m3u` in final lineup state
- [ ] Tuner admission rules and retune semantics intact

## Compatibility and Behavior

- [x] HLS playback for JavaScript/browser clients (GeneratedHls compatibility layer + HlsManifestRewriter)
- [x] CORS support for external network access
- [ ] VOD / Series counts match UI & exported stats
- [ ] IPTVnavtor groups and MacOS behavior validated

## Documentation

- [x] Update roadmap & user docs with Alpha 5 scope and status (dashboard redesign, profiles UX, terminology, HLS/CORS)
- [ ] Add release notes for Alpha 5 features
- [ ] Mark Beta transition criteria (after all Alpha checkboxes complete)
