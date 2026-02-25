# M3Undle Development Checklist

Legend: `[ ]` not started | `[~]` in progress | `[x]` done

See [ROADMAP.md](ROADMAP.md) for milestone goals and scope definitions.

---

## Alpha 1 — Functional Pass-Through

### Persistence (SQLite)
- [x] providers, profiles, profile_providers, fetch_runs
- [x] provider_groups, provider_channels
- [x] snapshots (profile-scoped), stream_keys
- [x] canonical_channels, channel_sources, epg_channel_map (schema-present, future use)
- [x] EF migration generated and applied against SQLite
- [x] Partial unique index `(provider_id, provider_channel_key) WHERE provider_channel_key IS NOT NULL`
- [x] Delete behavior matrix verified

### Provider Configuration UI
- [x] List / add / edit providers
- [x] Playlist URL + optional EPG URL
- [x] Active toggle (one active provider at a time — partial unique index enforced)
- [x] Associate provider to profile (single-select dropdown)
- [x] Profile creation inline from edit form
- [x] Profile dropdown filters to unassociated profiles only
- [x] Auto-create profile on import (unique name with suffix on collision)
- [x] Show last refresh status and snapshot timestamp

### Provider Preview UX
- [x] Preview groups from latest successful refresh
- [x] Channel counts per group + first N channels (configurable sample)
- [x] Refresh & Preview (live fetch, in-memory parse, no DB channel upsert)
- [x] Read-only preview (no filtering)

### Snapshot Fetcher (Background Service)
- [x] On-demand refresh trigger
- [x] Background scheduled refresh (fixed interval — configurable schedule is A2)
- [x] Fetch playlist + EPG, parse via PlaylistParser
- [x] Build channel index in-memory from ParsedProviderChannel (no provider_channels DB upsert)
- [x] Write snapshot files: `snapshots/{profile}/{snapshotId}/`
- [x] Insert snapshot record (staged → active lifecycle)
- [x] Update fetch_runs
- [x] Preserve last-known-good on failure

### Compatibility Endpoints
- [x] GET /m3u/m3undle.m3u (extended M3U with tvg-id, tvg-name, tvg-logo, group-title)
- [x] GET /xmltv/m3undle.xml
- [x] GET /stream/`<streamKey>` (relay only — never HTTP 302)
- [x] GET /status
- [x] GET /health

### API + UI Wiring
- [x] Provider CRUD API (GET, POST, PUT, PATCH /enabled, PATCH /active)
- [x] Status API (GET /api/v1/providers/{id}/status)
- [x] Preview endpoint (GET + POST /refresh-preview)
- [x] Snapshot trigger API (POST /api/v1/snapshots/refresh → 202/409)
- [x] Dashboard (active provider, last refresh, active snapshot, output URLs + copy)
- [x] Providers page (CRUD + preview fully wired, IsActive chip, Set Active)

### Settings
- [x] Settings page (authentication placeholder with Alpha 2 label)

### Logging
- [x] Serilog integration (structured logging, file sink)
- [x] Logging level cleanup — streaming/operational logs at Info; developer detail at Debug
- [x] Log viewer page (tail of recent structured log entries in UI)

### Observability
- [x] Version info visible in UI (footer or dashboard)
- [x] Real-time UI push for all pages (Dashboard refresh status, active snapshot, provider state) via Blazor streaming / in-process event bus

### Local Source Support
- [x] Local file as M3U source (file path accepted in provider config, no URL required)
- [x] Local file as XMLTV/EPG source

### Packaging & Ops
- [x] Dockerfile (ASP.NET)
- [x] Volume mounts for DB + snapshots
- [x] Container runbook doc (`docs/dev/container.md`)
- [x] .gitignore: snapshot artifacts excluded
- [x] Docker smoke tests:
  - [x] curl /health → 200
  - [x] curl /m3u/m3undle.m3u → valid M3U
  - [x] stream relay via /stream/`<key>` (502 from upstream when provider stream is unreachable — no 302, security contract upheld)

### Tests
- [x] Provider validation
- [x] Snapshot success/failure handling
- [x] Preview endpoint output
- [x] Stream timeout behavior

---

## Alpha 2 — Filtering, Mapping & Output Shaping

**Extension architecture gate:** Define `IChannelFilter`, `IGroupFilter`, `IChannelTransform`
interfaces at the start of A2. These are the contracts Pro loads external implementations
against in A4. Do not build filtering as concrete classes without these interfaces first.

### Filtering
- [ ] Group inclusion/exclusion rules (which provider groups publish to output)
- [ ] Channel filtering: keyword match
- [ ] Channel filtering: regex match
- [ ] Channel filtering: group-based
- [ ] Filter preview (show what would be excluded before committing)

### Mapping & Channel Transform
- [ ] Channel rename (display name override at output)
- [ ] Channel reorder (explicit position in output)
- [ ] Custom tvg-id override (point a channel at a specific EPG entry)
- [ ] Channel number assignment (initial — full numbering rules in Beta)

### Output Group Rules
Output group rules control `group-title` in the M3U EXTINF tags — this is what
determines how channels are organized in Jellyfin, Plex, Emby, and player apps.
- [ ] Create custom output groups (independent of provider group names)
- [ ] Assign channels from any provider group(s) to a custom output group
- [ ] Rename provider groups at the output layer (no custom group needed)
- [ ] Output group ordering
- [ ] Preview: show resulting output group structure before publishing

### Multiple EPG Sources
The provider's own XMLTV is often incomplete or absent. Users need to supplement or
replace it with better data.
- [ ] Add additional XMLTV/EPG source URLs per provider (or local files)
- [ ] XMLTV merge: combine multiple sources into a single guide feed
- [ ] De-duplicate EPG entries by channel id across sources
- [ ] Source priority: if a channel appears in multiple EPG sources, prefer source N
- [ ] tvg-id cross-source mapping: map a channel to EPG data from a different source

### Dashboard
- [ ] Filtered channel count display (filtered n / total n)
- [ ] Mapped channel count display

### Authentication
- [ ] GUI login (username/password)
- [ ] Authentication toggle in Settings (enable/disable login requirement)
- [ ] Session management

### Refresh Scheduling
Fixed-interval background refresh is sufficient for A1. A2 adds user control.
- [ ] Configurable refresh interval via Settings UI (e.g. every 4h, 12h, 24h)
- [ ] Manual schedule times (e.g. run at 03:00 daily)

---

## Alpha 3 — Buffering & DVR Integration

### Buffering
- [ ] FFmpeg buffering (configurable binary path + options)
- [ ] VLC/CVLC buffering (configurable binary path + options)
- [ ] Buffer size setting
- [ ] Client connection timeout setting

### DVR Integration
- [ ] Number of tuners setting
- [ ] HDHomeRun device emulation (allows Plex, Emby, Jellyfin to auto-discover as a network tuner):
  - [ ] GET /discover.json
  - [ ] GET /lineup.json
  - [ ] GET /lineup_status.json
- [ ] Connection limiting

---

## Alpha 4 — Pro Hooks & Security

### Plugin Architecture
- [ ] Plugin loader (external assembly discovery, plugin manifest format)
- [ ] Extension contracts:
  - [ ] `ISettingsContributor` (add settings sections from plugin)
  - [ ] `IEndpointModule` (register additional endpoints from plugin)
  - [ ] `IUiTheme` (CSS/branding injection)
- [ ] Internal A2 filtering implementations confirmed against plugin contract surface

### Security
- [ ] Output endpoint protection: secret token embedded in URL path
  (not header-based — IPTV clients cannot set custom headers)
- [ ] Token generation and rotation UI

### Remaining Items
- [ ] Carry forward any unfinished A1-A3 items

---

## Beta — Feature Finalization

- [ ] New channels inbox (review and approve newly discovered channels before publishing)
- [ ] Dynamic groups (auto add/drop for rotating sports or event feeds)
- [ ] Provider switch assistance (diff view + optional manual channel mapping hints)
- [ ] Full channel numbering rules (start ranges, pinned numbers, overflow — see NUMBERING_RULES.md)
- [ ] Security review
- [ ] Performance validation (large providers, 89k+ channels)
- [ ] Documentation complete and accurate

