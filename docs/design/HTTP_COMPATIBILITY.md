# HTTP Compatibility

## Goals
- Clients can consume M3U + XMLTV + stream URLs from this service reliably.
- The service controls channel identity and numbering to avoid DVR/guide churn.
- The playlist and stream URL contract is stable even if internal implementation changes.
- Provider credentials are never exposed to clients. Stream relay is a security requirement.

## Endpoint Naming

The service uses lineup-scoped endpoint paths. In Core, the lineup name is fixed to `m3undle`:

- `/m3u/m3undle.m3u`
- `/xmltv/m3undle.xml`
- `/stream/<streamKey>`

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
  - `lineups` is always a list. Core always has exactly one entry (`"m3undle"`).
    Pro extends this with additional named lineups.
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
  - Resolves streamKey -> canonical channel in active snapshot
  - Serves playable stream for that channel
  - Must be resilient:
    - no service crashes due to upstream failures
    - clear HTTP failure for that request if upstream fails
  - **MUST relay the stream — MUST NOT redirect to the upstream provider URL.**
    Provider stream URLs typically embed credentials (`http://provider/{username}/{password}/stream.ts`).
    An HTTP 302 redirect would deliver raw credentials to every client that follows the stream URL.
    Relay is a security contract, not an implementation detail. This MUST NOT be changed to a redirect.

## Authentication
Auth infrastructure (ASP.NET Core Identity) is present in the codebase. Whether to enable it is configured at first-run setup. Compatibility endpoints (`/m3u/`, `/xmltv/`, `/stream/`) are designed to be accessible without auth to support LAN clients. The web UI can optionally require login.
