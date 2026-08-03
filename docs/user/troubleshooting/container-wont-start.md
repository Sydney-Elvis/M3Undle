# Container Won't Start or Keeps Restarting

If `docker compose ps` shows the container cycling through `Restarting`, or logs show an unhandled exception right after startup, this is almost always a volume or permission problem rather than an M3Undle bug — the checks below isolate it in a couple of minutes.

## 1. Read the actual crash

```bash
docker compose logs --tail 200 m3undle
```

A stack trace ending in something like `System.UnauthorizedAccessException: Access to the path '/data/...' is denied` (often paired with the container exiting with code 139) means the process itself is crashing on startup — before the web server or health checks are even relevant. Don't chase client/network symptoms until this is clean.

## 2. Confirm what's actually mounted and configured

Docker Compose files drift from the documented examples over time — volumes get added for a specific reason and never written down, or an environment variable gets copied from an old example and stops matching what's current. Before debugging further, see what's actually in effect:

```bash
docker compose config
docker inspect m3undle --format '{{json .Mounts}}' | python3 -m json.tool
```

Compare the result against the [documented volumes](../getting-started/install-with-docker.md#volumes) and [environment variables](../reference/environment-variables.md). A few things worth knowing while you do this:

- Extra volumes beyond `/config` and `/data` are usually intentional — most commonly a separate mount for `/data/hls-work` (see [Sizing storage for generated HLS](../guides/browser-playback.md#sizing-storage-for-generated-hls)) to put scratch I/O on faster or larger storage.
- An `M3Undle__...` environment variable that doesn't match anything in the reference table isn't necessarily broken — config binding ignores keys it doesn't recognize rather than erroring, so a typo or an option from a previous version silently does nothing. If a variable you're relying on isn't in the reference doc, don't assume it's taking effect; open an issue so we can confirm and document it.

## 3. Permission denied on `/data` (or a subdirectory of it)

| Symptom | Likely cause |
|---|---|
| `UnauthorizedAccessException` creating `/data`, `/data/logs`, `/data/snapshots`, or `/data/hls-work` | The container's runtime user doesn't own (or lack write access to) the host directory/volume backing `/data` |
| The bind-mount source directory (e.g. `./data`, or a path like `/etc/docker-apps/m3undle/data`) didn't exist before the first `docker compose up` | Docker auto-creates missing bind-mount source directories as `root`, regardless of the container's `user:` directive — the single most common cause of this error. **Create the directory yourself before the first start**, so it's owned by whoever you're running the container as. |
| Crash happens immediately on every restart, same path every time | Same root cause — not a transient issue, won't resolve itself with a restart |
| Only a subdirectory fails while the rest of `/data` works | Usually that one subdirectory got created by a different process (e.g. `sudo`, or a root-owned bind mount auto-created by Docker) with different ownership than the rest |

M3Undle deliberately doesn't swallow this error and fall back to a different directory — a silently-relocated data directory is worse than a clear crash. See `RuntimePaths.EnsureDirectoryExists` if you're curious about the exact mechanics.

**Fix:**

1. Find out which user the container actually runs as:
   ```bash
   docker inspect m3undle --format '{{.Config.User}}'
   ```
   If you're using the `PUID`/`PGID` pattern from [Build and Run From Source](https://github.com/Sydney-Elvis/M3Undle/blob/main/docs/dev/BUILD.md), check your `.env` file instead.
2. Check current ownership on the host:
   ```bash
   ls -la ./data
   ```
3. Fix it to match:
   ```bash
   sudo chown -R <uid>:<gid> ./data
   ```

If you're using a named Docker volume rather than a bind mount, this class of problem is much rarer — see [Named volumes vs. bind mounts](../reference/docker-compose.md#named-volumes-vs-bind-mounts).

## 4. Container restarts under load, not on startup

If M3Undle starts fine and only cycles later — especially exit code `137` — a Docker memory limit is more likely than a code crash: Docker kills the container outright when it exceeds its memory limit, which looks the same as a crash from the outside. Before assuming it's a bug, open [System Resources](../guides/system-resources.md) while the container is healthy and check **OOM kills since start** and **Memory limit hits since start** — any nonzero count there confirms the container has hit its configured memory ceiling rather than crashed on its own.

## Still stuck

Include the M3Undle version, your `compose.yaml` **with secrets removed** (encryption key, admin password, any credentials), the exact error from the logs, and the output of `docker inspect <container> --format '{{json .Mounts}}'`. Find the exact version, build commit, and build date by clicking the version number in the footer, which opens an **About** panel.
