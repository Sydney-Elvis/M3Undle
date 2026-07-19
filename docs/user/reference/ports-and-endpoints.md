# Ports and Endpoints

Use the URLs displayed by M3Undle rather than assuming that every deployment publishes the same host ports. The observed instance served the web UI, media output, health checks, and HDHomeRun HTTP endpoints on port `8080`.

## User interface and media output

| Purpose | Observed path | Where to find it |
|---|---|---|
| Web interface | `/` | Open M3Undle in a browser |
| M3U playlist | `/m3u/m3undle.m3u` | Dashboard → **Endpoints → M3U Playlist** |
| XMLTV guide | `/xmltv/m3undle.xml` | Dashboard → **Endpoints → XMLTV Guide** |
| Stream monitor | `/streams` | **Streams** |

The dashboard provides read-only URL fields and copy actions for M3U and XMLTV. Both endpoints serve the currently active profile.

## Health endpoints

| Purpose | Path | Observed response |
|---|---|---|
| Liveness | `/livez` | `200`, plain-text `Healthy` |
| Readiness | `/readyz` | `200`, JSON with `ready: true` and `status: Healthy` |

Use liveness to confirm that the process is running and readiness to confirm that it is ready for normal traffic.

## HDHomeRun endpoints

Open **HDHomeRun** and copy the generated values under **HDHR Endpoints**. On the observed instance they used these paths:

| UI label | Path |
|---|---|
| Discover JSON | `/hdhr/discover.json` |
| Device XML | `/hdhr/device.xml` |
| Lineup JSON | `/hdhr/lineup.json` |
| Lineup Status | `/hdhr/lineup_status.json` |

The discovery response advertises the base URL, lineup URL, device identity, and tuner count. Use **Discover JSON** for clients that support manual HDHomeRun entry.

This deployment also answered compatibility aliases such as `/discover.json` and `/lineup.json`, but the `/hdhr/` URLs above are the values shown by the UI and should be preferred.

## Xtream

The dashboard shows whether Xtream access is secured, and **Settings → Security → Advanced Options** can enable or disable the protocol. The running UI does not display a copyable Xtream API path, so this page does not invent one. Use the client-specific guide for the version you are configuring.

## Choosing the correct base URL

Open **Settings → Endpoint URLs** to review three optional, read-only overrides:

- **Host / Public Base URL** from `M3UNDLE_PUBLIC_BASE_URL`
- **Docker Base URL** from `M3UNDLE_DOCKER_BASE_URL`
- **External / Reverse Proxy URL** from `M3UNDLE_EXTERNAL_BASE_URL`

These values control alternate URLs offered by dashboard and HDHomeRun copy actions. They are set through environment variables and take effect after restart.

For another container on the same Docker network, the page recommends a Compose service address such as `http://m3undle:8080`. For users behind a reverse proxy, configure the externally reachable HTTPS URL. If no override is set, M3Undle derives a URL from the browser or detected environment; verify that it is reachable from the client before copying it.

## Port `5004`

The documented, recommended default install (see [Install with Docker](../getting-started/install-with-docker.md)) publishes port `5004` specifically for HDHomeRun-style tuning, separate from `8080`. The instance this page was validated against was configured differently — it didn't have `5004` published, and its HDHomeRun HTTP endpoints worked on `8080` instead, which is possible because the whole application listens on every port it's given, not just specific routes per port.

Don't hard-code either port. Whatever your deployment's actual configuration is, the authoritative value is the **Discover JSON** URL shown on that instance's own **HDHomeRun** page — use that.
