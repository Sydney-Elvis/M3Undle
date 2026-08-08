# Install with Docker

M3Undle is published to GitHub Container Registry as `ghcr.io/sydney-elvis/m3undle`.

## Quick start

Create a directory for M3Undle:

```bash
mkdir m3undle && cd m3undle
mkdir config
```

!!! tip "Prefer a hidden config directory?"
    Use `.config` instead of `config` if you'd rather it not show up in a plain `ls`. Just make sure the `volumes:` line in `compose.yaml` matches whichever name you pick — e.g. `./.config:/config`. The directory name is otherwise arbitrary; only the `:/config` container-side path matters.

Create `compose.yaml`:

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:beta
    container_name: m3undle
    ports:
      - "5004:5004"
      - "8080:8080"
    environment:
      TZ: America/New_York
      M3UNDLE_ENCRYPTION_KEY: "replace-with-a-base64-32-byte-key"
    volumes:
      - ./config:/config
      - m3undle_data:/data
    restart: unless-stopped

volumes:
  m3undle_data:
```

Generate an encryption key (needed only if you plan to use Xtream Codes providers — see [Manage Providers](../guides/manage-providers.md)):

```bash
openssl rand -base64 32
```

Paste the generated value into `M3UNDLE_ENCRYPTION_KEY`, then start M3Undle:

```bash
docker compose up -d
```

## Finish setup in the web UI

!!! important "A running container is not a configured M3Undle installation"
    M3Undle does not include any channels or provider data. After the container starts, open the web UI at `http://<host>:8080` and sign in if you enabled UI authentication.

Before M3Undle can publish a working lineup, you must:

1. **[Add a provider](add-first-provider.md)** — use your own playlist or Xtream account, or use the documented IPTV.org example for a credential-free test.
2. **[Map the channels you want](create-first-lineup.md)** — new provider groups begin as unmapped.
3. **Build Output** to publish the lineup.

Container health only confirms that the application started. Seeing channels and playing a stream confirms that you completed the initial configuration.

## Ports

| Port | Purpose |
|---|---|
| `8080` | Web UI, M3U, XMLTV, Xtream, and general compatibility endpoints |
| `5004` | HDHomeRun-compatible tuning |

Keep `5004:5004` published if you plan to use any HDHomeRun-compatible client (NextPVR, Jellyfin Live TV, Emby Live TV, Plex).

## Volumes

| Mount | Required | Purpose |
|---|---|---|
| `/config` | Yes | `config.yaml` and `.env` credential file — files you edit directly |
| `/data` | Yes | SQLite database, snapshots, log files, runtime state, generated browser-playback files |
| `/m3u_data` (or any path) | No | Local `.m3u` files browsable via the in-app file browser — set `M3UNDLE_M3U_DIR` if you use a different container path |

Bind-mounting `./config` keeps configuration files easy to inspect, edit, and back up outside of Docker. The example above uses a Docker-managed volume for `/data`; a bind mount (`./data:/data`) works too if you'd rather have that on the host as well.

`/data` must be writable by whatever user the container runs as — M3Undle creates its database, logs, and snapshots there on startup and crashes immediately if it can't. If you bind-mount `/data` to a host path instead of using a named volume, **create that directory yourself before the first `docker compose up`**: Docker auto-creates a missing bind-mount source as `root`, which the container often can't write to. See [Container Won't Start](../troubleshooting/container-wont-start.md) if you hit this.

### Config file integration

Place a `config.yaml` (and optionally a `.env` credential file) directly in the `config/` directory mapped to `/config` — M3Undle finds them automatically, no extra environment variables required:

```
m3undle/
  compose.yaml
  config/            ← or `.config/` if you followed the hidden-directory tip above
    config.yaml    ← provider definitions
    .env           ← credentials (never commit this)
  data/            ← managed by the container
```

If you have a `config.yaml` already, you can import providers from it directly via the Add Provider dialog — see [Add the First Provider](add-first-provider.md).

If M3Undle logs `config.yaml not found` after startup, double-check that the host directory in your `volumes:` line actually matches the one containing `config.yaml` — a common mistake is renaming/moving the config folder without updating the mount, which leaves Docker bind-mounting an empty directory. Run `docker inspect <container> --format '{{json .Mounts}}'` to see exactly what's mounted.

## Tags

| Tag | Tracks |
|---|---|
| `beta` | Latest beta build — moves forward as new builds are published |
| `v1.0.0-beta.1` (or any specific version) | Exact, immutable version — pin to this for full control over updates |
| `latest` | Not published yet. Introduced no earlier than the release-candidate track. Pulling it during alpha/beta returns "image not found." |

## Updating

```bash
docker compose pull
docker compose up -d
```

Database migrations run automatically on startup.

## Continue setup

**[Add the First Provider →](add-first-provider.md)**

For advanced Docker options — authentication, HDHomeRun auto-discovery, reverse proxy networking, encryption key rotation, and the full environment variable reference — see [Reference > Environment Variables](../reference/environment-variables.md) and [Reference > Docker Compose](../reference/docker-compose.md).
