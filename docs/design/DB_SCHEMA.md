# DB Schema (SQLite) — M3Undle

## Design Goals
1. Stable channel identity (canonical channel stays stable even if provider changes URL/name/order)
2. Authoritative numbering (tvg-chno owned by us; stable across refresh)
3. Support provider churn + ephemeral channels (PPV/events that appear and disappear)
4. Prepare for multi-provider mapping and future redundancy/failover
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
- created_utc (TEXT)
- updated_utc (TEXT)

Indexes:
- idx_providers_enabled(enabled)

---

### profiles
- profile_id (PK, TEXT, uuid)
- name (TEXT, unique)
- enabled (INTEGER, 0/1)
- output_name (TEXT)  -- used for /m3u/<output_name>.m3u and /xmltv/<output_name>.xml
- merge_mode (TEXT)   -- 'single', 'merged', 'redundancy-ready'
- created_utc (TEXT)
- updated_utc (TEXT)

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
- event_start_utc (TEXT, nullable)
- event_end_utc (TEXT, nullable)
- first_seen_utc (TEXT)
- last_seen_utc (TEXT)
- active (INTEGER, 0/1)
- last_fetch_run_id (FK fetch_runs)

Unique:
- (provider_id, provider_channel_key)  -- when key is available

Indexes:
- idx_provider_channels_provider_active(provider_id, active)
- idx_provider_channels_seen(provider_id, last_seen_utc DESC)
- idx_provider_channels_is_event(provider_id, is_event, event_start_utc)

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
- **Future:** Controls how multiple providers are blended or failed-over for a single profile output.

### `profile_providers.priority`
- **Current:** Stored but has no effect. Currently one active provider per profile.
- **Future:** Priority ordering for multi-provider merge and failover.

## Tables reserved for future use

### `provider_channels`
- **Current:** Schema present and table exists in DB. **Not populated** by the snapshot builder or preview path. The V1 snapshot build operates entirely in-memory from parsed M3U data — no channel upserts are performed. See docs/dev/DESIGN_DECISIONS.md.
- **Future:** Tracks the volatile provider channel universe. Foundation for canonical channel mapping, event detection, and diff-based inbox UX.

### `provider_groups`
- **Current:** Schema present and table exists in DB. **Not populated** by the snapshot builder or preview path. Same reason as `provider_channels`.
- **Future:** Group catalog for a provider. Used for group-based inclusion rules and group filtering.

### `canonical_channels`
- **Current:** Schema present, not populated. V1 snapshot build does not create canonical channels; stream keys are derived from stable channel properties directly (see `stream_keys` note below).
- **Future:** User-facing stable channel identity that survives provider churn. Foundation for group inclusion, channel numbering, and DVR stability features.

### `channel_sources`
- **Current:** Schema present, not populated.
- **Future:** Maps canonical channels to one or more provider stream candidates. Foundation for multi-provider redundancy and failover.

### `channel_match_rules`
- **Current:** Schema present, not populated.
- **Future:** Auto-classification rules for mapping provider channels to canonical channels and identifying event/ephemeral channels.

### `epg_channel_map`
- **Current:** Schema present, not populated. EPG is passed through from the provider directly using provider tvg-ids.
- **Future:** Explicit tvg-id mapping between canonical channels and XMLTV channel IDs. Used when provider tvg-ids are unstable or need overriding.

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

