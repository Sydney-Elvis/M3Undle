# M3Undle Service + Web UI

The M3Undle service is the self-hosted component that turns large provider catalogs into clean, controlled, DVR-friendly lineups.

It is designed for the common real-world problem:

- Providers deliver 10,000–50,000+ channels
- Most are irrelevant (wrong language/region, duplicates, temporary events)
- Configuration in many tools is difficult to understand and hard to maintain
- Users need explicit control over what is published and why

The service focuses on **clarity, control, and predictable behavior**.

---

## What the Service Does

At a high level, the service:

- Ingests a provider playlist (M3U) and guide data (XMLTV)
- Supports multiple provider-linked EPG sources, source-priority merge rules, and per-channel guide mapping
- Builds versioned output and serves the **last-known-good** version
- Publishes compatibility endpoints for clients:
  - M3U — `/m3u/m3undle.m3u`
  - XMLTV — `/xmltv/m3undle.xml`
  - Shared live stream proxy — `/live/<streamKey>`, `/stream/<streamKey>`, `/tune/<streamKey>`, `/hdhr/tune/<streamKey>`
  - Direct relay for VOD-style routes — `/movie/<streamKey>`, `/vod/<streamKey>`, `/series/<streamKey>`
  - Xtream Codes API — `/player_api.php`, `/get.php`, `/live/<user>/<pass>/<id>`, `/movie/<user>/<pass>/<id>`, `/series/<user>/<pass>/<id>`
- Shares one upstream live connection across subscribers for the same channel session
- Keeps a small byte-bounded in-memory buffer for late joiners
- Reconnects on upstream stalls and evicts slow subscribers without blocking the whole session
- Enforces HDHomeRun tuner-slot limits by `VirtualTunerId`, so the same virtual tuner can retune without consuming another slot

---

## Key Concepts (User-Facing)

### Provider
An upstream source you configure (URL + credentials). Providers can be large and noisy.

Multiple providers can be configured and previewed. Published output is resolved through the active profile and its linked provider configuration. The shared `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml` endpoints always serve the currently active profile.

### Group
A category label from the provider playlist (e.g., `USA | News`, `LIVE | NFL (Direct)`, `UFC Fight Night | ...`). Groups are the primary way to understand the shape of a provider's catalog.

### Canonical Channel
A stable identity representing "the channel" independent of how providers rename/reorder things over time. Forms the foundation for lineup shaping in a future release.

### Lineup
The published channel set, served at:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`

### Published Version / Snapshot
An atomic version of a lineup output (playlist + guide mapping + stream keys).
Published versions allow last-known-good behavior: if a refresh fails, clients keep working.
`Snapshot` is the internal/technical term for this concept. User-facing UI uses "last published" or "published version".

### Stream Key
A stable identifier used by published stream URLs.
Clients receive a URL like `/stream/<streamKey>` instead of a raw provider URL.

---

## Refresh Lifecycle

A refresh run follows this pattern:

1. Fetch provider inputs (M3U + XMLTV) for the active profile from its linked provider configuration
2. Parse provider groups and channels into memory
3. Build snapshot output (M3U + XMLTV files written to disk)
4. Validate output (basic integrity checks)
5. Promote to active — clients immediately receive the new lineup
6. Serve active output to clients

If refresh fails:
- The service continues serving the last successfully published version (last-known-good).

---

## HTTP Endpoints (Compatibility Layer)

The service publishes endpoints intended to be consumed by clients and DVR systems.

- `GET /m3u/m3undle.m3u`
- `GET /xmltv/m3undle.xml`
- `GET /stream/<streamKey>`
- `GET /live/<streamKey>`
- `GET /tune/<streamKey>`
- `GET /hdhr/tune/<streamKey>`

Live routes are served by the shared stream proxy. VOD-style routes (`/movie`, `/vod`, `/series`) stay on direct relay paths.

**Xtream Codes API** (`/player_api.php`, `/get.php`, path-credential stream URLs) is also available for clients such as TiviMate, GSE Player, and IPTV Smarters. See `docs/design/HTTP_COMPATIBILITY.md` for the full endpoint reference.

Operational status endpoints are also available for authenticated UI users:

- `GET /status/streams`
- `GET /status/streams/clients`
- `GET /status/streams/providers`

See: `docs/design/HTTP_COMPATIBILITY.md`

---

## Web UI (Configuration + Visibility)

The Web UI is intended to make large catalogs manageable.

Views:

- **Overview**: see system health, published output, active profiles, and action items
- **Provider**: configure providers, preview catalogs, and manage provider/profile associations
  - Import providers from config.yaml (read-only, credentials secure)
  - Add/edit providers directly in the GUI (for testing or one-off providers)
  - Check provider health (credentials defined, last successful fetch, etc.)
- **EPG Sources**: manage provider-linked XMLTV sources, test guide fetches, and tune channel mappings
- **Profiles**: list all configured profiles (named published lineups); click through to the profile detail page for provider membership, published history, and pending review items
- **Channel Mapping**: build your output lineup from the provider's channel catalog; manage group pending/include/exclude decisions, manual-review vs auto-update mode, output group names, auto-numbering ranges, and per-channel overrides
- **Channel Review Queue**: review pending channels in `/channels/review`; include/exclude selected channels or bulk-action pending channels by provider group
- **Channels**: browse the live channels currently in the published lineup; edit channel numbers, output groups, and EPG IDs
- **Streams**: see active stream sessions, connected clients, buffer usage, reconnect activity, and recently ended sessions
- **Settings**: configure endpoint security credentials, HDHomeRun settings, stream proxy settings, refresh schedule, and downstream integrations; displays active vs. saved configuration with a restart-required indicator and in-app restart button

Design goals:
- configuration should be explicit and understandable
- changes should be visible (what changed, when)
- credentials should be secure and managed externally via `.env`

Provider configuration also supports an optional per-provider max concurrent stream limit, which is applied when admitting shared live sessions.

### Config.yaml Integration

The service can import provider definitions from the same `config.yaml` file used by the CLI. This allows:

- Single source of truth for provider definitions
- Secure credential management via `.env` files (credentials never stored in database)
- Coexistence of CLI and web UI workflows
- Read-only imports (config.yaml changes must be edited in the YAML, then re-imported)

See: `docs/spec/config_spec.md`

---

## Lineup Shaping

The following lineup shaping features are implemented:

- Group inclusion/exclusion rules (select which groups appear in your lineup)
- Group mode rules (`manual review` vs `auto-update`)
- Pending channel review queue with profile-scoped include/exclude decisions
- Notify/mute controls for pending-review alert noise on high-churn groups
- Channel numbering (start ranges, pinned numbers, overflow handling, sort position via Number Manager)
- Custom `tvg-id` override per channel (lock-gated field in the channel edit dialog)
- **Event tracking policies** for volatile groups (PPV/event/sports grids): `review`, `notify`, `auto_add_all`, `auto_add_populated`, `auto_add_matching`
- **Placeholder suppression**: blank/empty event slots are classified and never published or queued
- **Event content identity**: event slot key vs event content key distinguishes the slot from the real event
- **Richer event metadata**: sport, league, participants extracted per channel and available for filtering
- **Structured interest rules**: typed recurring-interest rules (team/league/sport/fighter/promotion/series) with `auto_add`, `notify`, or `suppress` actions, scoped to a profile/provider/group
- **Event card view** in the channel review queue: groups pending channels by event content key with inline sport/league labels and multi-feed count visibility

---

## Relationship to the CLI

The CLI is a file-oriented tool for filtering large playlists.
The service builds on the same foundational ideas but adds:

- DB-backed configuration
- lineup publishing
- HTTP endpoints
- web-based management and visibility

See: `CLI.md`

---

## Project Direction

The current focus is delivering a stable, fully usable self-hosted lineup manager.

The roadmap will continue expanding M3Undle's lineup management, endpoint support, and operational tooling as the project matures.
