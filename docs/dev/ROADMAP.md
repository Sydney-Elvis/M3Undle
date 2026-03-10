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

Status: Complete.

---

## Alpha 2 — Filtering, Mapping & Output Shaping
**Goal:** Users can shape their lineup — filter, rename, and restructure
output groups independently of the provider's group structure.

Status: Complete.

- Group inclusion/exclusion rules
- Channel filtering (keyword, regex/glob, group-based)
- Channel number assignment (initial)
- Create custom output groups (independent of provider group names)
- Assign channels from any provider group(s) to a custom output group
- Rename provider groups at the output layer
- Channel count in output (live, VOD, series breakdown) on Dashboard

---

## Alpha 3 — Security
**Goal:** GUI authentication and endpoint protection before DVR integration exposes the service more broadly.

### GUI Authentication
- GUI login (username/password)
- Authentication toggle in Settings (enable/disable login requirement)
- Session management

### Endpoint Security
- Output endpoint protection: secret token embedded in URL path
  (not headers — Media Players cannot set custom headers)
- Token generation and rotation UI

---

## Alpha 4 — Buffering, DVR Integration & EPG
**Goal:** Stream buffering, DVR auto-discovery, and supplemental EPG sources.

### Buffering
- FFmpeg buffering support
- VLC buffering support
- Buffer size setting
- Client connection timeout setting

### DVR Integration (HDHomeRun Emulation)
- Number of tuners setting
- HDHomeRun device emulation — allows Plex, Emby, Jellyfin to auto-discover M3Undle as a network tuner:
  - GET /discover.json
  - GET /lineup.json
  - GET /lineup_status.json
- Connection limiting

---

## Alpha 4 — Plugin Architecture & Security
**Goal:** Plugin loading infrastructure, extension contracts, and endpoint security.

- Plugin loader (external assembly discovery, plugin manifest format)
- Extension contracts: `ISettingsContributor`, `IEndpointModule`, `IUiTheme`
- Endpoint security: secret token embedded in URL path
  (not headers — Media Players cannot set custom headers)
- Token generation and rotation UI
- Any remaining foundational items before Beta

---

## Beta — Feature Finalization
**Goal:** All foundational functionality implemented, stable, and documented.
No major feature additions after Beta entry.

- Channel reorder (explicit sort position in output)
- Custom tvg-id override per channel
- Configurable refresh schedule via Settings UI (interval or scheduled times)
- New channels inbox (review and approve newly discovered channels before publishing)
- Dynamic groups (auto add/drop for rotating sports or event feeds)
- Provider switch assistance (diff view + optional manual channel mapping hints)
- Full channel numbering rules (start ranges, pinned numbers, overflow)

---

## Beta — Stabilization & Release Prep
**Goal:** No new features. All core functionality is complete by end of Alpha 5.
Beta is cleanup, hardening, and documentation only.

- Security review
- Performance validation (large providers, 89k+ channels)
- Bug fixes and polish
- Documentation complete

---

## Deferred / Future
- Change history / audit log
- Multi-provider output blending
- Additional diagnostic tooling
