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
    user: "${PUID}:${PGID}"
    ports:
      - "8080:8080"
    environment:
      TZ: America/New_York
      # Required if you use Xtream Codes providers (encrypted password storage).
      # Generate with: openssl rand -base64 32
      M3UNDLE_ENCRYPTION_KEY: "your-base64-32-byte-key"
      # Optional — enables the file browser when adding providers from local .m3u files.
      M3UNDLE_M3U_DIR: /m3u
    volumes:
      - ./config:/config
      - ./data:/data
      - ./m3u:/m3u        # optional — only needed if using M3UNDLE_M3U_DIR
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

### docker run

```bash
mkdir -p m3undle/config m3undle/data m3undle/m3u && cd m3undle

docker run -d \
  --name m3undle \
  --user "$(id -u):$(id -g)" \
  -p 8080:8080 \
  -e TZ=America/New_York \
  -e M3UNDLE_ENCRYPTION_KEY="your-base64-32-byte-key" \
  -v ./config:/config \
  -v ./data:/data \
  -v ./m3u:/m3u \
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

### Optional — Provider Features

| Variable | Default | Description |
|---|---|---|
| `M3UNDLE_M3U_DIR` | *(none)* | Directory the file browser exposes when adding a provider from a local `.m3u` file. Mount a host directory here (e.g. `/m3u`) and set this variable to enable it. |

### App Settings

| Variable | Default | Description |
|---|---|---|
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
openssl rand -base64 32
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
