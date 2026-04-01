# Docker

M3Undle is published to GitHub Container Registry (GHCR).

Image: `ghcr.io/sydney-elvis/m3undle`

---

## Quick Start

Create a directory for M3Undle, place a `compose.yaml` inside it, then run:

```bash
mkdir m3undle && cd m3undle
# create compose.yaml (see below)
mkdir config data hls-work
docker compose up -d
```

### compose.yaml

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:alpha
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
      # Optional — enables the file browser when adding providers from local .m3u files.
      M3UNDLE_M3U_DIR: /m3u
      # Optional but recommended — isolate generated HLS working files.
      # Keep this on fast storage if possible.
      M3Undle__Streaming__GeneratedHls__Directory: /hls-work
      # Uncomment to require login for the web UI:
      # M3UNDLE_AUTH_ENABLED: "true"
      # M3UNDLE_ADMIN_USER: admin
      # M3UNDLE_ADMIN_PASSWORD: "choose-a-strong-password"
    volumes:
      - ./config:/config
      - ./data:/data
      - ./m3u:/m3u        # optional — only needed if using M3UNDLE_M3U_DIR
      - ./hls-work:/hls-work # optional but recommended for generated browser HLS
    restart: unless-stopped
```

Create a `.env` file next to `compose.yaml` with your user and group IDs:

```env
PUID=1000
PGID=1000
```

Find your IDs on Linux with `id`:

```bash
$ id
uid=1000(jake) gid=1000(jake) groups=1000(jake),998(docker)
# PUID=1000  PGID=1000
```

Then open `http://<host>:8080`.

If you use HDHomeRun-compatible clients such as NextPVR, keep `5004:5004` published. `5004` is the HDHomeRun HTTP tuning port, while `8080` serves the web UI and general compatibility endpoints.

### docker run

```bash
mkdir -p m3undle/config m3undle/data m3undle/m3u m3undle/hls-work && cd m3undle

docker run -d \
  --name m3undle \
  --user "$(id -u):$(id -g)" \
  -p 5004:5004 \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -e M3UNDLE_ENCRYPTION_KEY="your-base64-32-byte-key" \
  -e M3Undle__Streaming__GeneratedHls__Directory=/hls-work \
  -v ./config:/config \
  -v ./data:/data \
  -v ./m3u:/m3u \
  -v ./hls-work:/hls-work \
  --restart unless-stopped \
  ghcr.io/sydney-elvis/m3undle:alpha
```

---

## Volumes

| Mount | Required | Purpose |
|---|---|---|
| `/config` | Yes | `config.yaml` and `.env` credential file — files you edit |
| `/data` | Yes | SQLite database, snapshots, log files — runtime state |
| `/m3u` (or any path) | No | Local `.m3u` files browsable via the file browser. Set `M3UNDLE_M3U_DIR` to the container path. |
| `/hls-work` (or any path) | Recommended | Generated rolling HLS playlists/segments for browser playback fallback. Set `M3Undle__Streaming__GeneratedHls__Directory` to match. |

Both `/config` and `/data` are required for data to persist across container restarts.

**Why bind mounts?** Bind mounts put files in a known place on the host. You can edit `config.yaml` with any editor, inspect logs, or wipe the data directory without going through Docker commands.

### Named volumes (alternative)

If you prefer Docker-managed storage, named volumes work too — replace the bind mounts:

```yaml
volumes:
  - m3undle_config:/config
  - m3undle_data:/data
  - m3undle_hlswork:/hls-work

# add at the bottom of compose.yaml:
volumes:
  m3undle_config:
  m3undle_data:
  m3undle_hlswork:
```

Named volumes avoid the ownership requirement on Linux and can give better I/O performance, but you can't browse the files directly from the host.

### Generated HLS Storage Sizing

Browser playback fallback (`?format=hls` or browser UA fallback) writes rolling HLS playlists/segments to the generated-HLS directory.

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
| `M3UNDLE_ADMIN_PASSWORD` | *(none)* | **Required** when `M3UNDLE_AUTH_ENABLED=true` and no admin account exists yet. Used only for the initial seed — changing this later has no effect (use Settings → Change Password instead). |

Endpoint security (M3U/XMLTV/stream/HDHR username/password auth) is managed in **Settings → Endpoint Security** and stored in the database.
The same settings page also controls the HDHomeRun `Virtual Tuner ID` used for tuner-slot ownership and retune behaviour.

### Optional — Provider Features

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_M3U_DIR` | `/m3u_data` | Directory the file browser exposes when adding a provider from a local `.m3u` file. The Docker image defaults to `/m3u_data`. Mount a host directory to that path, or set this variable to a different container path and mount there. |

### App Settings

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `5004;8080` | Ports the app listens on inside the container. `5004` is used for HDHomeRun-compatible tuning and `8080` for the web UI and general endpoints. |
| `M3Undle__Refresh__IntervalHours` | `4` | How often the background refresh runs |
| `M3Undle__Refresh__TimeoutMinutes` | `5` | Provider fetch timeout |
| `M3Undle__Refresh__StartupDelaySeconds` | `30` | Delay before first refresh after startup |
| `M3Undle__Snapshot__RetentionCount` | `3` | Number of snapshots to retain |
| `M3Undle__Cors__ApplicationAllowedOrigins__0` | *(unset)* | First allowed CORS origin for the application surface (`/api`, UI, `/Account/*`). Add more with `__1`, `__2`, etc. |
| `M3Undle__Streaming__GeneratedHls__Directory` | `/data/hls-work` (image default) | Directory used for generated rolling HLS session files (`index.m3u8` + segments). Set this to a dedicated mount (for example `/hls-work`) to isolate browser playback scratch storage. |
| `M3UNDLE_DATA_DIR` | `/data` (in image) | Override the data directory (database, logs, snapshots). Rarely needed when using the standard Docker volume layout. |

### Optional — HDHomeRun

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_HDHR_ENABLED` | `true` | Master switch for all HDHomeRun endpoints. Set to `false` to disable HDHR completely. Can also be toggled in **Settings** in the web UI. |
| `M3Undle__HdHomeRun__DiscoveryEnabled` | `false` | Enable SSDP and SiliconDust network discovery so clients like NextPVR, Jellyfin, and Emby find M3Undle automatically. Requires UDP ports (see [Docker Networking for HDHomeRun](#docker-networking-for-hdhr)). |
| `M3Undle__HdHomeRun__SsdpEnabled` | `true` | Enable SSDP/UPnP listener (UDP 1900). Only active when `DiscoveryEnabled` is also `true`. |
| `M3Undle__HdHomeRun__SiliconDustDiscoveryEnabled` | `true` | Enable SiliconDust proprietary discovery (UDP 65001). Only active when `DiscoveryEnabled` is also `true`. |
| `M3Undle__HdHomeRun__TunerCount` | `1` | Number of virtual tuner slots to advertise. Increase if your DVR app needs parallel recordings. |
| `M3Undle__HdHomeRun__AdvertisedBaseUrl` | *(auto-detect)* | Base URL returned in `discover.json` and discovery responses (e.g. `http://192.168.1.50:5004`). **Set this when running behind Docker NAT or a reverse proxy** — the container's auto-detected address often isn't reachable from the LAN. |
| `M3Undle__HdHomeRun__FriendlyName` | `M3Undle HDHomeRun` | Device name shown in client apps. |

### Optional — Reverse Proxy

| Variable | Default | Description |
|---|---|---|
| `M3Undle__ReverseProxy__ForwardLimit` | `1` | Max entries to process from `X-Forwarded-*` headers. |
| `M3Undle__ReverseProxy__TrustedProxies` | *(empty)* | Comma-separated list of trusted proxy IPs (e.g. `192.168.1.1`). Forwarded headers are only honoured from these IPs, `TrustedNetworks`, or loopback. |
| `M3Undle__ReverseProxy__TrustedNetworks` | *(empty)* | Comma-separated CIDR blocks (e.g. `10.0.0.0/8`). |

The following are set by the image and do not need to be overridden:

| Variable | Image Default |
|---|---|
| `ConnectionStrings__DefaultConnection` | `DataSource=/data/m3undle.db;Cache=Shared` |
| `M3Undle__Logging__LogDirectory` | `/data/logs` |
| `M3Undle__Snapshot__Directory` | `/data/snapshots` |
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
| `GET /status` | Machine-readable status JSON |

**M3U/XMLTV clients** — point at `http://<host>:8080/m3u/m3undle.m3u`.

**Xtream clients** (TiviMate, GSE Player, IPTV Smarters) — add M3Undle as an Xtream source with your server URL and the endpoint-security username/password from **Settings → Endpoint Security**.

**HDHomeRun clients** — see [HDHomeRun Setup](#hdhr-setup) below.

Stream URLs in the playlist point to the relay proxy — provider credentials are never exposed to clients.

---

## HDHomeRun Setup {#hdhr-setup}

M3Undle emulates an HDHomeRun network tuner so DVR applications (NextPVR, Jellyfin Live TV, Emby Live TV, Plex, Channels DVR) can consume your lineup as if it were a hardware tuner.

### How it works

Port **5004** on the container serves the HDHomeRun HTTP API. Client apps connect to this port to discover the device, pull the channel lineup, and tune streams.

Port **8080** serves the web UI, M3U/XMLTV, Xtream Codes, and general compatibility endpoints.

Both ports are set in the Dockerfile — you do not need to add them manually.

### Option A — Manual add (recommended for Docker)

This is the simplest setup. No discovery ports, no special networking.

1. Keep `5004:5004` and `8080:8080` published in your compose file.
2. In your DVR application, add a network tuner manually:
   - **NextPVR**: Settings → Tuners → Add → enter `http://<host-ip>:5004`
   - **Jellyfin**: Dashboard → Live TV → Add Tuner Device → HD Homerun → enter `http://<host-ip>:5004`
   - **Emby**: Live TV → Add Tuner → HDHomeRun → enter `http://<host-ip>:5004`
   - **Plex**: Settings → Live TV & DVR → Set Up → enter `http://<host-ip>:5004`
3. The client will connect, fetch `discover.json` and `lineup.json`, and show your channels.

No extra environment variables are needed — HDHR is enabled by default.

### Option B — Auto-discovery

If you want client apps to find M3Undle automatically (like a real HDHomeRun), you need to enable network discovery and publish the UDP ports.

1. Add to your `environment:` section:
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
    image: ghcr.io/sydney-elvis/m3undle:alpha
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

The `TunerCount` setting (default: `1`) controls how many simultaneous streams the emulated tuner advertises. If your DVR records multiple channels at once, increase this:

```yaml
M3Undle__HdHomeRun__TunerCount: "4"
```

The tuner count is also editable in the web UI under **Settings → HDHomeRun**.

### Disabling HDHR

If you do not need HDHomeRun emulation at all:

```yaml
M3UNDLE_HDHR_ENABLED: "false"
```

This disables all HDHR endpoints and discovery. Port 5004 will still listen (it is set at the ASP.NET level) but will return 404 for HDHR routes.

---

## Docker Networking for HDHomeRun {#docker-networking-for-hdhr}

| Setup | Discovery works from host? | Discovery works from LAN? | Manual add works? |
|---|---|---|---|
| Bridge (default) | Sometimes | No | **Yes** |
| Bridge + UDP ports published | Yes | Unreliable | **Yes** |
| `network_mode: host` | Yes | **Yes** | **Yes** |
| macvlan | Yes | **Yes** | **Yes** |

**Why multicast is tricky in Docker**: SSDP discovery uses UDP multicast on `239.255.255.250:1900`. Docker's bridge network creates a virtual network segment. Multicast packets from the container don't reach the physical LAN, and multicast queries from LAN clients don't reach the container. Publishing the port (`-p 1900:1900/udp`) only helps for unicast traffic — it does not bridge multicast.

**Recommendation**: Use **manual add** (Option A) unless you have a specific reason to need auto-discovery. It works with any Docker networking mode and is the most reliable approach.

**If you need auto-discovery**, use `network_mode: host` (Option C). It is the only straightforward option that reliably supports SSDP multicast across the LAN.

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
| `v1.0.0-alpha.1` | Exact version — immutable | Pin to this if you want full control over updates |
| `alpha` | Latest alpha release | Moves forward as new alpha builds are published |
| `beta` | Latest beta release | Available from v1.0.0-beta.1 |
| `latest` | Latest **stable** release | Not published until v1.0.0 — do not use during pre-release |

**Current phase:** alpha — use the `alpha` tag or pin to a specific version like `v1.0.0-alpha.1`.

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
