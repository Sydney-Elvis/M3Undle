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
- Timeout settings
- Enabled/disabled toggle
- Optional per-provider max concurrent stream limit

The UI shows:

- Last refresh time
- Success/failure status
- Channel count seen
- Associated profile and snapshot status

Provider configuration covers the playlist source and provider identity. Guide-source management is handled separately in the EPG section.

---

### 2. EPG Sources

The EPG Sources view manages guide inputs per provider.

It supports:

- Multiple XMLTV sources per provider
- Provider-built-in XMLTV, remote URL, or local file sources
- Source priority ordering
- On-demand source test fetch and parse
- Auto-mapping channels from a guide source
- Manual channel-to-guide mapping overrides

The published `/xmltv/m3undle.xml` output is compiled from the enabled sources, merged into one guide feed, and aligned with the active lineup.

---

### 3. Groups (Preview)

The Groups view shows a read-only preview of your provider's catalog.

For each group, the UI displays:

- Group name
- Channel count
- Sample channels

This view is read-only. Instead of manually inspecting thousands of channels, you can see exactly what the provider is delivering and what groups exist before deciding what to do with them.

---

### 4. Snapshots & Status

The Snapshots view shows:

- Last refresh run
- Active snapshot version
- Staged snapshot (if pending)
- Success/failure history

If a refresh fails, the system continues serving the last active snapshot.

The UI makes this behavior visible so you always know what clients are receiving.

---

### 5. Stream Identity

Each published channel uses a stable stream key.

Clients receive URLs like:

`/stream/<streamKey>`

Stream keys are stable across refreshes. They only regenerate if the active provider is switched.

This protects DVR mappings and client configurations.

---

### 6. Settings

The Settings page is split into three panels.

**UI Security**
Shows whether UI authentication (login) is currently enabled. This is controlled by the `M3UNDLE_AUTH_ENABLED` environment variable and is read-only in the UI.

**Endpoint Security**
Controls whether the M3U, XMLTV, stream, and HDHomeRun client endpoints require a username and password. You can:

- Enable or disable endpoint authentication
- Set the username and password clients must supply
- Set the `Virtual Tuner ID` used for HDHomeRun tuner ownership and retune/reuse behaviour
- See whether a credential is currently configured

**Stream Proxy**
Configures how M3Undle handles live stream relay. Settings are grouped into three areas:

- *Session Limits* — how many streams can play simultaneously, and how long a stream stays open after all viewers disconnect
- *Buffering* — how much memory each stream uses for buffering, and how data is read from the source
- *Reconnect Behaviour* — how quickly a stall is detected and how long M3Undle retries before giving up

Each setting includes a plain-English description, and a help icon explains the purpose and default value in detail.

Changes are saved to the database immediately but only take effect after a restart. The page shows the currently active (running) configuration alongside the saved values, and displays a warning banner when they differ. An in-app **Restart M3Undle** button is available once settings have been saved, and shows how many streams are currently active so you know the impact before restarting.

For HDHomeRun-style access, tuner ownership is tracked by the configured `Virtual Tuner ID`, not by remote IP. Re-tuning from the same virtual tuner replaces the prior playback session instead of consuming another tuner slot.

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

## Project Direction

The current focus is delivering a stable, fully usable self-hosted lineup manager.

Advanced features may be introduced in future releases as the project matures.
