# M3Undle Web UI

The M3Undle Web UI is designed to make very large provider catalogs understandable and manageable.

Many providers deliver 10,000–50,000+ channels across multiple regions, languages, sports feeds, and temporary event groups. The Web UI exists to give you clear control over what gets published — and why.

The interface emphasizes:

- Explicit configuration
- Predictable behavior
- Clear visibility
- No hidden logic

---

## UI Sections

### 1. Provider

The Provider section lets you configure and manage your upstream sources.

Add multiple providers and browse each one's catalog. You can switch the active provider at any time — the active provider is the one that drives the published output at `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml`.

Configuration includes:

- Playlist URL
- EPG URL (optional)
- Timeout settings
- Enabled/disabled toggle

The UI shows:

- Last refresh time
- Success/failure status
- Channel count seen
- Associated profile and snapshot status

---

### 2. Groups (Preview)

The Groups view shows a read-only preview of your provider's catalog.

For each group, the UI displays:

- Group name
- Channel count
- Sample channels

This view is read-only. Instead of manually inspecting thousands of channels, you can see exactly what the provider is delivering and what groups exist before deciding what to do with them.

---

### 3. Snapshots & Status

The Snapshots view shows:

- Last refresh run
- Active snapshot version
- Staged snapshot (if pending)
- Success/failure history

If a refresh fails, the system continues serving the last active snapshot.

The UI makes this behavior visible so you always know what clients are receiving.

---

### 4. Stream Identity

Each published channel uses a stable stream key.

Clients receive URLs like:

`/stream/<streamKey>`

Stream keys are stable across refreshes. They only regenerate if the active provider is switched.

This protects DVR mappings and client configurations.

---

## Lineup Shaping (Planned)

A future release will add lineup shaping controls to the UI:

- Group inclusion (select which groups appear in your lineup)
- Channel numbering (start ranges, pinned numbers, overflow handling)
- New channels inbox (review and approve newly discovered channels)
- Dynamic groups for rotating sports or event feeds

---

## UI Design Goals

The Web UI is built around the following principles:

- Explicit over implicit
- Controlled over automatic
- Transparent over opaque
- Scalable for large provider catalogs
- Self-hosted and privacy-respecting

Every action in the UI should be understandable without guessing what the system will do next.

---

## Relationship to CLI

The CLI is a file-oriented filtering tool.

The Web UI builds on those same concepts but adds:

- Database-backed configuration
- Snapshot lifecycle management
- HTTP endpoint publishing
- Visual lineup control

The CLI reduces playlists.
The GUI manages lineups.

---

## Editions

M3Undle follows an open-core model.

The current focus is delivering a stable, fully usable self-hosted lineup manager. Advanced features may be introduced in future releases.
