# Roadmap

## Alpha 1 — Functional Pass-Through
**Goal:** Core pass-through service proven and usable for daily driver testing on LAN.

Core complete:
- Provider configuration, active selection, group preview
- Snapshot-based serving with last-known-good behavior on refresh failure
- M3U + XMLTV compatibility endpoints (`/m3u/m3undle.m3u`, `/xmltv/m3undle.xml`)
- Stream proxy relay — `/stream/<streamKey>` (relay only, clients never see provider URLs)
- Stable stream keys derived from channel properties (not DB IDs)
- On-demand refresh trigger + background scheduled refresh (fixed interval)
- Local file support for M3U/XMLTV sources
- Serilog integration + log viewer UI
- Version info in UI + Docker smoke coverage

Status: Alpha 1 baseline is complete. Active development is in Alpha 2.

Not in Alpha 1: configurable refresh schedule, advanced mapping transforms,
multiple EPG sources, buffering, HDHR emulation.

---

## Alpha 2 — Filtering, Mapping & Output Shaping
**Goal:** Users can shape their lineup — filter, rename, reorder, and restructure
output groups independently of the provider's group structure.

### Filtering & Mapping
- Group inclusion/exclusion rules
- Channel filtering (keyword, regex, group-based)
- Channel rename, reorder, custom tvg-id override
- Channel number assignment (initial)

### Output Group Rules
Controls `group-title` in the M3U output — determines how channels appear in DVR
guide categories (Jellyfin, Plex, Emby, player apps).
- Create custom output groups (independent of provider group names)
- Assign channels from any provider group(s) to a custom output group
- Rename provider groups at the output layer
- Output group ordering and preview

### Multiple EPG Sources
- Add additional XMLTV/EPG source URLs or local files per provider
- Merge multiple XMLTV sources into a single guide feed
- Source priority rules (prefer source N when a channel appears in multiple)
- tvg-id cross-source mapping (map a channel to EPG data from a different source)

### Scheduling & Dashboard
- Configurable refresh schedule (interval or scheduled times) via Settings UI
- Filtered channel count (n / total) and mapped channel count on Dashboard

Current implementation note: group include/exclude, channel selection, output group
renaming/merging, and initial channel numbering are already in place. Remaining A2
work is focused on richer transforms, EPG mapping/merge, auth, and scheduling.

---

## Alpha 3 — Buffering & DVR Integration
**Goal:** Stream buffering and DVR auto-discovery.

- FFmpeg buffering support
- VLC buffering support
- Number of tuners setting
- HDHomeRun device emulation (`/discover.json`, `/lineup.json`, `/lineup_status.json`)
  — allows Plex, Emby, Jellyfin to auto-discover M3Undle as a network tuner
- Connection limiting

---

## Alpha 4 — Security & Stabilization
**Goal:** Complete endpoint protection and stabilization work needed before Beta closeout.

- Endpoint security: secret token embedded in URL path
  (not headers — Media Players cannot set custom headers)
- Token generation and rotation UI
- Any remaining core items before Beta

---

## Beta — Feature Finalization
**Goal:** All core functionality implemented, stable, and documented.
No major feature additions after Beta entry.

- New channels inbox (review and approve newly discovered channels before publishing)
- Dynamic groups (auto add/drop for rotating sports or event feeds)
- Provider switch assistance (diff view + optional manual channel mapping hints)
- Full channel numbering rules (start ranges, pinned numbers, overflow)
- Security review
- Performance validation (large providers, 89k+ channels)
- Documentation complete

---

## Deferred / Future
- Change history / audit log
- Multi-provider output blending
- Additional diagnostic tooling

