# Beta Validation Checklist

> **This is a release-validation record, not a test procedure.**
> The automated srv1 release gate is run manually during major-release
> development and immediately before tagging. Client checks on srv2 are ad hoc
> and are not required to complete every release.
> Detailed client steps live in `m3undle-lab/docs/CLIENT_APP_TESTING_CHECKLIST.md`.

Legend: `[ ]` not run | `[x]` passed | `[!]` failed / investigate

---

## Delivered in Beta 7

- [x] Portable backup and restore — encrypted archive creation, validation,
  upload/download, clean-install restore, rollback protection, scheduled
  backups, and headless restore
- [x] Backup/restore live round trip — 7/7 checks passed in the 2026-07-30
  srv1 release-gate run, including canonical state and restored M3U comparison
- [x] Full documentation site — getting started, everyday guides,
  client-specific notes, troubleshooting, concepts, and reference material
- [x] Simplified repository README pointing users to the documentation site

---

## Core Validation

These items are not client-specific and are not covered by the srv2 lab checklist.

- [ ] VOD / Series counts match UI and exported playlist stats
- [x] HDHR endpoints (`lineup.json`, `lineup.xml`, `lineup.m3u`) reflect correct state after channel reorder and guide-number overrides — covered by the 2026-07-30 srv1 run
- [ ] IPTVnavtor / macOS player — M3U playlist loads, channels play, group filtering is respected

---

## Ad Hoc Client Validation

These checks are retained for targeted compatibility work when a change affects
a client-facing surface. They are not an automated or mandatory Beta milestone.

### Jellyfin
- [ ] M3U mode — setup, playback, EPG, channel overrides
- [ ] HDHR mode — setup, playback, EPG, MPEG-TS delivery, post-restart lineup
- [ ] HLS browser session — plays and counts against provider stream cap

### NextPVR
- [ ] M3U mode — setup, playback, EPG
- [ ] HDHR mode — setup, playback, EPG, retune semantics

### Xtream / TiviMate
- [ ] TiviMate (or IPTV Smarters) connects via Xtream credentials — playlist loads, stream plays

---

## Cross-Client Scenarios

These require multiple clients active simultaneously. Covered in detail in the lab checklist cross-client section.

- [ ] Same channel shared across two clients — one upstream session (verified via `/status/streams`)
- [ ] Provider stream cap enforced under concurrent load (including HLS sessions)
- [ ] Active profile switch — clients see new lineup on next scan, in-progress streams not abruptly terminated
- [ ] Endpoint security — auth enabled/disabled behaves correctly for all client types
