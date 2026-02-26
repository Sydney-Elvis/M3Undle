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
    image: ghcr.io/sydney-elvis/m3undle:alpha
    container_name: m3undle
    ports:
      - "8080:8080"
    environment:
      TZ: America/New_York
    volumes:
      - ./config:/config
      - ./data:/data
    restart: unless-stopped
```

Then open `http://<host>:8080`.

### docker run

```bash
mkdir -p m3undle/config m3undle/data && cd m3undle

docker run -d \
  --name m3undle \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -v ./config:/config \
  -v ./data:/data \
  --restart unless-stopped \
  ghcr.io/sydney-elvis/m3undle:alpha
```

---

## Volumes

| Mount | Purpose |
|---|---|
| `/config` | `config.yaml` and `.env` credential file — files you edit |
| `/data` | SQLite database, snapshots, log files — runtime state |

Both are required for data to persist across container restarts.

**Why bind mounts?** Bind mounts put files in a known place on the host. You can edit `config.yaml` with any editor, inspect logs, or wipe the data directory without going through Docker commands.

### Linux: ownership

The container runs as a non-root user (UID `64198`). On Linux with bind mounts, pre-create the directories with the correct owner before starting the container:

```bash
mkdir -p config data
chown -R 64198:64198 config data
```

Docker Desktop on Mac and Windows handles this automatically.

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

| Variable | Default | Description |
|---|---|---|
| `TZ` | host timezone | Timezone for log timestamps (e.g. `America/New_York`, `Europe/London`, `UTC`) |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Port the app listens on inside the container |
| `M3Undle__Refresh__IntervalHours` | `4` | How often the background refresh runs |
| `M3Undle__Refresh__TimeoutMinutes` | `5` | Provider fetch timeout |
| `M3Undle__Refresh__StartupDelaySeconds` | `30` | Delay before first refresh after startup |
| `M3Undle__Snapshot__RetentionCount` | `3` | Number of snapshots to retain |

The following are set by the image and do not need to be overridden:

| Variable | Image Default |
|---|---|
| `ConnectionStrings__DefaultConnection` | `DataSource=/data/m3undle.db;Cache=Shared` |
| `M3Undle__Logging__LogDirectory` | `/data/logs` |
| `M3Undle__Snapshot__Directory` | `/data/snapshots` |
| `M3UNDLE_CONFIG_DIR` | `/config` |

---

## Compatibility Endpoints

Once running, clients consume these endpoints directly:

| Endpoint | Purpose |
|---|---|
| `GET /m3u/m3undle.m3u` | M3U playlist |
| `GET /xmltv/m3undle.xml` | XMLTV guide data |
| `GET /stream/<streamKey>` | Stream relay proxy |
| `GET /health` | Health check |
| `GET /status` | Machine-readable status JSON |

Point your DVR or media server at `http://<host>:8080/m3u/m3undle.m3u`.

Stream URLs in the playlist point to the relay proxy — provider credentials are never exposed to clients.

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
