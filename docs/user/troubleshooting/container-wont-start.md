# Container Won't Start or Keeps Restarting

Two different symptoms land on this page, with different causes:

- **The container exits or keeps restarting** — almost always a volume or permission problem rather than an M3Undle bug. Work through steps 1–4 below.
- **The container stays up but never becomes healthy** — the process is alive and simply never finished starting. Start with the section directly below.

## Container runs but never becomes healthy

`docker ps` shows `Up … (unhealthy)`, the log stops a line or two after startup, and there is **no exception and no `Now listening on:`**. The process is alive — it just never finished starting, so nothing is listening on port 8080 for the health check to reach.

A healthy startup reaches `Application started.` within a few seconds:

```text
Starting M3Undle 1.0.0-beta.8.1 (BuildDateUtc=..., BuildNumber=42)
Runtime: linux-amd64, .NET 10.0.0, container=True, user=root, timezone=America/New_York
Storage: data=/data [ext4 at /data, writable, 407.6 GB free of 491.1 GB]; logs=/data/logs [same volume as data, writable]; config=/config [ext4 at /config, writable]
Database: /data/m3undle.db [2.8 GB + 113.6 MB WAL]
Checking database /data/m3undle.db for pending migrations.
Database ready in 1842 ms (6 migration(s) applied).
Now listening on: http://[::]:8080
Application started. Press Ctrl+C to shut down.
```

Whichever line yours stops on narrows the cause considerably. Stopping right after `Checking database …` points at the `/data` volume, and the `Storage:` line immediately above it tells you what that volume actually is:

| Filesystem shown | Verdict |
|---|---|
| `ext4`, `xfs`, `btrfs`, `overlay` | Fine |
| `9p`, `cifs`, `smbfs`, `nfs`, `fuse.*` | **Likely the cause** — SQLite's file locking is unreliable on these |
| `NOT WRITABLE` instead of `writable` | A permissions problem — see [Permission denied on `/data`](#3-permission-denied-on-data-or-a-subdirectory-of-it) |

The quickest way to confirm a mount problem is to swap `/data` onto a Docker-managed volume and change nothing else:

```bash
docker volume create m3undle_test
```

```yaml
volumes:
  - ./config:/config
  - m3undle_test:/data   # was ./data:/data
```

If it starts cleanly, the filesystem behind your old `/data` was the problem. This is by far the most common cause on **Windows and macOS**, where Docker Desktop reaches host folders through a translation layer that doesn't provide the locking SQLite needs — see [Named volumes vs. bind mounts](../reference/docker-compose.md#named-volumes-vs-bind-mounts).

If the log stops *before* `Checking database`, the mount isn't the issue — collect the output from the sections below and open an issue.

### Stops right after "Applying N pending database migration(s)"

This one has a specific cause. Before applying migrations, Entity Framework records a lock in an internal `__EFMigrationsLock` table. If M3Undle is force-stopped while that lock is held — most often by restarting during a long provider or series import — the row is left behind. Every later start then waits **indefinitely** for a lock that will never be released, with no error and no further output. It's a [documented limitation of EF Core's SQLite provider](https://learn.microsoft.com/ef/core/providers/sqlite/limitations#concurrent-migrations-protection).

**M3Undle clears these automatically from v1.0.0-beta.9 onward**, logging:

```text
[WRN] Removed 1 abandoned EF Core migration lock row(s) (oldest taken ..., 286.6 hours ago).
```

If you're on an older build, either update, or clear it by hand:

```bash
docker stop m3undle
docker run --rm --volumes-from m3undle alpine sh -c \
  "apk add -q sqlite && sqlite3 /data/m3undle.db 'DELETE FROM __EFMigrationsLock;'"
docker start m3undle
```

**Your data is fine.** A stuck lock blocks migrations; it doesn't damage anything. There's no need to rebuild the database or re-import providers — startup typically completes in well under a second once the lock is gone.

## 1. Read the actual crash

```bash
docker compose logs --tail 200 m3undle
```

A stack trace ending in something like `System.UnauthorizedAccessException: Access to the path '/data/...' is denied` (often paired with the container exiting with code 139) means the process itself is crashing on startup — before the web server or health checks are even relevant. Don't chase client/network symptoms until this is clean.

## 2. Confirm what's actually mounted and configured

Docker Compose files drift from the documented examples over time — volumes get added for a specific reason and never written down, or an environment variable gets copied from an old example and stops matching what's current. Before debugging further, see what's actually in effect:

```bash
docker compose config
docker inspect m3undle --format '{{json .Mounts}}'
```

Both work identically on Linux, macOS, and Windows. In PowerShell you can pipe the second one through `ConvertFrom-Json | ConvertTo-Json` to pretty-print it; on Linux and macOS, `| python3 -m json.tool` does the same.

To inspect what the container itself sees — useful when `/data` is a named volume and therefore has no host path to look at — run the check inside it:

```bash
docker exec m3undle df -T /data
docker exec m3undle ls -la /data
docker exec m3undle sh -c 'tail -n 200 /data/logs/app-*.log'
```

These work while the container is hung, since the process is still running.

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

!!! note "Windows and macOS"
    Steps 2 and 3 above are Linux-specific — `ls -la`, `chown`, and the `PUID`/`PGID` pattern don't apply on Docker Desktop, which maps host files to the container user for you. If M3Undle reports a `/data` problem there, the fix is almost never ownership; it's the filesystem behind the mount. See [Container runs but never becomes healthy](#container-runs-but-never-becomes-healthy).

## 4. Container restarts under load, not on startup

If M3Undle starts fine and only cycles later — especially exit code `137` — a Docker memory limit is more likely than a code crash: Docker kills the container outright when it exceeds its memory limit, which looks the same as a crash from the outside. Before assuming it's a bug, open [System Resources](../guides/system-resources.md) while the container is healthy and check **OOM kills since start** and **Memory limit hits since start** — any nonzero count there confirms the container has hit its configured memory ceiling rather than crashed on its own.

## Still stuck

Include the M3Undle version, your `compose.yaml` **with secrets removed** (encryption key, admin password, any credentials), the exact error from the logs, and the output of `docker inspect <container> --format '{{json .Mounts}}'`. Find the exact version, build commit, and build date by clicking the version number in the footer, which opens an **About** panel.
