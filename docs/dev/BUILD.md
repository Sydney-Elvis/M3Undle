# Build and Run From Source

This covers building and running M3Undle locally from source, using the root `compose.yaml` — distinct from [Install with Docker](https://sydney-elvis.github.io/M3Undle/getting-started/install-with-docker/), which covers running the *published* image.

## Prerequisites

- Docker Desktop (or Docker Engine + Compose plugin)
- Repo root as current directory

## Setup

The compose file runs the container as your own user account via `user: "${PUID}:${PGID}"`. Create a `.env` file in the repo root before the first run (already covered by `.gitignore` — do not commit it):

```bash
echo "PUID=$(id -u)" >> .env
echo "PGID=$(id -g)" >> .env
```

Then create the bind-mounted data directories yourself, before the first run:

```bash
mkdir -p ./data ./m3u_data
```

Docker auto-creates a missing bind-mount source as `root` on first use, which the container (running as `PUID:PGID`) then can't write to — `mkdir`-ing it first as your own user avoids that.

## Build and run

```bash
docker compose up --build -d
```

Stop it:

```bash
docker compose down
```

## Ports and volumes

Compose publishes both ports the Dockerfile exposes:

- `5004` — HDHomeRun-compatible tuning
- `8080` — web UI, M3U/XMLTV, Xtream, and general compatibility endpoints

Bind-mounted host directories (create these yourself before the first run — see Setup above):

- `./data` → `/data` — SQLite database, snapshots, logs
- `./m3u_data` → `/m3u_data` — local `.m3u` files, browsable via the in-app file browser (`M3UNDLE_M3U_DIR`)

Note: this compose file does **not** mount `/config`, so `config.yaml` import and the `%VAR_NAME%` URL-credential-substitution feature (which need `/config` and `/config/.env` — see [Install with Docker](https://sydney-elvis.github.io/M3Undle/getting-started/install-with-docker/)) aren't available in this local-build setup out of the box. Add a `./config:/config` volume yourself if you need to test that path.

To wipe all persisted data:

```bash
rm -rf ./data ./m3u_data
```

## Smoke tests

Health:

```bash
curl -f http://localhost:8080/health
```

M3U endpoint:

```bash
curl -f http://localhost:8080/m3u/m3undle.m3u
```

Stream relay (extract the first `/stream/<streamKey>` from the playlist):

```bash
key_path=$(curl -fsS http://localhost:8080/m3u/m3undle.m3u | grep -o '/stream/[^[:space:]]*' | head -1)
curl -f "http://localhost:8080${key_path}" -o /dev/null
```

If no stream key is found, the playlist is likely empty because no active snapshot has been generated yet — add a provider first.

## Useful commands

```bash
docker compose ps
docker compose logs -f m3undle
docker inspect --format='{{json .State.Health}}' m3undle
```

## Notes

- DB schema migrations run automatically on app startup.
- The container runs as whatever user `PUID`/`PGID` in `.env` resolve to.
- `SOURCE_REVISION` is an optional build arg that feeds the informational build version (see `docs/spec/version_management.md`); leave it unset for local builds.
- If Docker in WSL is unavailable, enable WSL integration in Docker Desktop.
- `compose.yaml` hardcodes `ASPNETCORE_ENVIRONMENT: Production` (line 17). For manual API testing, change that to `Development` locally (don't commit it) to enable the interactive Scalar reference at `/scalar` and the raw OpenAPI document at `/openapi/management.json` — both require logging in via `/Account/Login` first. Neither is available in a standard Production run.

