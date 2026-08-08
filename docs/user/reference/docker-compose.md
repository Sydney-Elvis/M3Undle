# Docker Compose

Reference for compose-level options beyond the [Install with Docker](../getting-started/install-with-docker.md) quick start.

## Named volumes vs. bind mounts

The quick start bind-mounts `./config` so configuration files are easy to inspect, edit, and back up directly on the host. If you'd rather use Docker-managed storage for both, named volumes work too:

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:beta
    volumes:
      - m3undle_config:/config
      - m3undle_data:/data
    # ...rest of the service definition

volumes:
  m3undle_config:
  m3undle_data:
```

Named volumes avoid the ownership requirement on Linux and can give better I/O performance, but you can't browse the files directly from the host.

The bind-mount host directory name is arbitrary — `./config`, `./.config` (hidden), or anything else works, as long as the `volumes:` entry's host-side path matches wherever `config.yaml` actually lives. Renaming or moving that directory without updating the corresponding `volumes:` line is a common cause of `config.yaml not found` errors after otherwise-successful startups.

If you bind-mount instead, create the host directory yourself (`mkdir -p ./data`) before the first `docker compose up`. Docker auto-creates a missing bind-mount source as `root`, and M3Undle needs write access to `/data` from the moment it starts — a root-owned directory it can't write to causes an immediate crash. See [Container Won't Start](../troubleshooting/container-wont-start.md).

## Generated HLS storage sizing

See [Browser Playback](../guides/browser-playback.md) for how to size the `/data/hls-work` scratch space used by generated HLS sessions.

## Tags

| Tag | Tracks | Notes |
|---|---|---|
| `v1.0.0-beta.1` (or any specific version) | Exact, immutable version | Pin to this for full control over updates |
| `beta` | Latest beta build | Moves forward as new beta builds are published |
| `latest` | Latest **stable** release | Not published until v1.0.0 — do not use during pre-release; pulling it returns "image not found" |

**Current phase:** beta — use the `beta` tag or pin to a specific version like `v1.0.0-beta.1`.

## Updating

```bash
docker compose pull
docker compose up -d
```

Database migrations run automatically on startup.

## Changing ports

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

See [Environment Variables](environment-variables.md) for the full `ASPNETCORE_HTTP_PORTS` behavior and [HDHomeRun-Compatible Clients](../clients/hdhomerun-compatible-clients.md) if you're changing the HDHomeRun port specifically.
