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
- Alpha 5: complete — active profile switching, lineup status, channel review queue, dynamic groups, downstream integrations, HLS browser playback
- Alpha 6: complete — per-provider gateway/VPN routing, Xtream auto-detection, system event badge, observability endpoints, provider expiry
- Alpha 7: complete — adaptive stream recovery, channel health tracking (Stable/Cautious/Unstable), relay policy (auto/on/off), stream monitor improvements, HDHR page, About page, interface polish
- Beta: in progress — DVR client validation, documentation, hardening

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
- [x] Publish-target toggle (later replaced by the active-profile model)
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
- [x] Settings page stream UI: read/write configuration for approved stream proxy tuning values (enable/disable, session limits, buffer sizing, reconnect behaviour); values persisted to DB and loaded at startup via `IConfigureOptions`; in-app restart trigger with "restart required" banner when saved settings differ from the active runtime; startup `IValidateOptions` validation rejects out-of-range config from both appsettings and DB; byte-field upper bounds enforced in UI (1 GiB per session, 16 MiB read chunk) and service validation

#### DVR Integration (HDHomeRun Emulation)
- [x] Initial HDHomeRun compatibility groundwork:
  `GET /discover.json`, `GET /lineup.json`, `GET /lineup_status.json`, discovery service, device identity, lineup rendering tests
- [x] Number of tuners setting in user-facing configuration
- [x] Connection limiting via HDHomeRun tuner-slot enforcement keyed by `VirtualTunerId` from endpoint binding; same-tuner retunes replace prior subscriber instead of consuming another slot

#### EPG Sources
- [x] EPG source management UI + API (multiple sources per provider, test fetch, auto-map, manual mapping)
- [x] Additional XMLTV/EPG source URLs per provider
- [x] XMLTV merge into one guide feed
- [x] De-duplicate EPG entries by channel id
- [x] Source priority rules across guide inputs
- [~] Cross-source `tvg-id` mapping (via per-channel source mappings; canonical-channel mapping remains future work)

### Alpha 5 — Remaining Features
Goal: Finish remaining lineup-management features before Beta hardening.

Status: Complete. (Related issue seeds: #3, #4, #5, #6, #7, #8, #9)

- [x] Channel reorder (explicit sort position via Number Manager) — #3
- [x] Custom `tvg-id` override per channel (lock-gated field in channel edit dialog)
- [x] Full channel numbering rules (see `../design/NUMBERING_RULES.md`)
- [x] Dashboard redesign — health dashboard with Published Output, Published Profiles, Action Items, Output URLs sections
- [x] Profiles UX — `/profiles` list page and profile detail page (display name, output name, provider membership, published history)
- [x] Active profile switching — switch active profile from the UI with visible status feedback
- [x] Terminology cleanup — snapshot language scrubbed from user-facing UI; lineup/published-version language throughout
- [x] HLS playback for JavaScript/browser clients (GeneratedHls compatibility layer)
- [x] CORS support for external network access
- [x] Configurable refresh schedule in Settings UI — per-provider and global intervals via `RefreshScheduleService`
- [x] New channels inbox / review queue
- [x] Dynamic groups for rotating/event feeds
- [x] Downstream integrations — notify Jellyfin/Emby when the lineup changes (webhook + native adapter); snapshot change classification to suppress notifications for no-op refreshes
- [x] Lineup status service — real-time status display for active profile state and switching feedback
  - [x] Active-profile-scoped status payload: include active profile identity, serving provider, published snapshot/version, last refresh result, and refresh/switch state
  - [x] Correct status resolution: derive lineup state from the active profile's published snapshot, not any active snapshot in the database
  - [x] Explicit switch lifecycle feedback: requested, refresh/build in progress, complete, failed while serving last known-good
  - [x] Dashboard polish: show which profile is currently serving at the published output URLs and whether a switch is pending or degraded
  - [x] `/status` and readiness semantics aligned with the active profile state
  - [x] Regression coverage for multi-profile status cases: inactive profiles with retained snapshots, successful switch, failed switch, and no-active-profile state

> See the Alpha 5 validation checklist for concrete acceptance criteria:
> `docs/dev/ALPHA5_VALIDATION_CHECKLIST.md`

### Alpha 6 — Per-Provider Gateway Support, Xtream Auto-Detection & System Events
Goal: Per-provider gateway/VPN routing with Block and Fallback modes. Xtream Codes auto-detection at provider add time with explicit user mode selection. System event infrastructure with nav bar badge for diagnostic visibility. Gateway documentation and companion gateway project remain insiders features.

Status: Complete.

- [x] Per-provider gateway/VPN routing (Block and Fallback modes)
- [x] Xtream Codes auto-detection at provider add time
- [x] System event badge (nav bar, in-memory, diagnostic)
- [x] Prometheus-compatible metrics (LocalOnly, Token, Public modes)
- [x] Liveness/readiness/health probes
- [x] Authenticated diagnostics APIs
- [x] Provider account and playlist expiration visibility
- [x] Per-provider refresh scheduling

See implementation plans:
- [AUTOMATION_LAB_INTEGRATION_PLAN.md](../../.ai_docs/AUTOMATION_LAB_INTEGRATION_PLAN.md) — automation lab integration (provider seed, readiness endpoints, streaming test scenarios)
- [XTREM_PROVIDER_DETECTION.md](../../.ai_docs/XTREM_PROVIDER_DETECTION.md) — Xtream auto-detection
- [EVENT_BADGE_SYSTEM.md](../../.ai_docs/EVENT_BADGE_SYSTEM.md) — system event badge

### Alpha 7 — Adaptive Stream Recovery & Interface Polish
Goal: Make live stream handling robust for noisy/unstable providers. Polish interfaces and navigation before beta.

Status: Complete.

- [x] Adaptive stream recovery — detect stalls, recover from safe MPEG-TS boundaries, force hard controlled retune when needed
- [x] Channel health tracking — per-channel Stable/Cautious/Unstable classification persisted to DB
- [x] Relay policy — explicit per-provider Auto/On/Off setting replacing hidden clean-remux toggle
- [x] Stream health events — durable clean-watch evidence and health promotion/demotion logic
- [x] Stream monitor improvements — transfer rates, relay decision reason, startup health visible
- [x] HDHR discovery page — dedicated HDHomeRun page showing device info and endpoint URLs
- [x] About page — product info, version, build details
- [x] Dashboard reorganization — endpoints, stream limiting, and active-profile visibility improvements
- [x] Channel mapping UX — new channels not tracked by default; clearer group/chip display
- [x] Navigation improvements — guided setup flow, highlighted active page
- [x] Database performance — slow-DB mitigations, UI shows DB response status
- [x] Controlled-retune fix — retune suppressed when internal HLS relay subscriber is attached
- [x] IPTVnator compatibility fixes

---

### Beta — Hardening & Release Prep
Goal: No major feature additions. Stabilize, validate, and document.

Status: In progress.

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
