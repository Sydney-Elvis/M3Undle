# Environment Variables

Most Docker installs should use very few environment variables. If you're not sure whether you need one below, you probably don't — most day-to-day configuration (endpoint security, HDHomeRun behavior, observability, stream proxy tuning) is managed from the web UI once M3Undle is running, and is persisted in the database rather than requiring a container restart.

## Required / Recommended

| Variable | Default | Description |
|---|---|---|
| `TZ` | host timezone | Timezone for log timestamps (e.g. `America/New_York`, `Europe/London`, `UTC`) |
| `M3UNDLE_ENCRYPTION_KEY` | *(none)* | **Required for Xtream Codes providers** unless `M3UNDLE_ENCRYPTION_KEYS` is set. Base64-encoded 32-byte AES key used to encrypt passwords at rest. Generate with `openssl rand -base64 32`. Keep this secret — treat it like a master password. |
| `M3UNDLE_ENCRYPTION_KEYS` | *(none)* | Rotatable alternative to `M3UNDLE_ENCRYPTION_KEY` — a comma-separated `keyId:base64key` list. The **first** entry is the active key used for new encryption; every entry is usable for decryption. Takes precedence over `M3UNDLE_ENCRYPTION_KEY` if both are set. See [Manage Providers](../guides/manage-providers.md) for the full rotation workflow. |

## Optional — Authentication

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_AUTH_ENABLED` | `false` | Set to `true` to require login for the UI and management APIs. |
| `M3UNDLE_ADMIN_USER` | `admin` | Admin username/email. Used only on first startup when no account exists. |
| `M3UNDLE_ADMIN_PASSWORD` | *(none)* | **Required** when `M3UNDLE_AUTH_ENABLED=true` and no admin account exists yet. Also required when running one-time password recovery. |
| `M3UNDLE_ADMIN_PASSWORD_RESET` | `false` | One-time recovery switch. Set to `true` to force-reset the existing admin password from `M3UNDLE_ADMIN_PASSWORD` on startup, then set back to `false`. |

**Password recovery workflow** (lost admin password):

1. Set `M3UNDLE_ADMIN_PASSWORD` to the new password.
2. Set `M3UNDLE_ADMIN_PASSWORD_RESET=true`.
3. Restart the container once and log in with the new password.
4. Set `M3UNDLE_ADMIN_PASSWORD_RESET=false` and restart again.

Endpoint security (M3U/XMLTV/stream/HDHR username/password auth) is managed in **Settings → Security → Endpoint Credentials** and stored in the database — see [Security](../concepts/security.md).

## Optional — Provider Features

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_M3U_DIR` | `/m3u_data` | Directory the file browser exposes when adding a provider from a local `.m3u` file. Mount a host directory to that path, or set this variable to a different container path and mount there. |

## Optional — Observability

Most users should configure metrics from **Settings → Observability** after first startup — see [Observability](../concepts/observability.md). The variables below are useful for managed deployments or bootstrap defaults.

| Variable | Default | Description |
|---|---|---|
| `M3Undle__Observability__Metrics__Enabled` | `true` | Master switch for the Prometheus-compatible scrape endpoint. |
| `M3Undle__Observability__Metrics__Path` | `/metrics` | Scrape endpoint path. |
| `M3Undle__Observability__Metrics__Mode` | `LocalOnly` | Metrics access mode: `Disabled`, `LocalOnly`, `Token`, or `Public`. |
| `M3Undle__Observability__Metrics__EnableChannelLabels` | `false` | Reserved guard for channel-level labels. Leave disabled unless you understand the Prometheus cardinality impact. |
| `M3Undle__Observability__Metrics__LocalAllowedCidrs__0` | *(empty)* | First CIDR allowed in `LocalOnly` mode, e.g. `192.168.1.0/24`. Add more with `__1`, `__2`, etc. |

Example:

```yaml
environment:
  M3Undle__Observability__Metrics__Mode: "LocalOnly"
  M3Undle__Observability__Metrics__LocalAllowedCidrs__0: "192.168.1.0/24"
```

## Optional — Stream Relay Tuning

Most stream proxy settings are managed from **Settings → Streaming**, and restart-required changes are tracked in the UI. The variables below are advanced startup/config controls for behavior not exposed as a normal UI field.

| Variable | Default | Description |
|---|---|---|
| `M3Undle__Streaming__ProviderMaxConcurrentUpstreams` | *(unset)* | Optional global provider-upstream cap used when a provider doesn't have its own max concurrent stream limit. |
| `M3Undle__Streaming__Reconnect__ContentStallTimeout` | `00:00:08` | MPEG-TS content-stall timeout. Real TS content resets this timer; null-only packets don't, so prolonged CDN gaps can reconnect before the generic read-stall timeout. |
| `M3Undle__Streaming__Reconnect__StrikeCooldown` | `00:05:00` | Cooldown after retry exhaustion for a failing source, used to avoid provider retune storms. |
| `M3Undle__Streaming__GeneratedHls__Directory` | `/data/hls-work` | Where generated-HLS scratch files (rolling playlists/segments) are written. A relative value is resolved under the configured data directory (defaults to `/data` in the container); an absolute path is used as-is and does **not** need to live under the data directory — useful for mounting it on separate storage (e.g. `/hls-work`). The directory is created automatically if it doesn't exist. See [Browser Playback](../guides/browser-playback.md). |

## App Settings

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `5004;8080` | Ports the app listens on inside the container. `5004` is used for HDHomeRun-compatible tuning and `8080` for the web UI and general endpoints. |
| `M3Undle__Refresh__TimeoutMinutes` | `5` | Provider fetch timeout |
| `M3Undle__Refresh__StartupDelaySeconds` | `30` | Delay before first refresh after startup |
| `M3Undle__Snapshot__RetentionCount` | `3` | Number of snapshots to retain |
| `M3Undle__Cors__ApplicationAllowedOrigins__0` | *(unset)* | First allowed CORS origin for the application surface (`/api`, UI, `/Account/*`). Add more with `__1`, `__2`, etc. |
| `M3UNDLE_DATA_DIR` | `/data` (in image) | Override the data directory (database, logs, snapshots). Rarely needed with the standard Docker volume layout. |
| `M3UNDLE_CONFIG_DIR` | `/config` (in image) | Path M3Undle looks in for `config.yaml` and `/config/.env`. |

## Optional — HDHomeRun

Most users can skip this entire section. Use **Settings → HDHomeRun** for normal setup and day-to-day changes — see [HDHomeRun Compatibility](../concepts/hdhomerun-compatibility.md). The environment variables below are optional advanced overrides, not required setup.

Most HDHR behavior is configurable in the UI. The main exceptions are:

- `M3UNDLE_HDHR_ENABLED`, which can force HDHR off at startup
- `M3Undle__HdHomeRun__FriendlyName`, which is currently env/config only

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_HDHR_ENABLED` | `true` | Master switch for all HDHomeRun endpoints. Normally leave this unset. Set to `false` only to disable HDHR completely — this overrides the UI setting at startup, and also prevents normal HDHR management from the UI until removed. |
| `M3Undle__HdHomeRun__DiscoveryEnabled` | `true` | Enables SSDP and SiliconDust network discovery. Requires UDP ports — see [HDHomeRun-Compatible Clients](../clients/hdhomerun-compatible-clients.md) for Docker networking details. |
| `M3Undle__HdHomeRun__SsdpEnabled` | `true` | Controls the SSDP/UPnP listener on UDP 1900. |
| `M3Undle__HdHomeRun__SiliconDustDiscoveryEnabled` | `true` | Controls the SiliconDust discovery listener on UDP 65001. |
| `M3Undle__HdHomeRun__TunerCount` | `6` | Virtual tuner count advertised when no provider limit or UI override is in effect — advertised only, not a server-side cap. Clients allocate against it and often reserve one tuner for EPG, so keep it well above 1. |
| `M3Undle__HdHomeRun__AdvertisedBaseUrl` | *(auto-detect)* | Base URL returned in `discover.json` and discovery responses (e.g. `http://192.168.1.50:5004`). Normally leave blank; set only for advanced Docker NAT, LAN discovery, or reverse-proxy scenarios. |
| `M3Undle__HdHomeRun__FriendlyName` | `M3Undle HDHomeRun` | Device name shown in client apps. Env/config-only, not a web UI field. |

Quick rule: if you're running Docker on a normal home server, don't add HDHR env vars just because they exist. Bring the app up first, then use **Settings → HDHomeRun**. The main exception is `M3UNDLE_HDHR_ENABLED=false`.

## Optional — Reverse Proxy

| Variable | Default | Description |
|---|---|---|
| `M3Undle__ReverseProxy__ForwardLimit` | `1` | Max entries to process from `X-Forwarded-*` headers. |
| `M3Undle__ReverseProxy__TrustedProxies` | *(empty)* | Comma-separated list of trusted proxy IPs (e.g. `192.168.1.1`). Forwarded headers are only honored from these IPs, `TrustedNetworks`, or loopback. |
| `M3Undle__ReverseProxy__TrustedNetworks` | *(empty)* | Comma-separated CIDR blocks (e.g. `10.0.0.0/8`). |
