# HTTP Compatibility

## Goals
- Clients can consume M3U + XMLTV + stream URLs from this service reliably.
- The service controls channel identity and numbering to avoid DVR/guide churn.
- The playlist and stream URL contract is stable even if internal implementation changes.
- Provider credentials are never exposed to clients. Stream relay is a security requirement.

## Scope Note
- This document defines the external HTTP contract for client consumption and compatibility.
- Blazor Server UI internals may call application services directly instead of issuing loopback HTTP calls to `/api/v1/*`.
- Internal implementation choices MUST NOT change compatibility endpoint behavior.

## Endpoint Naming

The service uses lineup-scoped endpoint paths. In Core, the lineup name is fixed to `m3undle`:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`
- `/stream/<streamKey>`
- `/hdhr/discover.json`
- `/hdhr/lineup.json`
- `/hdhr/lineup.xml`
- `/hdhr/lineup.m3u`
- `/hdhr/lineup_status.json`
- `/hdhr/device.xml`
- `/hdhr/tune/<streamKey>`

Legacy aliases (`/discover.json`, `/lineup.json`, `/lineup.xml`, `/lineup.m3u`, `/lineup_status.json`, `/device.xml`, `/tune/<streamKey>`) remain available for compatibility.

## Endpoints

### Health
- GET /health
  - 200 if process is up
  - body may be minimal

### Status (machine-readable)
- GET /status
  - JSON payload:
    ```json
    {
      "status": "ok" | "degraded" | "no_active_snapshot",
      "lineups": [
        {
          "name": "m3undle",
          "status": "ok" | "degraded" | "no_active_snapshot",
          "activeProvider": { "providerId": "...", "name": "..." } | null,
          "activeSnapshot": {
            "snapshotId": "...",
            "profileId": "...",
            "createdUtc": "...",
            "channelCountPublished": 0
          } | null,
          "lastRefresh": {
            "status": "ok" | "running" | "fail",
            "startedUtc": "...",
            "finishedUtc": "..." | null,
            "channelCountSeen": 0 | null,
            "errorSummary": "..." | null
          } | null
        }
      ]
    }
    ```
  - `lineups` is always a list. Current core behavior has exactly one entry (`"m3undle"`).
  - Top-level `status` summarises across all lineups (`"ok"` if any lineup is ok).

### Playlist (M3U)
- GET /m3u/m3undle.m3u
  - MUST include:
    - #EXTM3U url-tvg="http(s)://<host>/xmltv/m3undle.xml" x-tvg-url="..."
  - Each channel entry SHOULD include:
    - tvg-chno (from provider, if present)
    - tvg-name (canonical display name)
    - tvg-id (stable ID or mapped xmltv id)
    - tvg-logo (canonical or provider)
    - group-title (canonical group)
  - Each channel entry MUST point stream URL to:
    - http(s)://<host>/stream/<streamKey>

### Guide (XMLTV)
- GET /xmltv/m3undle.xml
  - XMLTV aligned with published channels
  - Channel ids should be stable over time for canonical channels

### Stream
- GET /stream/<streamKey>
- GET /tune/<streamKey>
  - Resolves streamKey -> canonical channel in active snapshot
  - Serves playable stream for that channel
  - Must be resilient:
    - no service crashes due to upstream failures
    - clear HTTP failure for that request if upstream fails
  - **MUST relay the stream — MUST NOT redirect to the upstream provider URL.**
    Provider stream URLs typically embed credentials (`http://provider/{username}/{password}/stream.ts`).
    An HTTP 302 redirect would deliver raw credentials to every client that follows the stream URL.
    Relay is a security contract, not an implementation detail. This MUST NOT be changed to a redirect.

### HDHomeRun HTTP API
- GET `/hdhr/discover.json`
  - Returns stable device identity metadata (`DeviceID`, `DeviceAuth`, `FriendlyName`, `ModelNumber`, `BaseURL`, `LineupURL`, `TunerCount`).
- GET `/hdhr/lineup.json`
  - Returns live channels from the active snapshot with stable `GuideNumber`, `GuideName`, and M3Undle-owned tune URLs.
- GET `/hdhr/lineup.xml`
  - XML lineup equivalent of `/lineup.json`.
- GET `/hdhr/lineup.m3u`
  - M3U lineup equivalent of `/lineup.json`.
- GET `/hdhr/lineup_status.json`
  - Returns lineup readiness and channel count.
- GET/POST `/hdhr/lineup.post`
  - No-op compatibility endpoint expected by some HDHomeRun clients.
- GET `/hdhr/device.xml`
  - UPnP device description used by SSDP/manual client probes.

### HDHomeRun Tuner Semantics
- HDHomeRun tune requests consume tuner slots from the configured `TunerCount`.
- Tuner ownership is keyed by the resolved endpoint-binding `VirtualTunerId`, not by remote IP.
- A new `/hdhr/tune/<streamKey>` request on the same `VirtualTunerId` replaces the prior playback from that tuner instead of consuming another slot.
- Requests from distinct `VirtualTunerId` values can consume separate tuner slots up to the configured `TunerCount`.
- Requests beyond the configured tuner count return a busy/unavailable response.

### Xtream Codes API

M3Undle exposes a compatible Xtream Codes API surface, enabling clients such as TiviMate,
GSE Player, and IPTV Smarters to connect using the endpoint-security username and password.

#### player_api.php

- `GET/POST /player_api.php` (no `action`) — same as `get_account_info`
- `GET/POST /player_api.php?action=get_account_info` — returns `user_info` and `server_info` blocks
- `GET/POST /player_api.php?action=get_live_categories` — live channel groups
- `GET/POST /player_api.php?action=get_vod_categories` — VOD groups
- `GET/POST /player_api.php?action=get_series_categories` — series groups
- `GET/POST /player_api.php?action=get_live_streams[&category_id=N]` — live channel list
- `GET/POST /player_api.php?action=get_vod_streams[&category_id=N]` — VOD list
- `GET/POST /player_api.php?action=get_series[&category_id=N]` — series list

Authentication is via query-string `username` and `password` parameters, matching the
endpoint-security credentials configured in **Settings → Endpoint Security**.
For security, `user_info.password` in `get_account_info` responses is always returned as an
empty string (the credential is never reflected back in response payloads).

Stream IDs returned in the list responses are stable 31-bit integers derived from the channel's
stream key (MD5, first 4 bytes). Category IDs are derived the same way from the group title.
Both are stable for the lifetime of a snapshot and may change between snapshot refreshes.

#### get.php

- `GET /get.php` — serves the M3U playlist. Accepts the same `username`/`password` query
  parameters as `player_api.php`. Equivalent to `/m3u/m3undle.m3u` with Xtream-style auth.

#### Path-credential streaming

Xtream clients construct tune URLs by embedding credentials in the path:

```
GET /live/{username}/{password}/{streamId}[.ts][/{*tail}]
GET /movie/{username}/{password}/{streamId}[.mp4][/{*tail}]
GET /series/{username}/{password}/{streamId}[.mkv][/{*tail}]
```

The `streamId` is the integer returned by `get_live_streams` / `get_vod_streams` / `get_series`.
The optional file extension and trailing wildcard segments are accepted and ignored — they exist
for player compatibility only. The stream itself is served through the same shared proxy or direct
relay as all other stream routes.

Credential validation follows the same rules as the standard endpoint filter: when endpoint
security is disabled, any credentials in the path are accepted and the default profile is used.

### Discovery (optional)
- SSDP / UPnP listener on UDP `1900`
- SiliconDust discovery listener on UDP `65001`
- Discovery uses the same device identity and base URL as manual HTTP endpoints.
- Discovery is disabled by default; manual add via `/discover.json` remains available.

## Authentication
UI authentication and client endpoint authentication are independent:

- UI auth (`M3UNDLE_AUTH_ENABLED`) controls access to the web UI and management APIs.
- Endpoint auth is configured in the web UI (**Settings → Endpoint Security**) and stored in the database.
- Endpoint auth settings also carry the HDHomeRun `Virtual Tuner ID` used for tuner-slot ownership.
- When endpoint auth is enabled, M3U/XMLTV/stream/HDHR endpoints require stateless username/password access (no redirects, no session-cookie requirements).
- When endpoint auth is disabled, endpoint behavior remains open as before.
