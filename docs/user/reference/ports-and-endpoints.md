# Ports and Endpoints

Use the URLs displayed by M3Undle rather than assuming that every deployment publishes the same host ports. By default, the web UI, media output, health checks, and HDHomeRun HTTP endpoints are all served on port `8080`.

## User interface and media output

| Purpose | Path | Where to find it |
|---|---|---|
| Web interface | `/` | Open M3Undle in a browser |
| M3U playlist | `/m3u/m3undle.m3u` | Dashboard → **Endpoints → M3U Playlist** |
| XMLTV guide | `/xmltv/m3undle.xml` | Dashboard → **Endpoints → XMLTV Guide** |
| Stream monitor | `/streams` | **Streams** |

The dashboard provides read-only URL fields and copy actions for M3U and XMLTV. Both endpoints serve the currently active profile.

## Health endpoints

| Purpose | Path | Response |
|---|---|---|
| Liveness | `/livez` | `200`, plain-text `Healthy` |
| Readiness | `/readyz` | `200`, JSON with `ready: true` and `status: Healthy` |

Use liveness to confirm that the process is running and readiness to confirm that it is ready for normal traffic.

## Diagnostics APIs

Authenticated JSON diagnostics for operators, requiring existing admin/UI authorization — not exposed through metrics-token authentication. Use these when investigating provider fetches, stream sharing, lineup publish history, or EPG source behavior; use `/metrics` (see [Metrics](metrics.md)) for time-series monitoring instead.

If **UI Authentication** is disabled (the default — see [Security](../concepts/security.md)), these endpoints are open to anyone who can reach the server, and the plain `curl` examples below work as shown. If UI Authentication is enabled, they require a real authenticated session — pass your login cookie (e.g. `curl -b cookies.txt`, saved from a browser or a prior `curl -c cookies.txt` login request) rather than expecting a bare `curl` call to work.

| Endpoint | Purpose |
|---|---|
| `GET /api/admin/diagnostics/providers` | Provider fetch and channel diagnostics |
| `GET /api/admin/diagnostics/streams` | Active and recently ended stream sessions, clients, and upstreams |
| `GET /api/admin/diagnostics/lineup` | Published lineup status and recent snapshots |
| `GET /api/admin/diagnostics/epg` | EPG source, fetch, and mapping diagnostics |

```bash
curl -s http://<host>:8080/api/admin/diagnostics/providers
curl -s http://<host>:8080/api/admin/diagnostics/streams
curl -s http://<host>:8080/api/admin/diagnostics/lineup
curl -s http://<host>:8080/api/admin/diagnostics/epg
```

### Test-mode RCA bundle

When `M3UNDLE_TEST_MODE=true`, an additional `GET /debug/streams/rca` endpoint (UI admin auth required) returns a compact root-cause-analysis bundle: active/recent stream sessions, clients, provider streams, cooldowns, and recent stream diagnostic events in one payload. Combine it with the container's application logs when investigating playback stalls or provider failures.

## HDHomeRun endpoints

Open **HDHomeRun** and copy the generated values under **HDHR Endpoints**. These paths are used:

| UI label | Path |
|---|---|
| Discover JSON | `/hdhr/discover.json` |
| Device XML | `/hdhr/device.xml` |
| Lineup JSON | `/hdhr/lineup.json` |
| Lineup Status | `/hdhr/lineup_status.json` |

The discovery response advertises the base URL, lineup URL, device identity, and tuner count. Use **Discover JSON** for clients that support manual HDHomeRun entry.

M3Undle also answers compatibility aliases such as `/discover.json` and `/lineup.json`, but the `/hdhr/` URLs above are the values shown by the UI and should be preferred.

## Xtream

The dashboard shows whether Xtream access is secured, and **Settings → Security → Advanced Options** can enable or disable the protocol. M3Undle's UI doesn't display a copyable Xtream API path, so this page doesn't invent one. Use the client-specific guide for the version you are configuring.

## Choosing the correct base URL

Open **Settings → Endpoint URLs** to review three optional, read-only overrides:

- **Host / Public Base URL** from `M3UNDLE_PUBLIC_BASE_URL`
- **Docker Base URL** from `M3UNDLE_DOCKER_BASE_URL`
- **External / Reverse Proxy URL** from `M3UNDLE_EXTERNAL_BASE_URL`

These values control alternate URLs offered by dashboard and HDHomeRun copy actions. They are set through environment variables and take effect after restart.

For another container on the same Docker network, the page recommends a Compose service address such as `http://m3undle:8080`. For users behind a reverse proxy, configure the externally reachable HTTPS URL. If no override is set, M3Undle derives a URL from the browser or detected environment; verify that it is reachable from the client before copying it.

## Port `5004`

The documented, recommended default install (see [Install with Docker](../getting-started/install-with-docker.md)) publishes port `5004` specifically for HDHomeRun-style tuning, separate from `8080`. Some deployments don't publish `5004` at all — HDHomeRun HTTP endpoints still work on `8080` in that case, since the whole application listens on every port it's given, not just specific routes per port.

Don't hard-code either port. Whatever your deployment's actual configuration is, the authoritative value is the **Discover JSON** URL shown on that instance's own **HDHomeRun** page — use that.
