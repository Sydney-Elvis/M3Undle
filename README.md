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
> - Provider switching with snapshot lifecycle
> - Group preview (read-only catalog browsing)
> - Compatibility endpoints: `/m3u/`, `/xmltv/`, `/stream/`, HDHomeRun HTTP API
> - Stream relay proxy (relay-only, no buffering)
> - Web UI for provider management (Pre-Alpha)
> - HDHomeRun tuner emulation endpoints (`/discover.json`, `/lineup.json`, `/tune/<streamKey>`)
> 
> **Forthcoming**
> - Group-based inclusion rules
> - Channel numbering controls
> - Advanced channel filtering workflows
> - Additional Service/Web UI hardening toward Alpha

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
It is a system for managing Playlist catalogs at scale.

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

### Service + Web UI (Pre-Alpha)

The service layer is in **Pre-Alpha** — actively building toward the Alpha checkpoint (minimal, functional proof-of-concept). Still incomplete and will change significantly.

Current Pre-Alpha work includes:

- Database-backed configuration
- Provider switching with snapshot lifecycle
- Group preview (read-only catalog browse)
- HTTP compatibility endpoints (`/m3u/`, `/xmltv/`, `/stream/`)
- HDHomeRun HTTP endpoints (`/discover.json`, `/lineup.json`, `/lineup.xml`, `/lineup.m3u`, `/lineup_status.json`, `/device.xml`)
- Stream relay proxy (relay-only, no buffering)
- Web UI for provider management

Future releases will add: group-based inclusion rules, channel numbering, filtering, and more.

See: `docs/SERVICE.md`

---

## UI Authentication

The web UI supports a simple local authentication model:

- One access level only: authenticated or not authenticated
- No roles or user tiers
- Compatibility endpoints remain anonymous for LAN clients

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
- The following endpoints remain anonymous by design:
  - `/m3u/m3undle.m3u`
  - `/xmltv/m3undle.xml`
  - `/stream/<streamKey>` and related compatibility stream paths
  - `/status`
  - `/health`

---

## Docker

```bash
docker run -d \
  --name m3undle \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -v ./data:/data \
  -v ./config:/config \
  --restart unless-stopped \
  ghcr.io/sydney-elvis/m3undle:alpha
```

Image: [`ghcr.io/sydney-elvis/m3undle`](https://github.com/Sydney-Elvis/M3Undle/pkgs/container/m3undle)

See [`docs/DOCKER.md`](docs/DOCKER.md) for Compose example, volume layout, and all environment variables.

---

## Compatibility Endpoints

M3Undle publishes endpoints compatible with common clients:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`
- `/stream/<streamKey>`
- `/discover.json`
- `/lineup.json`
- `/lineup.xml`
- `/lineup.m3u`
- `/lineup_status.json`
- `/device.xml`
- `/tune/<streamKey>`

Automatic discovery support:
- SSDP/UPnP (`UDP 1900`)
- SiliconDust discovery (`UDP 65001`)
- Discovery is disabled by default; manual add works without discovery

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

Advanced features may be introduced in future releases as the project matures.

---

## License

Project license: Apache License 2.0
See `LICENSE` for details.

---

## Status

**CLI:** Stable and usable.

**Service + Web UI:** **Pre-Alpha** — actively building toward the Alpha checkpoint. Foundational concepts being proven. Not production-ready. Will change significantly.
