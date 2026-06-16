# Docker

M3Undle is published to GitHub Container Registry (GHCR).

Image: `ghcr.io/sydney-elvis/m3undle`

---

## Quick Start

Create a directory for M3Undle, place a `compose.yaml` inside it, then run:

```bash
mkdir m3undle && cd m3undle
# create compose.yaml (see below)
mkdir config data
docker compose up -d
```

### compose.yaml

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:beta
    container_name: m3undle
    user: "${PUID}:${PGID}"
    ports:
      - "5004:5004"
      - "8080:8080"
      # Uncomment these for HDHomeRun auto-discovery (see HDHomeRun section below):
      # - "1900:1900/udp"   # SSDP / UPnP
      # - "65001:65001/udp" # SiliconDust discovery
    environment:
      TZ: America/New_York
      # Required if you use Xtream Codes providers (encrypted password storage).
      # Generate with: openssl rand -base64 32
      #   Linux/macOS: openssl rand -base64 32
      #   Windows PowerShell: [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }) -as [byte[]])
      M3UNDLE_ENCRYPTION_KEY: "your-base64-32-byte-key"
      # Uncomment to require login for the web UI:
      # M3UNDLE_AUTH_ENABLED: "true"
      # M3UNDLE_ADMIN_USER: admin
      # M3UNDLE_ADMIN_PASSWORD: "choose-a-strong-password"
      # M3UNDLE_ADMIN_PASSWORD_RESET: "false" # set "true" once to recover a forgotten admin password
    volumes:
      - ./config:/config
      - ./data:/data
    restart: unless-stopped
```

Create a `.env` file next to `compose.yaml` with your user and group IDs:

```env
PUID=1000
PGID=1000
```

Find your numeric UID and GID with these commands:

```bash
# Get your numeric UID and GID
id -u        # prints your UID (e.g. 1000)
id -g        # prints your GID (e.g. 1000)

# For a specific user:
id -u <username>
id -g <username>

# Sanitized example output:
$ id
uid=1000(<your-username>) gid=1000(<your-username>) groups=1000(<your-username>),998(docker)
# PUID=1000  PGID=1000
```

Then open `http://<host>:8080`.

If you use HDHomeRun-compatible clients such as NextPVR, keep `5004:5004` published. `5004` is the HDHomeRun HTTP tuning port, while `8080` serves the web UI and general compatibility endpoints.

### docker run

```bash
mkdir -p m3undle/config m3undle/data && cd m3undle

docker run -d \
  --name m3undle \
  --user "$(id -u):$(id -g)" \
  -p 5004:5004 \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -e M3UNDLE_ENCRYPTION_KEY="your-base64-32-byte-key" \
  -v ./config:/config \
  -v ./data:/data \
  --restart unless-stopped \
  ghcr.io/sydney-elvis/m3undle:beta
```

---

## Volumes

| Mount | Required | Purpose |
|---|---|---|
| `/config` | Yes | `config.yaml` and `.env` credential file — files you edit |
| `/data` | Yes | SQLite database, snapshots, log files, runtime state, and generated browser playback files |
| `/m3u` (or any path) | No | Local `.m3u` files browsable via the file browser. Set `M3UNDLE_M3U_DIR` to the container path. |

Both `/config` and `/data` are required for data to persist across container restarts.

**Why bind mounts?** Bind mounts put files in a known place on the host. You can edit `config.yaml` with any editor, inspect logs, or wipe the data directory without going through Docker commands.

### Named volumes (alternative)

If you prefer Docker-managed storage, named volumes work too — replace the bind mounts:

```yaml
volumes:
  - m3undle_config:/config
  - m3undle_data:/data

# add at the bottom of compose.yaml:
volumes:
  m3undle_config:
  m3undle_data:
```

Named volumes avoid the ownership requirement on Linux and can give better I/O performance, but you can't browse the files directly from the host.

### Generated HLS Storage Sizing

Browser playback fallback (`?format=hls` or browser UA fallback) writes rolling HLS playlists/segments under `/data/hls-work` by default.

Estimate required storage with:

`required_bytes ~= concurrent_generated_hls_sessions * average_bitrate_bytes_per_second * retained_seconds`

Equivalent Mbps form:

`required_gb ~= concurrent_sessions * average_mbps * retained_seconds / 8 / 1024`

Recommended planning:

- Start with a **2x to 4x safety multiplier** over the raw estimate.
- Small/home usage: allocate **2-5 GB**.
- Multi-user usage: allocate **10-20 GB** or more depending on bitrate and concurrency.

Examples:

- 5 sessions at 8 Mbps, 60 seconds retained: raw ~300 MB, recommended **1-2 GB**.
- 10 sessions at 12 Mbps, 90 seconds retained: raw ~1.35 GB, recommended **3-5 GB**.

In v1 these playlists are rolling/sliding, session-scoped, and cleaned on session end/inactivity; full VOD retention is not used.

If you expect heavy browser playback and want this scratch space on a separate disk, mount storage directly at the default internal path:

```yaml
volumes:
  - ./config:/config
  - ./data:/data
  - ./hls-work:/data/hls-work
```

---

## Config File Integration

Place a `config.yaml` (and optionally a `.env` credential file) in the `config/` directory (mapped to `/config`). M3Undle will find them automatically — no extra environment variables required.

```
m3undle/
  compose.yaml
  config/
    config.yaml    ← provider definitions
    .env           ← credentials (never commit this)
  data/            ← managed by the container
```

See [spec/config_spec.md](spec/config_spec.md) for the config file format.

---

## Environment Variables

Most Docker installs should use very few environment variables. If you are not sure whether you need one from the tables below, you probably do not.

For HDHomeRun specifically, the normal workflow is:
1. Start M3Undle with the default ports.
2. Open the web UI.
3. Adjust HDHomeRun behavior in **Settings → HDHomeRun**.

Treat the HDHR environment variables below as advanced startup overrides. They are mainly useful for automation, bootstrap defaults, or forcing behavior in special Docker or reverse-proxy setups.

### Required / Recommended

| Variable | Default | Description |
|---|---|---|
| `TZ` | host timezone | Timezone for log timestamps (e.g. `America/New_York`, `Europe/London`, `UTC`) |
| `M3UNDLE_ENCRYPTION_KEY` | *(none)* | **Required for Xtream Codes providers.** Base64-encoded 32-byte AES key used to encrypt passwords at rest. Generate with `openssl rand -base64 32`. Keep this secret — treat it like a master password. |

### Optional — Authentication

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_AUTH_ENABLED` | `false` | Set to `true` to require login for the UI and management APIs. |
| `M3UNDLE_ADMIN_USER` | `admin` | Admin username/email. Used only on first startup when no account exists. |
| `M3UNDLE_ADMIN_PASSWORD` | *(none)* | **Required** when `M3UNDLE_AUTH_ENABLED=true` and no admin account exists yet. Also required when running one-time password recovery. |
| `M3UNDLE_ADMIN_PASSWORD_RESET` | `false` | One-time recovery switch. Set to `true` to force-reset the existing admin password from `M3UNDLE_ADMIN_PASSWORD` on startup, then set back to `false`. |

Password recovery workflow (lost admin password):
1. Set `M3UNDLE_ADMIN_PASSWORD` to the new password.
2. Set `M3UNDLE_ADMIN_PASSWORD_RESET=true`.
3. Restart the container once and log in with the new password.
4. Set `M3UNDLE_ADMIN_PASSWORD_RESET=false` and restart again.

Endpoint security (M3U/XMLTV/stream/HDHR username/password auth) is managed in **Settings → Endpoint Security** and stored in the database.
The same settings page also controls the HDHomeRun `Virtual Tuner ID` used for tuner-slot ownership and retune behaviour.

### Optional — Provider Features

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_M3U_DIR` | `/m3u_data` | Directory the file browser exposes when adding a provider from a local `.m3u` file. The Docker image defaults to `/m3u_data`. Mount a host directory to that path, or set this variable to a different container path and mount there. |

### Optional — Observability

Most users should configure metrics from **Settings → Observability** after first startup. The variables below are useful for managed deployments or bootstrap defaults.

| Variable | Default | Description |
|---|---|---|
| `M3Undle__Observability__Metrics__Enabled` | `true` | Master switch for the Prometheus-compatible scrape endpoint. |
| `M3Undle__Observability__Metrics__Path` | `/metrics` | Scrape endpoint path. |
| `M3Undle__Observability__Metrics__Mode` | `LocalOnly` | Metrics access mode: `Disabled`, `LocalOnly`, `Token`, or `Public`. |
| `M3Undle__Observability__Metrics__EnableChannelLabels` | `false` | Reserved guard for channel-level labels. Leave disabled unless you understand the Prometheus cardinality impact. |
| `M3Undle__Observability__Metrics__LocalAllowedCidrs__0` | *(empty)* | First CIDR allowed in `LocalOnly` mode, for example `192.168.1.0/24`. Add more with `__1`, `__2`, etc. |

Example:

```yaml
environment:
  M3Undle__Observability__Metrics__Mode: "LocalOnly"
  M3Undle__Observability__Metrics__LocalAllowedCidrs__0: "192.168.1.0/24"
```

Metrics tokens are generated in the web UI and shown once. See [OBSERVABILITY.md](OBSERVABILITY.md) for Prometheus and Grafana examples.

### Optional — Stream Relay Tuning

Most stream proxy settings are managed from **Settings → Stream Proxy** and restart-required changes are tracked in the UI. The variables below are advanced startup/config controls for behavior that is not exposed as a normal UI field.

| Variable | Default | Description |
|---|---|---|
| `M3Undle__Streaming__ProviderMaxConcurrentUpstreams` | *(unset)* | Optional global provider-upstream cap used when a provider does not have its own max concurrent stream limit. |
| `M3Undle__Streaming__Reconnect__ContentStallTimeout` | `00:00:08` | MPEG-TS content-stall timeout. Real TS content resets this timer; null-only packets do not, so prolonged CDN gaps can reconnect before the generic read-stall timeout. |
| `M3Undle__Streaming__Reconnect__StrikeCooldown` | `00:05:00` | Cooldown after retry exhaustion for a failing source, used to avoid provider retune storms. |

The UI-managed stream settings, such as max simultaneous streams, idle grace, buffer size, read-stall timeout, reconnect window, and connect timeout, are persisted in the database and can override appsettings/environment values after first configuration.

### App Settings

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `5004;8080` | Ports the app listens on inside the container. `5004` is used for HDHomeRun-compatible tuning and `8080` for the web UI and general endpoints. |
| `M3Undle__Refresh__TimeoutMinutes` | `5` | Provider fetch timeout |
| `M3Undle__Refresh__StartupDelaySeconds` | `30` | Delay before first refresh after startup |
| `M3Undle__Snapshot__RetentionCount` | `3` | Number of snapshots to retain |
| `M3Undle__Cors__ApplicationAllowedOrigins__0` | *(unset)* | First allowed CORS origin for the application surface (`/api`, UI, `/Account/*`). Add more with `__1`, `__2`, etc. |
| `M3UNDLE_DATA_DIR` | `/data` (in image) | Override the data directory (database, logs, snapshots). Rarely needed when using the standard Docker volume layout. |

### Optional — HDHomeRun

Most users can skip this entire section.

Use the web UI at **Settings → HDHomeRun** for normal setup and day-to-day changes. The environment variables below are optional advanced overrides, not required setup.

Most HDHR behavior is configurable in **Settings → HDHomeRun**. The main exceptions are:
- `M3UNDLE_HDHR_ENABLED`, which can force HDHR off at startup
- `M3Undle__HdHomeRun__FriendlyName`, which is currently env/config only

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_HDHR_ENABLED` | `true` | Master switch for all HDHomeRun endpoints. Normally leave this unset. Set it to `false` only if you want to disable HDHR completely. This env var overrides the UI setting at startup. |
| `M3Undle__HdHomeRun__DiscoveryEnabled` | `true` | Enables SSDP and SiliconDust network discovery. Normally change this in **Settings → HDHomeRun**. Only set it here if you want a startup default or managed deployment behavior. Requires UDP ports (see [Docker Networking for HDHomeRun](#docker-networking-for-hdhr)). |
| `M3Undle__HdHomeRun__SsdpEnabled` | `true` | Controls the SSDP/UPnP listener on UDP 1900. Normally change this in **Settings → HDHomeRun**. |
| `M3Undle__HdHomeRun__SiliconDustDiscoveryEnabled` | `true` | Controls the SiliconDust discovery listener on UDP 65001. Normally change this in **Settings → HDHomeRun**. |
| `M3Undle__HdHomeRun__TunerCount` | `6` | Virtual tuner count advertised when no provider limit or UI override is in effect. This is the advertised count only, not a server-side cap; clients allocate against it and often reserve one tuner for EPG, so keep it well above 1. When a limit *is* enforced, the advertised count equals that limit instead. Normally change this in **Settings → HDHomeRun**. |
| `M3Undle__HdHomeRun__AdvertisedBaseUrl` | *(auto-detect)* | Base URL returned in `discover.json` and discovery responses (for example `http://192.168.1.50:5004`). Normally leave this blank and let M3Undle auto-detect it. Set it only for advanced Docker NAT, LAN discovery, or reverse-proxy scenarios. This can also be changed in **Settings → HDHomeRun**. |
| `M3Undle__HdHomeRun__FriendlyName` | `M3Undle HDHomeRun` | Device name shown in client apps. Rarely needed. This is currently an env/config-only setting, not a web UI field. |

Quick rule: if you are running Docker on a normal home server, do not add HDHR env vars just because they exist. Bring the app up first, then use **Settings → HDHomeRun**. The main exception is `M3UNDLE_HDHR_ENABLED=false`, which is the one env var intended to force HDHR off at startup.

### Optional — Reverse Proxy

| Variable | Default | Description |
|---|---|---|
| `M3Undle__ReverseProxy__ForwardLimit` | `1` | Max entries to process from `X-Forwarded-*` headers. |
| `M3Undle__ReverseProxy__TrustedProxies` | *(empty)* | Comma-separated list of trusted proxy IPs (e.g. `192.168.1.1`). Forwarded headers are only honoured from these IPs, `TrustedNetworks`, or loopback. |
| `M3Undle__ReverseProxy__TrustedNetworks` | *(empty)* | Comma-separated CIDR blocks (e.g. `10.0.0.0/8`). |

The image sets this internal path so M3Undle can find `config.yaml` and `/config/.env` automatically:

| Variable | Image Default |
|---|---|
| `M3UNDLE_CONFIG_DIR` | `/config` |

---

## Provider Types

M3Undle supports three provider types, added through the web UI:

### URL / File — No extra setup

Paste any `http://` or `https://` playlist URL. To keep credentials out of the database, put them in `/config/.env` and reference them with `%VAR_NAME%` placeholders in the URL:

```env
# /config/.env
MY_PASSWORD=supersecret
```

```
http://my.server:8080/get.php?username=alice&password=%MY_PASSWORD%
```

For local files, mount the directory and set `M3UNDLE_M3U_DIR` (see above) to use the built-in file browser.

### Xtream Codes — Requires `M3UNDLE_ENCRYPTION_KEY`

Xtream Codes providers store the password encrypted in the database using AES-256-GCM. The encryption key must be available at runtime via `M3UNDLE_ENCRYPTION_KEY`.

**Generate a key:**

```bash
# Linux / macOS
openssl rand -base64 32

# Windows PowerShell (no openssl needed)
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }) -as [byte[]])
```

Set it as a container environment variable — **not** in the `/config/.env` file:

```yaml
# compose.yaml
environment:
  M3UNDLE_ENCRYPTION_KEY: "paste-your-generated-key-here"
```

> [!WARNING]
> If you lose the encryption key, stored Xtream passwords cannot be decrypted. You will need to re-enter passwords for all Xtream providers. Back up your key.

### Import from config.yaml

If you have a `config.yaml` in `/config`, M3Undle can import providers from it directly via the Add Provider dialog. This is useful for migrating from a config-file workflow.

---

## Credential Security Notes

- **Xtream passwords** are encrypted (AES-256-GCM) and stored in the database. The plaintext password is never persisted.
- **URL credentials via `.env`** (`%VAR_NAME%` substitution) are stored in plaintext in `/config/.env`. Restrict file permissions on the host accordingly.
- **The encryption key** (`M3UNDLE_ENCRYPTION_KEY`) should be set as an environment variable, not stored in `/config/.env`. Anyone with read access to the `.env` file would gain access to the key.
- The `/config/.env` file is for provider URL substitution only — things like `%PROVIDER_PASS%` in playlist URLs.

---

## Compatibility Endpoints

Once running, clients consume these endpoints directly:

| Endpoint | Purpose |
|---|---|
| `GET /m3u/m3undle.m3u` | M3U playlist |
| `GET /xmltv/m3undle.xml` | XMLTV guide data |
| `GET /stream/<streamKey>` | Stream relay proxy |
| `GET /player_api.php` | Xtream Codes API (account info, categories, stream lists) |
| `GET /get.php` | Xtream Codes M3U playlist |
| `GET /live/<user>/<pass>/<id>` | Xtream Codes live stream (path-credential) |
| `GET /movie/<user>/<pass>/<id>` | Xtream Codes VOD stream (path-credential) |
| `GET /series/<user>/<pass>/<id>` | Xtream Codes series stream (path-credential) |
| `GET /hdhr/discover.json` | HDHomeRun discovery |
| `GET /hdhr/lineup.json` | HDHomeRun channel lineup |
| `GET /health` | Health check |
| `GET /livez` | Liveness probe |
| `GET /readyz` | Readiness probe |
| `GET /healthz` | JSON health summary |
| `GET /metrics` | Prometheus-compatible metrics scrape endpoint |
| `GET /status` | Machine-readable status JSON |

**M3U/XMLTV clients** — point at `http://<host>:8080/m3u/m3undle.m3u`.

**Xtream clients** (TiviMate, GSE Player, IPTV Smarters) — add M3Undle as an Xtream source with your server URL and the endpoint-security username/password from **Settings → Endpoint Security**.

**HDHomeRun clients** — see [HDHomeRun Setup](#hdhr-setup) below.

Stream URLs in the playlist point to the relay proxy — provider credentials are never exposed to clients.

Metrics access is controlled separately from UI login and endpoint security. The default is local-only access. See [OBSERVABILITY.md](OBSERVABILITY.md).

---

## HDHomeRun Setup {#hdhr-setup}

M3Undle emulates an HDHomeRun network tuner so DVR applications (NextPVR, Jellyfin Live TV, Emby Live TV, Plex, Channels DVR) can consume your lineup as if it were a hardware tuner.

### How it works

Port **5004** on the container serves the HDHomeRun HTTP API. Client apps connect to this port to discover the device, pull the channel lineup, and tune streams.

Port **8080** serves the web UI, M3U/XMLTV, Xtream Codes, and general compatibility endpoints.

Both ports are set in the Dockerfile — you do not need to add them manually.

### Option A — Manual add (recommended)

This is the most reliable setup across all Docker networking modes and client applications. No discovery ports, no special networking.

1. Keep `5004:5004` and `8080:8080` published in your compose file.
2. In your DVR application, add a network tuner manually:
   - **Jellyfin**: Dashboard → Live TV → Add Tuner Device → HD Homerun → enter `http://<host-ip>:5004` *(recommended — see note below)*
   - **NextPVR**: Settings → Tuners → Add → enter `http://<host-ip>:5004`
   - **Emby**: Live TV → Add Tuner → HDHomeRun → enter `http://<host-ip>:5004`
   - **Plex**: Settings → Live TV & DVR → Set Up → enter `http://<host-ip>:5004`
3. The client will connect, fetch `discover.json` and `lineup.json`, and show your channels.

No extra environment variables are needed. HDHR is enabled by default, and the rest of the HDHR behavior can be adjusted later in **Settings → HDHomeRun**.

> [!NOTE]
> **Jellyfin users**: Manual add is the supported path for Jellyfin. Jellyfin's "Detect My Devices" auto-discovery may not find M3Undle in Docker bridge or NAT-like setups because some client autodetect flows connect to the responder IP on port 80 instead of using the advertised base URL on port 5004. Manual entry using `http://<host-ip>:5004` works reliably regardless of networking mode.

### Option B — Auto-discovery (best effort)

Auto-discovery lets some client apps find M3Undle automatically, similar to a real HDHomeRun. This works best on flat LAN or host-network deployments. In Docker bridge or NAT-like setups, discovery may not reach all clients — use [Option A](#option-a--manual-add-recommended) as the fallback.

Not all clients handle discovery identically. Some (such as NextPVR) parse the advertised base URL from the discovery response and connect on the correct port. Others (such as Jellyfin) may ignore the advertised URL and attempt to connect to the responder IP on port 80, which fails when M3Undle serves HDHR on port 5004.

In most cases, change discovery in **Settings → HDHomeRun** first and only use env vars here if you need a forced startup default.

1. Discovery is enabled by default on a fresh install. If you have disabled it in **Settings → HDHomeRun** or want to force the startup default from Docker, add this to your `environment:` section:
   ```yaml
   M3Undle__HdHomeRun__DiscoveryEnabled: "true"
   ```
2. Publish the discovery ports:
   ```yaml
   ports:
     - "5004:5004"
     - "8080:8080"
     - "1900:1900/udp"    # SSDP / UPnP
     - "65001:65001/udp"  # SiliconDust discovery
   ```
3. Set the advertised base URL so discovery responses point to your host, not the container's internal IP:
   ```yaml
   M3Undle__HdHomeRun__AdvertisedBaseUrl: "http://192.168.1.50:5004"
   ```
   Replace `192.168.1.50` with the LAN IP of the Docker host.

> [!IMPORTANT]
> **Docker bridge networking and multicast**: SSDP relies on UDP multicast, which does not pass through Docker's default bridge network. Discovery may work for clients on the Docker host itself, but **clients on other machines will not see M3Undle via auto-discovery** unless you use `network_mode: host` (see below) or a macvlan network. For most users, **Option A (manual add) is more reliable in Docker**.

### Option C — Host networking (full discovery compatibility)

For auto-discovery to work identically to a real HDHomeRun (including from other machines on the LAN):

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:beta
    container_name: m3undle
    network_mode: host
    environment:
      TZ: America/New_York
      M3Undle__HdHomeRun__DiscoveryEnabled: "true"
    volumes:
      - ./config:/config
      - ./data:/data
    restart: unless-stopped
```

With `network_mode: host`, the container shares the host's network stack directly. No port mapping is needed (or allowed) — the app listens on the host's ports `5004`, `8080`, `1900/udp`, and `65001/udp` directly. SSDP multicast works because the container is on the real LAN interface.

> [!WARNING]
> `network_mode: host` bypasses Docker network isolation. All container ports are exposed directly on the host. Only use this on a trusted LAN.

### Tuner count

The `TunerCount` setting (default: `6`) controls how many virtual tuners the emulated device advertises when no provider limit or UI override is in effect. It is the advertised count only — not a server-side stream cap. Clients allocate against it and commonly reserve one tuner for EPG/PSIP scanning, so a value of 1 can leave no tuner free for live TV; keep it comfortably above 1. When a provider limit or UI override *is* set, the advertised count matches that limit (and is enforced server-side). Most users should change this in **Settings → HDHomeRun** if needed. If your deployment needs a startup default from Docker, set:

```yaml
M3Undle__HdHomeRun__TunerCount: "6"
```

The tuner count is editable in the web UI under **Settings → HDHomeRun**.

### Disabling HDHR

If you do not need HDHomeRun emulation at all:

```yaml
M3UNDLE_HDHR_ENABLED: "false"
```

This disables all HDHR endpoints and discovery. Port 5004 will still listen (it is set at the ASP.NET level) but will return 404 for HDHR routes.

Because `M3UNDLE_HDHR_ENABLED` is a startup override, setting it to `false` also prevents normal HDHR management from the UI until you remove the env var and restart.

---

## Docker Networking for HDHomeRun {#docker-networking-for-hdhr}

| Setup | Discovery works from host? | Discovery works from LAN? | Manual add works? |
|---|---|---|---|
| Bridge (default) | Sometimes | No | **Yes** |
| Bridge + UDP ports published | Yes | Unreliable | **Yes** |
| `network_mode: host` | Yes | **Yes** | **Yes** |
| macvlan | Yes | **Yes** | **Yes** |

**Why multicast is tricky in Docker**: SSDP discovery uses UDP multicast on `239.255.255.250:1900`. Docker's bridge network creates a virtual network segment. Multicast packets from the container don't reach the physical LAN, and multicast queries from LAN clients don't reach the container. Publishing the port (`-p 1900:1900/udp`) only helps for unicast traffic — it does not bridge multicast.

**Recommendation**: Use **manual add** (Option A) unless you have a specific reason to need auto-discovery. It works with any Docker networking mode, any client application, and is the most reliable approach.

**If you need auto-discovery**, use `network_mode: host` (Option C). It is the only straightforward option that reliably supports SSDP multicast across the LAN. Even with host networking, some clients may not follow the advertised base URL from discovery responses — manual add remains the most portable option.

**`AdvertisedBaseUrl` explained**: When a client discovers M3Undle, the response includes a URL where the client should connect for tuning. If M3Undle is behind Docker NAT, it may auto-detect `172.17.0.x` as its address — which is unreachable from the LAN. Setting `AdvertisedBaseUrl` to `http://<your-host-ip>:5004` ensures clients get a reachable address. This is required for bridge networking with discovery; not needed with `network_mode: host`.

---

## Updating

```bash
docker compose pull
docker compose up -d
```

Database migrations run automatically on startup.

---

## Tags

| Tag | Tracks | Notes |
|---|---|---|
| `v1.0.0-beta.1` | Exact version — immutable | Pin to this if you want full control over updates |
| `beta` | Latest beta release | Moves forward as new beta builds are published |
| `latest` | Latest **stable** release | Not published until v1.0.0 — do not use during pre-release |

**Current phase:** beta — use the `beta` tag or pin to a specific version like `v1.0.0-beta.1`.

`latest` does not exist yet. Pulling it will return "image not found".

---

## Ports

Change the host-side port without touching the container:

```yaml
ports:
  - "9090:8080"  # host:container
```

To change the port the container listens on internally:

```yaml
environment:
  ASPNETCORE_HTTP_PORTS: "9090"
ports:
  - "9090:9090"
```
