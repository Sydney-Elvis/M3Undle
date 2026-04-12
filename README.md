# M3Undle

A self-hosted lineup manager built for large provider catalogs.

M3Undle helps you take control of massive provider playlists and publish clean, predictable lineups for DVR and media server environments.

Designed for self-hosted systems like NextPVR, Jellyfin, or any client that consumes M3U + XMLTV.

> [!IMPORTANT]
> **Feature Status**
>
> **Included today**
> - CLI tooling (provider fetch, group discovery, M3U/XMLTV filtering)
> - Secure `.env` credential handling
> - Database-backed provider configuration
> - Published lineup versioning with last-known-good lifecycle
> - Group preview (read-only catalog browsing)
> - Group inclusion/exclusion rules and channel filtering (keyword, regex, glob)
> - Group mode: manual review (`select`) or auto-update (`all`) per group
> - Channel numbering assignment, pinning, reorder via Number Manager, and group rename at the output layer
> - Custom `tvg-id` override per channel (lock-gated)
> - New channels inbox / review queue with include/exclude and event card view
> - Event tracking policies for PPV/event groups (`review`, `notify`, `auto_add_all`, `auto_add_populated`, `auto_add_matching`)
> - Structured event interest rules (team/league/sport/fighter/promotion/series) with auto-add, notify, or suppress actions
> - Custom groups — user-defined output groups populated from any provider group or individual channel search
> - EPG source management with multi-source XMLTV merge, priority rules, per-source cadence, and guide publishing
> - Profiles UI — named published lineups with provider membership, published history, and profile detail pages
> - Active profile switching with real-time status feedback
> - Dashboard redesign — health dashboard with Published Output, Published Profiles, Action Items, and Output URLs
> - Downstream integrations — automatic notification to Jellyfin, Emby, or a generic webhook after meaningful lineup or guide changes
> - Configurable refresh schedule (manual, 1h–24h) with startup catch-up behavior
> - HLS browser playback compatibility layer (GeneratedHls + HLS manifest rewriter)
> - CORS support for external network access
> - Compatibility endpoints: `/m3u/`, `/xmltv/`, `/stream/`, HDHomeRun HTTP API, Xtream Codes API
> - Shared live stream proxy with byte-bounded buffering, reconnect handling, and direct-relay fallback for VOD-style routes
> - Stream monitoring UI and stream status endpoints
> - HDHomeRun tuner emulation endpoints (`/discover.json`, `/lineup.json`, `/tune/<streamKey>`) with tuner-slot enforcement keyed by `VirtualTunerId`
>
> **Forthcoming (Alpha 6)**
> - Per-provider gateway/VPN routing (Block and Fallback modes)
> - Xtream Codes auto-detection at provider add time with explicit user mode selection
> - System event badge (nav bar, in-memory, diagnostic visibility)

---

## Why M3Undle Exists

Modern providers often deliver enormous catalogs — 10,000 to 50,000+ channels across multiple regions, languages, sports packages, and temporary event feeds.

Most users only need a small, carefully selected subset of those channels.

Managing that scale can be difficult:

- Massive group lists with mixed languages
- Constantly rotating sports or event feeds
- Temporary PPV channels
- Duplicate regional variations
- Unclear mapping between configuration and published output
- Hard-to-understand numbering behavior

M3Undle is designed to make large catalogs manageable.

It focuses on:

- Clear group selection
- Explicit inclusion rules
- Controlled channel numbering
- Stable channel identity
- Transparent publishing
- Predictable refresh behavior

The goal is simple:

Give you control over what gets published — and make it understandable.

---

## What M3Undle Is

M3Undle is a lineup management system for playlist providers.

It:

- Connects to your provider
- Normalizes channels into canonical identities
- Allows you to define a controlled lineup
- Preserves stream key stability
- Protects DVR mappings from churn
- Publishes compatibility endpoints expected by clients

It is not just a playlist filter.
It is a system for managing playlist catalogs at scale.

---

## Components

### CLI (Available Now)

The CLI was the first component and remains useful for automation and scripting.

It supports:

- Provider playlist fetching
- Group discovery
- M3U filtering
- XMLTV filtering
- Secure `.env` credential handling

See: `docs/CLI.md`

---

### Service + Web UI (Alpha 5)

The service layer is in **Alpha 5** — functional for daily-driver LAN use. See `docs/SERVICE.md` for the full feature list and design notes.

Current Alpha 5 capabilities include:

- Database-backed configuration
- Published lineup versioning with last-known-good lifecycle
- Group preview (read-only catalog browse)
- Group inclusion/exclusion rules and channel filtering (keyword, regex, glob)
- Group mode per group: `manual review` (select only approved channels) or `auto-update` (publish active channels automatically)
- Channel numbering assignment, pinning, and reorder via Number Manager; group rename at the output layer
- Custom `tvg-id` override per channel (lock-gated field in the channel edit dialog)
- New channels inbox / review queue — pending channels surfaced per profile with include/exclude decisions; event card view groups pending channels by event content
- Event tracking policies for volatile groups (PPV/sports grids): `review`, `notify`, `auto_add_all`, `auto_add_populated`, `auto_add_matching`
- Structured recurring event interest rules (team/league/sport/fighter/promotion/series) with auto-add, notify, or suppress actions
- Custom groups — user-defined output groups backed by individual channel picks or linked provider groups, with full mode and tracking-policy support
- EPG Sources UI and API for provider-linked guide sources, test fetches, per-source cadence override, and channel mapping
- Profiles page — list all named published lineups; profile detail shows provider membership, published history, and pending review counts
- Active profile switching — switch the active output profile from the UI with requested/refreshing/completed/failed feedback states
- Dashboard — health dashboard with Published Output (live/movie/series counts), Published Profiles tiles, Action Items (pending review counts), and Output URLs
- Downstream integrations — automatic post-publish notification to Jellyfin, Emby, or a generic webhook; fires only when a meaningful lineup or guide change is detected; recent success/failure visible in Settings
- Configurable refresh schedule in Settings UI (manual, 1h, 2h, 4h, 6h default, 12h, 24h) with startup catch-up behavior
- HLS browser playback compatibility (GeneratedHls + HLS manifest rewriter for `?format=hls` and browser UA fallback)
- CORS support for external network access
- HTTP compatibility endpoints (`/m3u/`, `/xmltv/`, `/stream/`)
- HDHomeRun HTTP endpoints (`/discover.json`, `/lineup.json`, `/lineup.xml`, `/lineup.m3u`, `/lineup_status.json`, `/device.xml`)
- HDHomeRun tuner-slot enforcement and retune/reuse behaviour keyed by endpoint `VirtualTunerId`
- Xtream Codes API (`/player_api.php`, `/get.php`, path-credential stream URLs)
- Shared live stream proxy for `/live`, `/stream`, `/tune`, and `/hdhr/tune`
- Byte-bounded in-memory buffer for late joiners with reconnect handling and slow-subscriber eviction
- Direct relay retained for `/movie`, `/vod`, and `/series`
- Stream monitoring UI plus `/status/streams`, `/status/streams/clients`, and `/status/streams/providers`
- Stream enable/disable control in Settings and provider-level max concurrent stream limits
- UI authentication (`M3UNDLE_AUTH_ENABLED`) and endpoint security with credential management

Planned work (Alpha 6): per-provider gateway/VPN routing, Xtream Codes auto-detection at provider add time, system event badge.

See: `docs/SERVICE.md`

---

## UI Authentication

The web UI supports a simple local authentication model:

- One access level only: authenticated or not authenticated
- No roles or user tiers
- Endpoint authentication is configured separately in the UI

### Setup

Authentication is controlled entirely by environment variables — no UI toggle required.

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_AUTH_ENABLED` | `false` | Set to `true` to require login for the UI and management APIs |
| `M3UNDLE_ADMIN_USER` | `admin` | Admin username/email (used on first startup only) |
| `M3UNDLE_ADMIN_PASSWORD` | *(none)* | **Required** when `M3UNDLE_AUTH_ENABLED=true` and no account exists yet |

On first startup with `M3UNDLE_AUTH_ENABLED=true`, the admin account is created automatically from these variables. On subsequent startups the account already exists — changing the env vars does not affect the stored password (use **Settings → Change Password** instead).

### Behavior

- If `M3UNDLE_AUTH_ENABLED=false` (default), the UI and management APIs are open on your network.
- If `M3UNDLE_AUTH_ENABLED=true`, the UI and `/api/v1/*` management APIs require login.
- Compatibility endpoints can be secured independently from UI auth using **Settings → Endpoint Security**.
- Endpoint credentials are stored hashed in the database and validated with stateless username/password auth.
- `/status` and `/health` remain unauthenticated.

---

## Docker

```bash
docker run -d \
  --name m3undle \
  -p 5004:5004 \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -v ./data:/data \
  -v ./config:/config \
  --restart unless-stopped \
  ghcr.io/sydney-elvis/m3undle:alpha
```

Image: [`ghcr.io/sydney-elvis/m3undle`](https://github.com/Sydney-Elvis/M3Undle/pkgs/container/m3undle)

Port `5004` is the HDHomeRun HTTP tuning port; `8080` serves the web UI, M3U, XMLTV, and compatibility endpoints. Both are always needed.

For HDHomeRun auto-discovery (optional), you also need UDP ports `1900` (SSDP) and `65001` (SiliconDust). See [`docs/DOCKER.md`](docs/DOCKER.md) for full HDHR setup options, Docker networking guidance, and all environment variables.

---

## Compatibility Endpoints

M3Undle publishes endpoints compatible with common clients:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`
- `/stream/<streamKey>`
- `/live/<streamKey>`
- `/tune/<streamKey>`
- `/hdhr/discover.json`
- `/hdhr/lineup.json`
- `/hdhr/lineup.xml`
- `/hdhr/lineup.m3u`
- `/hdhr/lineup_status.json`
- `/hdhr/device.xml`
- `/hdhr/tune/<streamKey>`

Live routes use the shared stream proxy and keep provider credentials hidden from clients. Movie, VOD, and series routes remain direct relay paths.

Operational stream visibility is available via the Streams page in the UI and the status endpoints `/status/streams`, `/status/streams/clients`, and `/status/streams/providers`.

Legacy HDHomeRun root aliases (`/discover.json`, `/lineup.json`, etc.) are still available for compatibility.

Automatic discovery support:
- SSDP/UPnP (`UDP 1900`)
- SiliconDust discovery (`UDP 65001`)
- Discovery is disabled by default; manual add works without discovery — see [`docs/DOCKER.md`](docs/DOCKER.md) for setup steps

See: `docs/design/HTTP_COMPATIBILITY.md`

---

## Design Principles

- Explicit over implicit
- Controlled over automatic
- Transparent over opaque
- Scalable for large provider catalogs
- Self-hosted and privacy-respecting

---

## Project Direction

The current focus is delivering a stable, fully usable self-hosted lineup manager.

The roadmap will continue expanding M3Undle's lineup management and publishing capabilities as the project matures.

---

## License

Project license: Apache License 2.0
See `LICENSE` for details.

---

## Status

**CLI:** Stable and usable.

**Service + Web UI:** **Alpha 5** — functional for daily-driver LAN use. Not production-ready. Active development continues toward Alpha 6 and Beta.
