# DB Schema (SQLite) — M3Undle

## Design Goals
1. Stable channel identity (canonical channel stays stable even if provider changes URL/name/order)
2. Authoritative numbering (tvg-chno owned by us; stable across refresh)
3. Support provider churn + ephemeral channels (PPV/events that appear and disappear)
4. Prepare for future source mapping and reserved schema growth
5. Fast UI: indexed lookups, minimal joins for common screens

## Terminology
- Provider: upstream service (A, B)
- Provider Channel: a channel as seen in a provider playlist at a specific time (volatile)
- Canonical Channel: user-facing channel identity (stable)
- Source: a provider stream candidate for a canonical channel
- Snapshot: materialized output for serving (playlist/xmltv + JSON index)

---

## Tables

### providers
- provider_id (PK, TEXT, uuid)
- name (TEXT, unique)
- enabled (INTEGER, 0/1)
- playlist_url (TEXT)
- xmltv_url (TEXT, nullable)
- headers_json (TEXT, nullable)
- user_agent (TEXT, nullable)
- timeout_seconds (INTEGER, default 20)
- force_mpegts (INTEGER, 0/1, default 0) -- force upstream MPEG-TS stream handling regardless of content-type
- created_utc (TEXT)
- updated_utc (TEXT)

Note: `providers.is_active` was removed in migration `Alpha6_ActiveProfile`. Active state now lives on `profiles.is_active`.

Indexes:
- idx_providers_enabled(enabled)

---

### profiles
- profile_id (PK, TEXT, uuid)
- name (TEXT, unique)
- enabled (INTEGER, 0/1)
- is_active (INTEGER, 0/1, default 0) -- exactly one profile may have is_active=1; partial unique index enforced
- output_name (TEXT)  -- used for /m3u/<output_name>.m3u and /xmltv/<output_name>.xml
- merge_mode (TEXT)   -- 'single', 'merged', 'redundancy-ready'
- refresh_schedule_kind_override (TEXT, nullable) -- null inherits global; otherwise 'manual'|'1h'|'2h'|'4h'|'6h'|'12h'|'24h'
- refresh_startup_catchup_override (INTEGER, 0/1, nullable) -- null inherits global startup catch-up behavior
- created_utc (TEXT)
- updated_utc (TEXT)

Indexes:
- idx_profiles_is_active(is_active) UNIQUE WHERE is_active = 1

---

### profile_providers
- profile_id (FK profiles)
- provider_id (FK providers)
- priority (INTEGER) -- lower = preferred
- enabled (INTEGER, 0/1)
PK: (profile_id, provider_id)

Indexes:
- idx_profile_providers_profile(profile_id, priority)

---

### fetch_runs
- fetch_run_id (PK, TEXT, uuid)
- provider_id (FK providers)
- started_utc (TEXT)
- finished_utc (TEXT, nullable)
- status (TEXT) -- 'ok','fail'
- error_summary (TEXT, nullable)
- playlist_etag (TEXT, nullable)
- playlist_last_modified (TEXT, nullable)
- xmltv_etag (TEXT, nullable)
- xmltv_last_modified (TEXT, nullable)
- playlist_bytes (INTEGER, nullable)
- xmltv_bytes (INTEGER, nullable)
- channel_count_seen (INTEGER, nullable)

Indexes:
- idx_fetch_runs_provider_time(provider_id, started_utc DESC)
- idx_fetch_runs_status(status, started_utc DESC)

---

### provider_groups
- provider_group_id (PK, TEXT, uuid)
- provider_id (FK providers)
- raw_name (TEXT)
- normalized_name (TEXT, nullable)
- first_seen_utc (TEXT)
- last_seen_utc (TEXT)
- active (INTEGER, 0/1)

Unique:
- (provider_id, raw_name)

Indexes:
- idx_provider_groups_provider_active(provider_id, active)

---

### provider_channels
Tracks the volatile world.
- provider_channel_id (PK, TEXT, uuid)
- provider_id (FK providers)
- provider_channel_key (TEXT) -- best-effort stable key if available
- display_name (TEXT)
- tvg_id (TEXT, nullable)
- tvg_name (TEXT, nullable)
- logo_url (TEXT, nullable)
- stream_url (TEXT)
- group_title (TEXT, nullable)
- provider_group_id (FK provider_groups, nullable)
- is_event (INTEGER, 0/1)
- is_placeholder (INTEGER, 0/1, default 0) -- blank/empty event slot
- event_slot_key (TEXT, nullable) -- stable identity for the reusable upstream slot
- event_content_key (TEXT, nullable) -- normalized identity for the event content assigned to a slot
- event_title (TEXT, nullable) -- normalized human-readable event title
- event_sport (TEXT, nullable) -- detected sport category (e.g. "football", "mma", "racing")
- event_league (TEXT, nullable) -- detected league or promotion (e.g. "NFL", "UFC", "Formula 1")
- event_participants_json (TEXT, nullable) -- JSON array of participant names (e.g. ["Eagles","Giants"])
- event_start_utc (TEXT, nullable)
- event_end_utc (TEXT, nullable)
- first_seen_utc (TEXT)
- last_seen_utc (TEXT)
- active (INTEGER, 0/1)
- content_type (TEXT, default 'live') -- 'live'|'vod'|'series'
- last_fetch_run_id (FK fetch_runs)

Unique:
- (provider_id, provider_channel_key)  -- when key is available

Indexes:
- idx_provider_channels_provider_active(provider_id, active)
- idx_provider_channels_seen(provider_id, last_seen_utc DESC)
- idx_provider_channels_is_event(provider_id, is_event, event_start_utc)
- idx_provider_channels_placeholder_active(provider_id, is_placeholder, active)
- idx_provider_channels_event_content(provider_id, event_content_key) WHERE event_content_key IS NOT NULL

---

### canonical_channels
Stable identity controlled by you (profile-scoped).
- channel_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- display_name (TEXT)
- channel_number (INTEGER) -- authoritative tvg-chno
- group_name (TEXT, nullable)
- logo_url (TEXT, nullable)
- enabled (INTEGER, 0/1)
- is_event (INTEGER, 0/1)
- event_policy (TEXT) -- 'ttl-days','auto-hide-after-end','manual'
- notes (TEXT, nullable)
- created_utc (TEXT)
- updated_utc (TEXT)

Optional unique:
- (profile_id, channel_number)

Indexes:
- idx_canonical_channels_profile_number(profile_id, channel_number)
- idx_canonical_channels_profile_enabled(profile_id, enabled)

---

### channel_sources
Maps canonical channel -> one or more provider sources (future redundancy-ready).
- channel_source_id (PK, TEXT, uuid)
- channel_id (FK canonical_channels)
- provider_id (FK providers)
- provider_channel_id (FK provider_channels)
- priority (INTEGER) -- 1 primary, 2 fallback, etc.
- enabled (INTEGER, 0/1)
- override_stream_url (TEXT, nullable)
- last_success_utc (TEXT, nullable)
- last_failure_utc (TEXT, nullable)
- failure_count_rolling (INTEGER, default 0)
- health_state (TEXT) -- 'unknown','ok','degraded','down'
- created_utc (TEXT)
- updated_utc (TEXT)

Constraints:
- UNIQUE(channel_id, priority)

Indexes:
- idx_channel_sources_channel(channel_id, priority)
- idx_channel_sources_health(health_state, last_failure_utc DESC)

---

### channel_match_rules (optional but recommended)
Auto-suggestion rules for mapping and event classification.
- rule_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- enabled (INTEGER, 0/1)
- match_type (TEXT) -- 'tvg_id','name_contains','regex','group_contains'
- match_value (TEXT)
- target_channel_id (FK canonical_channels, nullable)
- target_group_name (TEXT, nullable)
- default_priority (INTEGER, default 1)
- is_event_rule (INTEGER, 0/1)
- created_utc (TEXT)
- updated_utc (TEXT)

Indexes:
- idx_match_rules_profile(profile_id, enabled)

---

### epg_channel_map
- epg_map_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- channel_id (FK canonical_channels)
- xmltv_channel_id (TEXT)
- source (TEXT) -- 'provider','manual','rule'
- created_utc (TEXT)
- updated_utc (TEXT)

Unique:
- UNIQUE(profile_id, channel_id)
- UNIQUE(profile_id, xmltv_channel_id)

Indexes:
- idx_epg_map_profile(profile_id, xmltv_channel_id)

---

### profile_group_filters
V1 per-group inclusion decisions for a profile. Drives what channels appear in the output.
- profile_group_filter_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- provider_group_id (FK provider_groups)
- decision (TEXT) — `'pending'` | `'include'` | `'exclude'`
- is_new (INTEGER, 0/1) — compatibility flag; in current behavior `pending` groups are treated as new
- channel_mode (TEXT) — `'select'` (manual review) | `'all'` (auto-update)
- tracking_policy (TEXT, default 'review') — event-channel tracking: `'review'` | `'notify'` | `'auto_add_all'` | `'auto_add_populated'` | `'auto_add_matching'`
- tracking_keywords (TEXT, nullable) — comma/newline/pipe-separated free-text keywords for `auto_add_matching`
- output_name (TEXT, nullable) — renames the group in the published output; defaults to provider raw_name
- auto_num_start (INTEGER, nullable) — first channel number for auto-numbering within this group
- auto_num_end (INTEGER, nullable) — last channel number before auto-numbering stops
- track_new_channels (INTEGER, 0/1) — notification preference for pending review noise (notify/mute)
- sort_override (INTEGER, nullable)
- created_utc (TEXT)
- updated_utc (TEXT)

Unique:
- (profile_id, provider_group_id)

Indexes:
- idx_pgf_profile_group_unique (profile_id, provider_group_id)
- idx_pgf_profile_decision (profile_id, decision)
- idx_pgf_profile_tracking_policy (profile_id, tracking_policy)

---

### profile_event_interest_rules
Structured recurring-interest rules for event channel auto-add/notify/suppress behavior.
- rule_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- provider_id (FK providers, nullable) — optional scope to one provider
- provider_group_id (FK provider_groups, nullable) — optional scope to one group
- enabled (INTEGER, 0/1, default 1)
- match_type (TEXT) — `'keyword'` | `'team'` | `'league'` | `'sport'` | `'fighter'` | `'promotion'` | `'series'`
- match_value (TEXT) — value to match (case-insensitive substring)
- action (TEXT) — `'auto_add'` | `'notify'` | `'suppress'`
- priority (INTEGER, default 100) — evaluation order; lower values are checked first
- created_utc (TEXT)
- updated_utc (TEXT)

Indexes:
- idx_peir_profile_enabled_priority (profile_id, enabled, priority)

---

### profile_group_channel_filters
V1 per-channel selections and overrides within a group filter.
- profile_group_channel_filter_id (PK, TEXT, uuid)
- profile_group_filter_id (FK profile_group_filters)
- provider_channel_id (FK provider_channels)
- state (TEXT) — `'pending'` | `'included'` | `'excluded'`
- display_name_override (TEXT, nullable)
- output_group_name (TEXT, nullable) — moves channel to a different output group
- channel_number (INTEGER, nullable) — explicit `tvg-chno`; takes precedence over auto-numbering
- tvg_id_override (TEXT, nullable) — replaces the provider's `tvg-id` in the snapshot output; lock-gated in UI to prevent accidental EPG breakage
- created_utc (TEXT)
- updated_utc (TEXT)

Unique:
- (profile_group_filter_id, provider_channel_id)

Indexes:
- idx_pgcf_filter_channel_unique (profile_group_filter_id, provider_channel_id)

---

### snapshots
- snapshot_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- created_utc (TEXT)
- status (TEXT) -- 'active','staged','failed','archived'
- playlist_path (TEXT)
- xmltv_path (TEXT)
- channel_index_path (TEXT)
- status_json_path (TEXT)
- channel_count_published (INTEGER)
- error_summary (TEXT, nullable)
- change_class (TEXT, nullable) -- classification of change vs previous snapshot: 'none'|'guide_only'|'lineup'|'breaking'|null (first run)

Indexes:
- idx_snapshots_profile_status(profile_id, status, created_utc DESC)

---

### stream_keys
Stable token used by clients in /stream/<streamKey>.
- stream_key (PK, TEXT)
- profile_id (FK profiles)
- channel_id (FK canonical_channels)
- created_utc (TEXT)
- last_used_utc (TEXT, nullable)
- revoked (INTEGER, 0/1)

Unique:
- UNIQUE(profile_id, channel_id)

Indexes:
- idx_stream_keys_profile(profile_id, revoked)
- idx_stream_keys_channel(channel_id)

---

## Observability Additions

The following tables and columns support metrics access, token management, and observability settings.

---

### metrics_tokens
App-generated tokens for Prometheus-compatible metrics scraping.

- metrics_token_id (PK, TEXT, uuid)
- name (TEXT, unique)
- token_hash (TEXT) -- hashed token; plaintext is shown once at creation only
- scope (TEXT, default 'metrics:read')
- created_utc (TEXT)
- last_used_utc (TEXT, nullable)
- expires_utc (TEXT, nullable)

Indexes:
- ux_metrics_tokens_name(name)
- idx_metrics_tokens_expires(expires_utc)

Notes:
- Tokens authenticate only the metrics endpoint.
- Diagnostics APIs continue to use UI/admin authorization.
- Token regeneration is create-new plus delete-old.

---

### site_settings additions (observability)
New columns added to the existing `site_settings` table:

- observability_metrics_enabled (INTEGER, 0/1, default 1)
- observability_metrics_mode (TEXT, default 'LocalOnly') -- 'Disabled'|'LocalOnly'|'Token'|'Public'
- observability_metrics_enable_channel_labels (INTEGER, 0/1, default 0)
- observability_metrics_local_allowed_cidrs (TEXT, nullable) -- newline-separated CIDR list

The configured metrics path remains an app setting (`M3Undle:Observability:Metrics:Path`) because the OpenTelemetry scrape endpoint is mapped at startup.

---

## Alpha 5 Additions

The following tables and columns were added during the Alpha 5 release cycle.

---

### profile_custom_groups
User-defined output groups not tied to a single provider group.
- custom_group_id (PK, TEXT, uuid)
- profile_id (FK profiles)
- name (TEXT) -- user-defined output group name
- decision (TEXT, default 'include') -- 'pending'|'include'|'exclude'
- channel_mode (TEXT, default 'select') -- 'select' (manual review) | 'all' (auto-update)
- tracking_policy (TEXT, default 'review') -- same policy options as profile_group_filters
- tracking_keywords (TEXT, nullable)
- auto_num_start (INTEGER, nullable)
- auto_num_end (INTEGER, nullable)
- track_new_channels (INTEGER, 0/1)
- sort_override (INTEGER, nullable)
- created_utc (TEXT)
- updated_utc (TEXT)

---

### profile_custom_group_channels
Per-channel membership in a custom group.
- custom_group_channel_id (PK, TEXT, uuid)
- custom_group_id (FK profile_custom_groups, CASCADE)
- provider_channel_id (FK provider_channels, CASCADE)
- state (TEXT, default 'included') -- 'pending'|'included'|'excluded'
- channel_number (INTEGER, nullable)
- display_name_override (TEXT, nullable)
- tvg_id_override (TEXT, nullable)
- created_utc (TEXT)
- updated_utc (TEXT)

Unique:
- (custom_group_id, provider_channel_id)

---

### profile_custom_group_provider_links
Linked provider groups that feed channels into a custom group.
- link_id (PK, TEXT, uuid)
- custom_group_id (FK profile_custom_groups, CASCADE)
- provider_group_id (FK provider_groups, CASCADE)
- created_utc (TEXT)

Unique:
- (custom_group_id, provider_group_id)

---

### downstream_integrations
Configured downstream client integrations (Jellyfin, Emby, webhook).
- downstream_integration_id (PK, TEXT, uuid)
- profile_id (FK profiles, nullable) -- null = applies to all profiles
- name (TEXT)
- kind (TEXT) -- 'jellyfin'|'emby'|'webhook'
- base_url (TEXT)
- api_key_encrypted (TEXT, nullable) -- AES-256-GCM encrypted; requires M3UNDLE_ENCRYPTION_KEY
- webhook_headers_json (TEXT, nullable)
- trigger_on_lineup_update (INTEGER, 0/1, default 1)
- trigger_on_guide_update (INTEGER, 0/1, default 1)
- enabled (INTEGER, 0/1, default 1)
- last_notified_utc (TEXT, nullable)
- last_notify_error (TEXT, nullable)
- created_utc (TEXT)
- updated_utc (TEXT)

---

### site_settings additions (refresh schedule)
New columns added to the existing `site_settings` table:
- refresh_schedule_kind (TEXT, default '6h') -- 'manual'|'1h'|'2h'|'4h'|'6h'|'12h'|'24h'
- refresh_startup_catchup (INTEGER, 0/1, default 1) -- global default for startup catch-up when the active profile has no override

### epg_sources additions
- refresh_interval_hours (INTEGER, nullable) -- per-source cadence override; null = follow global schedule

---

## Notes / Behavior

### Stable identity despite provider churn
- Canonical channel is what clients effectively bind to (via numbering + stream_key + EPG mapping).
- Provider channels may change name/logo/group/url; canonical identity remains stable.

### Authoritative numbering
- canonical_channels.channel_number is source of truth.
- Sort output by channel_number (tie-break by display_name).

### Snapshot lifecycle
- Build staged snapshot, validate, then atomically mark active.
- Keep last-known-good active if refresh fails.

---

# Appendix A — Event/Ephemeral Channel Detection

## Detection modes
### Mode 1 — Explicit rules (preferred)
Use `channel_match_rules` to identify events by group/name patterns (provider-specific).

### Mode 2 — Heuristics (suggest-only by default)
Signals:
- date/time prefixes in name (e.g., `01/14 07:45 pm | ...`)
- group patterns (e.g., starts with `Live |`)
- keywords (PPV, Live Only, Fixture)
- sudden appearance spikes in volatile groups

Recommendation:
- heuristics mark provider_channels.is_event=1 and create UI suggestions
- do not auto-create canonical channels unless enabled by rule/policy

## Event lifecycle policies (canonical_channels.event_policy)
- ttl-days (recommended default): hide after last_seen_utc + TTL
- auto-hide-after-end: hide after event_end_utc + grace
- manual: admin-controlled

## UI fast path
- “New/Changed from Provider” view (first_seen/last_seen + diffs)
- “Event Channels” view (TTL, bulk hide/archive)

---

# Appendix C — Current Constraints and Reserved Fields

Several schema fields and tables are present for forward-compatibility but are not populated or enforced currently. This appendix documents what is active versus what is reserved for future releases.

## Fields reserved for future use

### `profiles.output_name`
- **Current:** The output name is locked to `m3undle` in Core regardless of this field value. Serving endpoints are always `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml`.
- **Future:** Named output endpoints per profile (e.g. `/m3u/livingroom.m3u`, `/m3u/mancave.m3u`).

### `profiles.merge_mode`
- **Current:** Always `single`. The `merged` and `redundancy-ready` values are schema-valid but unused.
- **Future:** Reserved for possible profile source strategies if user-facing behavior is added later.

### `profile_providers.priority`
- **Current:** Ordering/preference metadata only. No user-facing provider-priority behavior is exposed.
- **Future:** Reserved for profile source ordering if that behavior is introduced later.

## Tables active in V1 (beyond the original design)

### `provider_channels`
- **Current:** Actively populated by each snapshot refresh. Only live channels are persisted; VOD/Series remain in-memory for the snapshot build. Used as the source of truth for the Channels and Channel Mapping pages.
- **Future:** Full population including VOD/Series. Foundation for canonical channel mapping, event detection, and diff-based inbox UX.

### `provider_groups`
- **Current:** Actively populated by each snapshot refresh. Used to drive the group-level inclusion/exclusion UI (Channel Mapping page).
- **Future:** Group catalog extended for group-ordering and dynamic group rules.

### `profile_group_filters` and `profile_group_channel_filters`
- **Current:** Active per-group and per-channel review tables. Store group decision/mode/notify behavior plus per-channel pending/included/excluded state and overrides (name/output group/channel number/`tvg_id_override`).
- **Future:** These tables are the V1 bridge until `canonical_channels` and `channel_sources` are fully activated.

## Tables reserved for future use

### `canonical_channels`
- **Current:** Schema present, not populated. V1 snapshot build does not create canonical channels; stream keys are derived from stable channel properties directly (see `stream_keys` note below).
- **Future:** User-facing stable channel identity that survives provider churn. Foundation for group inclusion, channel numbering, and DVR stability features.

### `channel_sources`
- **Current:** Schema present, not populated.
- **Future:** Maps canonical channels to one or more provider stream candidates. Foundation for later source-selection behavior if needed.

### `channel_match_rules`
- **Current:** Schema present, not populated.
- **Future:** Auto-classification rules for mapping provider channels to canonical channels and identifying event/ephemeral channels.

### `epg_channel_map`
- **Current:** Legacy canonical-channel mapping table remains in schema but is not used by the active web EPG pipeline.
- **Future:** Explicit tvg-id mapping between canonical channels and XMLTV channel IDs. Used when provider tvg-ids are unstable or need overriding.

### `epg_sources` / `epg_source_channels` / `epg_channel_mappings` / `epg_fetch_runs`
- **Current:** Active web EPG pipeline stores provider-linked source definitions, discovered source channels, mapping records per profile+provider channel+source, and fetch history. `epg_sources.refresh_interval_hours` (nullable) was added in Alpha 5 to support per-source cadence overrides.
- **Current:** Snapshot refresh compiles a merged guide.xml from enabled sources using source priority, coverage checks, and deduplication.
- **Future:** Align this model with canonical channels for cross-provider EPG identity.

## Notes on stream_keys (V1 pass-through mode)

The `stream_keys` table is defined with `channel_id (FK canonical_channels)` for the future canonical-channel-backed model. In **V1 pass-through mode**, stream keys are not stored in this table at all. Instead, the `channel_index.json` snapshot file serves as the lookup table for `/stream/<streamKey>`.

Stream key derivation in V1:
- Input: `tvg-id` when present; otherwise `displayName + "\u001f" + streamUrl`
- Hash: `SHA-256(stableKey + ":" + profileId)` → base64url → first 16 chars
- Keys are deterministic and stable across refreshes as long as channel identity is stable

---

# Appendix B — Client Compatibility (NextPVR, Jellyfin, Plex)

## What must remain stable
- channel identity (canonical ID + stable stream_key)
- numbering (tvg-chno)
- XMLTV channel ids (epg_channel_map.xmltv_channel_id)
- playlist sort order (by channel_number)

## Playlist conventions
- Header must include url-tvg / x-tvg-url pointing to this service’s XMLTV
- EXTINF should include tvg-chno, tvg-name, tvg-id, group-title, tvg-logo where available
- Stream URLs must be service-owned (/stream/<streamKey>)

## Failure behavior
- If upstream fails, do not churn the lineup.
- Serve last-known-good snapshot and show degraded state in /status + UI.

