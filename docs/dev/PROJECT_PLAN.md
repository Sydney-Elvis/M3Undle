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
- Alpha 3: complete
- Alpha 4: complete — stream proxy, HDHomeRun tuner-slot enforcement, and EPG sources implemented; all checklist items passed; DVR client validation (Plex/Emby/Jellyfin) moved to Beta
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

Status: Complete. All checklist items passed. End-to-end DVR client validation (Plex, Emby, Jellyfin) moved to Beta — see BETA_VALIDATION_CHECKLIST.md.

#### Stream Proxy (Shared Live Streaming)
- [x] Native .NET MPEG-TS shared stream proxy — no FFmpeg required
- [x] One upstream provider connection per active live channel session, fanned out to many subscribers
- [x] In-memory ring buffer for late joiners (byte-bounded, default 4 MiB per session, hard cap 32 MiB)
- [x] Upstream stall detection and minimal reconnect (default 30s stall timeout, 75s outage window)
- [x] Basic slow-subscriber eviction (queue-full disconnect)
- [x] Source strike cooldown after retry exhaustion to prevent retry storms (default 5m, in-memory only)
- [x] Explicit route split: `/live`, `/stream`, `/tune`, `/hdhr/tune` → shared session; `/movie`, `/vod`, `/series` → direct relay
- [x] Streaming observability endpoints: `/status/streams`, `/status/streams/clients`, `/status/streams/providers`
- [x] Settings page stream UI: full read/write configuration for all stream proxy tuning values (enable/disable, session limits, buffer sizing, reconnect behaviour); values persisted to DB and loaded at startup via `IConfigureOptions`; in-app restart trigger with "restart required" banner when saved settings differ from the active runtime; startup `IValidateOptions` validation rejects out-of-range config from both appsettings and DB; byte-field upper bounds enforced in UI (1 GiB per session, 16 MiB read chunk) and service validation

#### DVR Integration (HDHomeRun Emulation)
- [x] Initial HDHomeRun compatibility groundwork:
  `GET /discover.json`, `GET /lineup.json`, `GET /lineup_status.json`, discovery service, device identity, lineup rendering tests
- [x] Number of tuners setting in user-facing configuration
- [x] Connection limiting via HDHomeRun tuner-slot enforcement keyed by `VirtualTunerId` from endpoint binding; same-tuner retunes replace prior subscriber instead of consuming another slot
- [ ] End-to-end validation with Plex, Emby, and Jellyfin *(moved to Beta — see BETA_VALIDATION_CHECKLIST.md)*

#### EPG Sources
- [x] EPG source management UI + API (multiple sources per provider, test fetch, auto-map, manual mapping)
- [x] Additional XMLTV/EPG source URLs per provider
- [x] XMLTV merge into one guide feed
- [x] De-duplicate EPG entries by channel id
- [x] Source priority rules across guide inputs
- [~] Cross-source `tvg-id` mapping (via per-channel source mappings; canonical-channel mapping remains future work)

### Alpha 5 — Remaining Features
Goal: Finish remaining lineup-management features before Beta hardening.

Status: Planned. (Related issue seeds: #3, #4, #5, #6, #7, #8, #9)

- [ ] Channel reorder (explicit sort position) — #3
- [ ] Custom `tvg-id` override per channel
- [ ] Configurable refresh schedule in Settings UI
- [ ] New channels inbox / review queue
- [ ] Dynamic groups for rotating/event feeds
- [ ] Provider switch assistance
- [ ] Full channel numbering rules (see `../design/NUMBERING_RULES.md`)


> See the Alpha 5 validation checklist for concrete acceptance criteria:
> `docs/dev/ALPHA5_VALIDATION_CHECKLIST.md`

### Beta — Hardening & Release Prep
Goal: No major feature additions. Stabilize, validate, and document.

Status: Planned.

- [ ] Security review
- [ ] Performance validation for large providers
- [ ] Bug fixes and polish
- [ ] Documentation complete and accurate
- [ ] DVR client validation — Plex, Emby, Jellyfin (see [BETA_VALIDATION_CHECKLIST.md](BETA_VALIDATION_CHECKLIST.md))

## Design Documents

- [ARCHITECTURE_MAP.md](../design/ARCHITECTURE_MAP.md)
- [DB_SCHEMA.md](../design/DB_SCHEMA.md)
- [HTTP_COMPATIBILITY.md](../design/HTTP_COMPATIBILITY.md)
- [LINEUP_RULES.md](../design/LINEUP_RULES.md)
- [NUMBERING_RULES.md](../design/NUMBERING_RULES.md)
- [stream_proxy_design.md](../design/stream_proxy_design.md)
