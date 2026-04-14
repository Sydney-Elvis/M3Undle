# Beta Validation Checklist

> **This is a sign-off gate, not a test procedure.**
> Detailed test steps and per-mode scenarios live in `m3undle-lab/docs/SRV2_CLIENT_CHECKLIST.md`.
> Mark items here once the corresponding lab checklist section is fully passed.

Legend: `[ ]` not run | `[x]` passed | `[!]` failed / investigate

---

## Core Validation

These items are not client-specific and are not covered by the srv2 lab checklist.

- [ ] VOD / Series counts match UI and exported playlist stats
- [ ] HDHR endpoints (`lineup.json`, `lineup.xml`, `lineup.m3u`) reflect correct state after channel reorder and guide-number overrides
- [ ] IPTVnavtor / macOS player — M3U playlist loads, channels play, group filtering is respected

---

## DVR Client Validation

Each row represents a full pass of the corresponding section in `m3undle-lab/docs/SRV2_CLIENT_CHECKLIST.md`.

### Jellyfin
- [ ] M3U mode — setup, playback, EPG, channel overrides
- [ ] HDHR mode — setup, playback, EPG, MPEG-TS delivery, post-restart lineup
- [ ] HLS browser session — plays and counts against provider stream cap

### Emby
- [ ] M3U mode — setup, playback, EPG, channel overrides
- [ ] HDHR mode — setup, playback, EPG, MPEG-TS delivery, post-restart lineup

### NextPVR
- [ ] M3U mode — setup, playback, EPG
- [ ] HDHR mode — setup, playback, EPG, retune semantics

### Plex
> Requires Plex Pass. Test environment must have an active Plex Pass subscription.
- [ ] HDHR device added and channel scan completes
- [ ] Guide data matches channels
- [ ] Live playback starts
- [ ] Retune on same virtual tuner works
- [ ] Tuner exhaustion handled predictably (provider cap enforced)

### Xtream / TiviMate
- [ ] TiviMate (or IPTV Smarters) connects via Xtream credentials — playlist loads, stream plays

---

## Cross-Client Scenarios

These require multiple clients active simultaneously. Covered in detail in the lab checklist cross-client section.

- [ ] Same channel shared across two clients — one upstream session (verified via `/status/streams`)
- [ ] Provider stream cap enforced under concurrent load (including HLS sessions)
- [ ] Active profile switch — clients see new lineup on next scan, in-progress streams not abruptly terminated
- [ ] Endpoint security — auth enabled/disabled behaves correctly for all client types
