# M3Undle Development Plan

This is the canonical planning document for M3Undle. It replaces the old roadmap and development checklist.

Legend: `[ ]` not started | `[~]` in progress | `[x]` done

## Product Goal

A self-hosted IPTV lineup manager that:
- connects to M3U and XMLTV sources
- publishes client-friendly endpoints (M3U, XMLTV, stream proxy, HDHomeRun-compatible endpoints)
- preserves stable channel identity and practical numbering behavior
- gives users direct control over what gets published

Primary published endpoints:
- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`
- `/stream/<streamKey>`

## Current Release State

- Alpha 1: complete
- Alpha 2: complete
- Alpha 3: in progress
- Alpha 4: planned, with some HDHomeRun groundwork already landed early
- Alpha 5: planned
- Beta: hardening and release prep

## Release Milestones

### Alpha 1 — Functional Pass-Through
Goal: Core pass-through service proven and usable for daily-driver LAN testing.

Status: Complete.

#### Persistence
- [x] providers, profiles, profile_providers, fetch_runs
- [x] provider_groups, provider_channels
- [x] snapshots (profile-scoped), stream_keys
- [x] canonical_channels, channel_sources, epg_channel_map (schema present for future use)
- [x] EF migration generated and applied against SQLite
- [x] Partial unique index `(provider_id, provider_channel_key) WHERE provider_channel_key IS NOT NULL`
- [x] Delete behavior matrix verified

#### Provider Configuration UI
- [x] List / add / edit providers
- [x] Playlist URL + optional EPG URL
- [x] Active toggle (one active provider at a time)
- [x] Associate provider to profile
- [x] Inline profile creation from provider edit flow
- [x] Auto-create profile on import
- [x] Last refresh status and snapshot timestamp display

#### Provider Preview UX
- [x] Preview groups from latest successful refresh
- [x] Channel counts per group + sample channels
- [x] Refresh & Preview (live fetch, in-memory parse, no DB channel upsert)
- [x] Read-only preview

#### Snapshot Fetcher
- [x] On-demand refresh trigger
- [x] Background scheduled refresh (fixed interval)
- [x] Fetch playlist + EPG and parse via `PlaylistParser`
- [x] Build channel index in-memory from parsed provider channels
- [x] Write snapshot files under `snapshots/{profile}/{snapshotId}/`
- [x] Insert snapshot record with staged -> active lifecycle
- [x] Update fetch runs
- [x] Preserve last-known-good on failure

#### Published Endpoints
- [x] `GET /m3u/m3undle.m3u`
- [x] `GET /xmltv/m3undle.xml`
- [x] `GET /stream/<streamKey>` relay only, never HTTP 302
- [x] `GET /status`
- [x] `GET /health`

#### API + UI Wiring
- [x] Provider CRUD API
- [x] Provider status API
- [x] Preview endpoint
- [x] Snapshot refresh trigger API
- [x] Dashboard
- [x] Providers page CRUD + preview wiring

#### Settings, Logging, Observability, Ops
- [x] Settings page placeholder
- [x] Structured logging with UI log viewer
- [x] Version visible in UI
- [x] Real-time UI refresh/event wiring
- [x] Local file support for M3U/XMLTV
- [x] Dockerfile, volumes, runbook, smoke coverage

#### Test Coverage
- [x] Provider validation
- [x] Snapshot success/failure handling
- [x] Preview output
- [x] Stream timeout behavior

### Alpha 2 — Filtering, Mapping & Output Shaping
Goal: Users can shape their lineup independently of provider structure.

Status: Complete.

#### Filtering
- [x] Group inclusion/exclusion rules
- [x] Channel filtering by keyword
- [x] Channel filtering by regex/glob
- [x] Channel filtering by group
- [x] Filter preview in channel mapping UI

#### Mapping & Output Rules
- [x] Initial channel number assignment
- [x] Group rename at output layer
- [x] Create custom output groups
- [x] Assign channels from any provider group(s) to custom output groups
- [x] Dashboard output counts for live, VOD, and series

### Alpha 3 — Security
Goal: Lock down UI and client-facing endpoints before broader DVR exposure.

Status: Complete.

#### GUI Authentication
- [x] ASP.NET Identity login flow is present
- [x] Cookie/session management is wired
- [x] Authentication gate is controlled by `M3UNDLE_AUTH_ENABLED`

#### Endpoint Security
- [x] DB schema for endpoint credentials and bindings
- [x] Settings API/service for endpoint credential management
- [x] Credential validation for protected client endpoints
- [x] Query-string and Basic-auth based client access flow

### Alpha 4 — Stream Proxy, DVR Integration & EPG
Goal: Native shared stream proxy, HDHomeRun compatibility, and stronger guide-source handling.

Status: Planned.

#### Stream Proxy (Shared Live Streaming)
- [ ] Native .NET MPEG-TS shared stream proxy — no FFmpeg required
- [ ] One upstream provider connection per active live channel session, fanned out to many subscribers
- [ ] In-memory ring buffer for late joiners (byte-bounded, default 4 MiB per session, hard cap 32 MiB)
- [ ] Upstream stall detection and minimal reconnect (default 30s stall timeout, 75s outage window)
- [ ] Basic slow-subscriber eviction (queue-full disconnect)
- [ ] Source strike cooldown after retry exhaustion to prevent retry storms (default 5m, in-memory only)
- [ ] Explicit route split: `/live`, `/stream`, `/tune`, `/hdhr/tune` → shared session; `/movie`, `/vod`, `/series` → direct relay
- [ ] Streaming observability endpoints: `/status/streams`, `/status/streams/clients`, `/status/streams/providers`
- [ ] Settings page controls: max concurrent sessions, buffer size per session, stall timeout, outage window, idle grace period, status retention window

#### DVR Integration (HDHomeRun Emulation)
- [~] Initial HDHomeRun compatibility groundwork is already present:
  `GET /discover.json`, `GET /lineup.json`, `GET /lineup_status.json`, discovery service, device identity, lineup rendering tests
- [ ] Number of tuners setting in user-facing configuration
- [ ] Connection limiting
- [ ] End-to-end validation with Plex, Emby, and Jellyfin

#### EPG Sources
- [ ] Additional XMLTV/EPG source URLs per provider
- [ ] XMLTV merge into one guide feed
- [ ] De-duplicate EPG entries by channel id
- [ ] Source priority rules across guide inputs
- [ ] Cross-source `tvg-id` mapping

### Alpha 5 — Remaining Features
Goal: Finish remaining lineup-management features before Beta hardening.

Status: Planned.

- [ ] Channel reorder (explicit sort position)
- [ ] Custom `tvg-id` override per channel
- [ ] Configurable refresh schedule in Settings UI
- [ ] New channels inbox / review queue
- [ ] Dynamic groups for rotating/event feeds
- [ ] Provider switch assistance
- [ ] Full channel numbering rules (see `../design/NUMBERING_RULES.md`)

### Beta — Hardening & Release Prep
Goal: No major feature additions. Stabilize, validate, and document.

Status: Planned.

- [ ] Security review
- [ ] Performance validation for large providers
- [ ] Bug fixes and polish
- [ ] Documentation complete and accurate

## Design Documents

- [ARCHITECTURE_MAP.md](../design/ARCHITECTURE_MAP.md)
- [DB_SCHEMA.md](../design/DB_SCHEMA.md)
- [HTTP_COMPATIBILITY.md](../design/HTTP_COMPATIBILITY.md)
- [LINEUP_RULES.md](../design/LINEUP_RULES.md)
- [NUMBERING_RULES.md](../design/NUMBERING_RULES.md)
- [stream_proxy_design.md](../design/stream_proxy_design.md)
- [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md)
