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

### 7. Channel Mapping

The Channel Mapping page is where you build your output lineup from the provider's channel catalog.

**Group decisions**

Every provider group arrives in a "hold" state. You explicitly include or exclude each group:

- **Include** — channels from this group appear in your output
- **Exclude** — group is ignored entirely
- **Hold** — parked; not yet decided

Groups marked as new (never seen before) are highlighted so you can review and decide without losing track of them. A "Dismiss new" action clears the flag once you have reviewed.

**Group settings**

For each included group you can:

- Set a custom **output name** (renames the group in the published M3U)
- Set an **auto-numbering range** (start–end numbers assigned automatically to unnumbered channels)
- Track or ignore new channels as they appear in the group

**Per-channel control**

Within a group you can select individual channels and set per-channel overrides:

- **Channel number** — explicit `tvg-chno` that takes precedence over auto-numbering
- **Output group** — move a channel to a different output group without changing its source group

**Build Output**

After making changes, use **Build Output** to regenerate the active snapshot. Changes to channel settings are pending until the next build.

---

### 8. Channels

The Channels page shows the live channels currently in the active output snapshot.

You can search by channel name, group, or EPG ID, and filter by group. Each row shows the channel number, logo, display name, group, and EPG ID.

**Edit channel**

The edit button on each row opens a dialog where you can:

- Override the **channel number** (or clear it to fall back to auto-numbering)
- Override the **output group**
- Set a **custom EPG ID** — this field is locked by default; click the padlock to unlock it, read the warning, and confirm before editing. An incorrect EPG ID will break guide data for that channel. The override applies at the next Build Output.

**Remove channel**

The delete button removes a channel from the output. The change takes effect after the next Build Output.

**Number Manager mode**

The **Manage Numbers** button in the page header switches the page into Number Manager mode. The channel grid is replaced by a full-channel editable list:

- Each row shows the current channel number (editable), channel name, and group
- **▲ ▼** buttons swap a channel with its neighbour, transferring their numbers
- Editing a number field directly updates the value and re-sorts the list
- Changed rows are marked with an indicator
- **Apply All** saves all pending number changes to the database
- Changes take effect after a Build Output; exit the mode with **Exit Number Manager**

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

The roadmap will continue expanding the UI's lineup management and operational visibility as the project matures.
