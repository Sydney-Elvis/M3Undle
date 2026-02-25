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
- Builds **snapshots** and serves **last-known-good** output
- Publishes compatibility endpoints for clients:
  - M3U — `/m3u/m3undle.m3u`
  - XMLTV — `/xmltv/m3undle.xml`
  - Stream relay proxy — `/stream/<streamKey>`

---

## Core Concepts (User-Facing)

### Provider
An upstream source you configure (URL + credentials). Providers can be large and noisy.

Multiple providers can be configured and previewed. You can compare their catalogs and switch the active provider at any time. Only one provider drives the published output at a time — switching will rebuild the published lineup from the new source.

### Group
A category label from the provider playlist (e.g., `USA | News`, `LIVE | NFL (Direct)`, `UFC Fight Night | ...`). Groups are the primary way to understand the shape of a provider's catalog.

### Canonical Channel
A stable identity representing "the channel" independent of how providers rename/reorder things over time. Forms the foundation for lineup shaping in a future release.

### Lineup
The published channel set, served at:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`

### Snapshot
A published, atomic "version" of a lineup output (playlist + guide mapping + stream keys).
Snapshots allow last-known-good behavior: if a refresh fails, clients keep working.

### Stream Key
A stable identifier used by published stream URLs.
Clients receive a URL like `/stream/<streamKey>` instead of a raw provider URL.

---

## Snapshot Lifecycle

A refresh run follows this pattern:

1. Fetch provider inputs (M3U + XMLTV) from the active provider
2. Parse and upsert provider groups and channels into the DB
3. Generate a **staged snapshot** (M3U + XMLTV files written to disk)
4. Validate snapshot (basic integrity checks)
5. Promote staged snapshot to **active**
6. Serve active snapshot to clients

If refresh fails:
- The service continues serving the last active snapshot (last-known-good).

---

## HTTP Endpoints (Compatibility Layer)

The service publishes endpoints intended to be consumed by clients and DVR systems.

- `GET /m3u/m3undle.m3u`
- `GET /xmltv/m3undle.xml`
- `GET /stream/<streamKey>`

See: `docs/design/HTTP_COMPATIBILITY.md`

---

## Web UI (Configuration + Visibility)

The Web UI is intended to make large catalogs manageable.

Views:

- **Provider**: configure providers, preview catalogs, and manage the active provider
  - Import providers from config.yaml (read-only, credentials secure)
  - Add/edit providers directly in the GUI (for testing or one-off providers)
  - Check provider health (credentials defined, last successful fetch, etc.)
- **Groups**: browse the provider's groups and channel counts (read-only preview)
- **Snapshots / Status**: see refresh history and the current active snapshot

Design goals:
- configuration should be explicit and understandable
- changes should be visible (what changed, when)
- credentials should be secure and managed externally via `.env`

### Config.yaml Integration

The service can import provider definitions from the same `config.yaml` file used by the CLI. This allows:

- Single source of truth for provider definitions
- Secure credential management via `.env` files (credentials never stored in database)
- Coexistence of CLI and web UI workflows
- Read-only imports (config.yaml changes must be edited in the YAML, then re-imported)

See: `docs/spec/config_spec.md`

---

## Lineup Shaping (Planned)

A future release will introduce lineup shaping controls:

- Group inclusion rules (select which groups appear in your lineup)
- Channel numbering (start ranges, pinned numbers, overflow handling)
- New channels inbox (review and approve newly discovered channels)
- Dynamic groups for rotating sports or event feeds

---

## Relationship to the CLI

The CLI is a file-oriented tool for filtering large playlists.
The service builds on the same core ideas but adds:

- DB-backed configuration
- snapshot publishing
- HTTP endpoints
- web-based management and visibility

See: `CLI.md`

---

## Editions Note

M3Undle follows an open-core model.
The current focus is delivering a stable, fully usable self-hosted lineup manager.
Advanced features may be introduced in future releases.
