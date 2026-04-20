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

Add multiple providers and browse each one's catalog. The Provider page manages upstream sources and their profile associations. Published output at `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml` is driven by the active profile.

Configuration includes:

- Playlist URL
- Timeout settings
- Enabled/disabled toggle
- Optional per-provider max concurrent stream limit

The UI shows:

- Last refresh time
- Success/failure status
- Channel count seen
- Associated profile and published version status

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

### 3. Profiles

The Profiles page lists all configured profiles — the named lineups you publish for clients.

Each profile shows:

- Display name and output name
- Enabled/disabled state
- Linked providers
- Last published time and health status
- Live, movie, and series counts

Clicking a profile opens its detail page, which shows provider membership, published history, and pending review items.

Profiles can be deleted via `DELETE /api/v1/profiles/{profileId}`. Deletion removes all associated data (group filters, channel selections, custom groups, canonical channels, stream keys, snapshots, and provider links) and is blocked while a snapshot refresh is in progress.

---

### 4. Published Version & Status

The dashboard shows:

- Last refresh run
- Current published version
- Success/failure history

If a refresh fails, the system continues serving the last known-good version.

The UI makes this behavior visible so you always know what clients are receiving.

---

### 5. Stream Identity

Each published channel uses a stable stream key.

Clients receive URLs like:

`/stream/<streamKey>`

Stream keys are stable across refreshes. They only regenerate when the published channel identity changes.
This protects DVR mappings and client configurations.

---

### 6. Settings

The Settings page is split into several panels.

**UI Security**
Shows whether UI authentication (login) is currently enabled. This is controlled by the `M3UNDLE_AUTH_ENABLED` environment variable and is read-only in the UI.

**Endpoint Security**
Controls whether the M3U, XMLTV, stream, and HDHomeRun client endpoints require a username and password. You can:

- Enable or disable endpoint authentication
- Set the username and password clients must supply
- Set the `Virtual Tuner ID` used for HDHomeRun tuner ownership and retune/reuse behaviour
- See whether a credential is currently configured

**Refresh Schedule**
Controls how often M3Undle fetches updated lineup and guide data from providers. Options: Manual only, every 1h, 2h, 4h, 6h (default), 12h, or 24h. The startup catch-up toggle controls whether a refresh runs automatically at startup when the last known snapshot is older than the selected interval. Schedule changes take effect immediately without a restart.

**Stream Proxy**
Configures how M3Undle handles live stream relay. Settings are grouped into three areas:

- *Session Limits* — how many streams can play simultaneously, and how long a stream stays open after all viewers disconnect
- *Buffering* — how much memory each stream uses for buffering, and how data is read from the source
- *Reconnect Behaviour* — how quickly a stall is detected and how long M3Undle retries before giving up

Each setting includes a plain-English description, and a help icon explains the purpose and default value in detail.

Stream proxy changes are saved to the database immediately but only take effect after a restart. The page shows the currently active (running) configuration alongside the saved values, and displays a warning banner when they differ. An in-app **Restart M3Undle** button is available once settings have been saved, and shows how many streams are currently active so you know the impact before restarting.

For HDHomeRun-style access, tuner ownership is tracked by the configured `Virtual Tuner ID`, not by remote IP. Re-tuning from the same virtual tuner replaces the prior playback session instead of consuming another tuner slot.

**Downstream Integrations**
Configure automated notification to downstream clients (Jellyfin, Emby, or a generic webhook) after M3Undle publishes a lineup or guide update. Each integration can be:

- Enabled or disabled individually
- Bound to a specific profile or applied to all profiles
- Configured to fire on lineup updates, guide-only updates, or both

M3Undle only fires notifications when a meaningful change is detected — no-op refreshes are suppressed. Recent notification success/failure status is shown for each configured integration.

---

### 7. Channel Mapping

The Channel Mapping page is where you build your output lineup from the provider's channel catalog.

**Group decisions**

Every provider group arrives in **pending** state. You explicitly include or exclude each group:

- **Include** — channels from this group appear in your output
- **Exclude** — group is ignored entirely
- **Pending** — parked; not yet approved for output

Groups marked as new (never seen before) are highlighted so you can review and decide without losing track of them. A "Dismiss new" action clears the flag once you have reviewed.

**Group mode**

For each included group, choose mode:

- **Manual review** (`select`) — only channels you explicitly include are published
- **Auto-update** (`all`) — active channels publish automatically unless explicitly excluded

**Group settings and notifications**

For each included group you can:

- Set a custom **output name** (renames the group in the published M3U)
- Set an **auto-numbering range** (start–end numbers assigned automatically to unnumbered channels)
- Set **Notify** on/off for pending items from that group:
  - notify on: contributes to nav/footer/dashboard review counts
  - notify off: pending items remain reviewable but do not create global badge noise

**Event tracking policy (for PPV/event groups)**

For volatile event groups (PPV grids, rotating sports slots, live-event feeds), set an event tracking policy to control how event channels are handled without weekly manual review:

- **Review** (default) — new events queue for manual review; blank placeholder slots are suppressed
- **Notify only** — surface event arrivals without queuing pending rows and without auto-publishing
- **Auto-add all** — automatically publish every non-placeholder event channel from this group
- **Auto-add populated** — publish only events where the system successfully identified the event content (a matchup name, participant pair, or similar). Channels that are still blank slots or have unrecognizable names are skipped. Stricter than auto-add all.
- **Auto-add matching** — publish only events whose name or content matches configured keywords

For `auto_add_matching`, configure:
- **Keywords** — comma, pipe, or newline-separated terms matched case-insensitively against event display name and content key. A live preview shows which currently-seen events would match.

Note: structured recurring-interest rules (per-team, per-league, per-fighter matching with suppress/notify actions) are stored in the data model and applied during snapshot builds, but do not yet have an editing UI. Use keywords for self-service matching until the interest rules editor ships.

**Per-channel control (manual-review groups)**

Within a group you can select individual channels and set per-channel overrides:

- **Channel number** — explicit `tvg-chno` that takes precedence over auto-numbering
- **Output group** — move a channel to a different output group without changing its source group

Checking a channel in a pending manual-review group also promotes that group to **Include**. This keeps search-driven mapping usable: you can approve only the channels you checked without needing a second group-level click.

**Channel Review Queue**

The dedicated review page (`/channels/review`) lists pending channels across the selected profile and supports:

- Include/exclude selected channels
- Include/exclude all pending channels for a selected provider group
- Search, group filtering, and notify-only filtering
- **Event card view** — a toggle switch in the toolbar. When on, pending channels are grouped by event content key rather than shown as a flat list. Each event card shows the detected sport, league, and how many duplicate feeds exist, and lets you include or exclude all feeds for that event in one action. Useful for PPV and event groups where multiple feeds carry the same content.

**Build Output**

After making changes, use **Build Output** to update the published lineup. Changes to channel settings are pending until the next build.

Technical note: excluded live groups are dropped earlier than snapshot composition. They are skipped during provider-channel sync and deactivated in the live catalog, so exclusion is the state that reduces later processing work. Pending groups still stay indexed so they remain reviewable.

---

### 8. Channels

The Channels page shows the live channels currently in the published lineup.

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
- Published lineup management
- HTTP endpoint publishing
- Visual lineup control

The CLI reduces playlists.
The GUI manages lineups.

---

## Project Direction

The current focus is delivering a stable, fully usable self-hosted lineup manager.

The roadmap will continue expanding the UI's lineup management and operational visibility as the project matures.
