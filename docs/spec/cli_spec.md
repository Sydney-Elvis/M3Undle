# IPTV CLI – Commands & Usage

This document defines the command surface and runtime behavior for the `iptv` CLI. It references the shared configuration described separately in **Config – Schema**.

---

## Subcommands

```
iptv groups   [options]
iptv run      [options]
```

---

## Command: `iptv groups`

Create or refresh the **group selection file** used by filtering.

### Flags

* `--playlist-url <url>`  (or `--config ... --profile ...`)
* `--out-groups <path>` (optional). If omitted, groups are written to **stdout** for piping.
* `--verbose`
* `--live`
* `--force` (optional). Override file validation checks (version mismatch, missing headers, etc.)

### Note on `--live` flag

- `--live` (optional): when provided, only **live** streams are enumerated. This filter works by **excluding** VOD content (movies and series) based on URL patterns:
  - URLs containing `/movie/` or `/movies/` path segments are excluded
  - URLs containing `/series/` path segments are excluded
  - URLs with `type=vod`, `type=movie`, `type=series`, `kind=vod`, `kind=movie`, or `kind=series` query parameters are excluded
  - Everything else is considered a live stream
  
  This approach is more reliable than trying to detect live streams directly, as VOD content has consistent URL patterns across most IPTV providers.

### Note on `--force` flag

- `--force` (optional): bypasses file validation checks. Use this to modify groups files created with different versions or files that don't match the expected format. A backup is still created before modification.

### Behavior

* Downloads groups and writes a plain text file, one group per line. 

### Examples

```bash
iptv groups \
  --playlist-url "https://host/get.php?username=U&password=P&type=m3u_plus&output=ts" \
  --out-groups ./groups.txt \
  --preserve-comments \
  --verbose
  --live
# Edit groups.txt → comment to KEEP, leave bare to DROP
```

```bash
iptv groups --config /etc/iptv/config.yml --profile default --out-groups /config/groups.txt --verbose --live
```

---

## Command: `iptv run`

One-shot pipeline: **fetch → filter → write**.

### Flags

* **Inputs**

  * `--playlist-url <url>` (required if `--config` not provided)
  * `--epg-url <url>` (optional)
* **Filtering**

  * `--groups-file <path>` (optional) — text file; one group per line:
  * `--live` (optional) - details in Note
    * **Commented (`#`) = KEEP**
    * **Uncommented = DROP**
* **Outputs**

  * `--out-playlist <path>` (recommended; use `-` for **stdout**)
  * `--out-epg <path>` (required if `--epg-url` provided; use `-` for **stdout**)
* **Runtime**

  * `--config <path>` (optional)
  * `--profile <name>` (default `default`)
  * `--verbose`

#### Note on `--live`

- `--live` (optional): when provided, only **live** streams are processed. This filter works by **excluding** VOD content (movies and series) based on URL patterns:
  - URLs containing `/movie/` or `/movies/` path segments are excluded
  - URLs containing `/series/` path segments are excluded
  - URLs with `type=vod`, `type=movie`, `type=series`, `kind=vod`, `kind=movie`, or `kind=series` query parameters are excluded
  - Everything else is considered a live stream
  
  This filter is applied **before** reading and applying the groups file, making it more efficient. This approach is more reliable than trying to detect live streams directly, as VOD content has consistent URL patterns across most IPTV providers.
#### Note on Stdout behavior
- If **only a playlist** is being produced and `--out-playlist` is **omitted**, the playlist is written to **stdout**.
- If an **EPG** is also being produced, at least one of `--out-playlist` or `--out-epg` must be provided; alternatively, pass `--out-playlist -` or `--out-epg -` to write that artifact to **stdout**.

### Behavior

1. If `--config` is present, load profile; then apply any flag overrides.
2. If a suitable `.env` exists (see Environment behavior), substitute every `%VAR%` token (for example `%USER%`, `%ACCOUNT_PASS%`, `%API_TOKEN%`) **inside playlist/EPG URL strings only** before fetching.
3. Fetch playlist (and EPG if provided).
4. Parse M3U/XMLTV.
5. If `--live` is provided, exclude VOD content (movies/series):
   - Entries with URLs containing `/movie/`, `/movies/`, or `/series/` path segments are dropped
   - Entries with URLs containing `type=vod`, `type=movie`, `type=series`, `kind=vod`, `kind=movie`, or `kind=series` query parameters are dropped
   - All other entries are kept as live streams
6. If `--groups-file` provided: **drop** any groups that are not commented (commented lines are kept).
7. Write outputs atomically to `--out-playlist`/`--out-epg`.
8. Log newly discovered groups and summarize channel changes **only for kept groups**.

### Examples

```bash
iptv run \
  --playlist-url "https://host/get.php?username=U&password=P&type=m3u_plus&output=ts" \
  --epg-url "https://host/xmltv.php?username=U&password=P" \
  --groups-file ./groups.txt \
  --out-playlist ./out/playlist.m3u \
  --out-epg ./out/epg.xml \
  --verbose
  --live
```

**Config-driven:**

```bash
iptv run --config /etc/iptv/config.yml --profile default --verbose --live
# If /etc/iptv/.env exists, any matching %VAR% tokens in playlist/EPG URLs are replaced before download.
```

**Using credentials from .env:**

```powershell
# PowerShell example
iptv groups --playlist-url "http://host/get.php?username=%USER%&password=%PASS%&type=m3u_plus&output=ts" --out-groups groups.txt --verbose
```

```bash
# Bash/Linux example
iptv groups --playlist-url "http://host/get.php?username=%USER%&password=%PASS%&type=m3u_plus&output=ts" --out-groups groups.txt --verbose
```

---

## Environment behavior (strict)
* **Search order** – the CLI looks for `.env` files in a single location per invocation:
  1. The directory that contains the `--config` file (when `--config` is supplied)
  2. Otherwise, the current working directory
  There is no additional fallback.
* **Tokens** – every key inside `.env` (case-insensitive) becomes a `%VAR%` placeholder. Any matching `%VAR%` inside playlist or EPG URLs (from flags or config) is replaced before network calls. Use descriptive keys such as `%USER%`, `%PASS%`, `%ACCOUNT_EMAIL%`, or `%API_TOKEN%`.
* **Scope** – substitution is limited to playlist/EPG URLs. Other config fields remain untouched.
* If no `.env` is present at the search location, no substitution occurs. Embedding credentials directly in URLs is still fully supported.
* **Shell compatibility** – the `%...%` format works reliably in PowerShell, Bash, CMD, and Zsh without shell expansion.

---

## Exit codes

* `0` success
* `2` config/validation error
* `3` network error
* `4` auth error
* `5` IO error
* `6` parse error

---

## Minimal cron example

```bash
*/30 * * * * root iptv run --config /etc/iptv/config.yml --profile default --live
