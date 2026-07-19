# M3Undle Config – Schema

This document defines the `config.yaml` schema M3Undle Web can import providers from (see **Settings → Providers → Import**, and [Install with Docker § Config file integration](https://sydney-elvis.github.io/M3Undle/getting-started/install-with-docker/)). It originated as a schema shared with the standalone `bndl` CLI; the CLI has since moved to the separate `m3undle-cli` repository and owns its own copy of this schema there — the two may diverge over time. This document describes Web's import behavior only.

- **Formats supported:** YAML (preferred) and JSON
- **Profiles:** A `config.yaml` file may define multiple named profiles. Each one becomes an importable provider definition in the Web UI; there is no `--profile` flag here — that belongs to the separate CLI tool.
- **Env substitution:** `%VAR%` placeholders inside string fields will be replaced with values from the `.env` file

---

## Top-Level Structure
```yaml
profiles:
  <profileName>:
    inputs: { ... }
    filters: { ... }
    mapping: { ... }
    output: { ... }
    logging: { ... }
```

> **Required keys per profile:** `inputs`, `output`. Others are optional with defaults.

> **Scope note:** M3Undle Web's import (`ConfigYamlService`) only reads `profiles.<name>.inputs.playlist` and `profiles.<name>.inputs.epg` — that's all it needs to create a provider. `filters`, `mapping`, `output`, and `logging` below are part of the schema for compatibility with the separate `m3undle-cli` tool; Web parses and ignores them. Don't expect setting them in an imported `config.yaml` to change Web behavior — use the Channel Mapping page for filtering/renaming instead.

---

## 1) `inputs`
Holds source locations and download settings for playlist and EPG.

```yaml
inputs:
  playlist:
    url: "%PROVIDER_PLAYLIST_URL%"   # string, required
    headers:                          # map<string,string>, optional
      User-Agent: "M3Undle/1.0"
    timeoutSeconds: 30                # int, optional (default 30)
    retries: 3                        # int, optional (default 3)
    maxDownloadMb: 200                # int, optional; safety cap

  epg:
    url: "%PROVIDER_EPG_URL%"        # string, optional (omit to skip)
    headers: {}
    allowCompressed: true             # bool, optional (default true) – autodetect .gz/.zip
    timeoutSeconds: 60
    retries: 3
    maxDownloadMb: 500
```

**Validation**
- `playlist.url` must be a valid `http(s)` URL
- `epg.url` optional; if absent, EPG stage is skipped
- `timeoutSeconds` ∈ [5, 600]
- `retries` ∈ [0, 10]

---

## 2) `filters`
Declarative rules for trimming channels.

```yaml
filters:
  includeGroups: []                     # list<string>; if non-empty, only these groups are kept
  excludeGroups: []                     # list<string>; removed even if included elsewhere
  excludeTitleRegex: "(?i)\\b(4k|uhd)\\b"  # string; .NET regex; optional
  dropListFile: "/config/drop-groups.txt"   # path to LF/CRLF text file, one group per line; optional
  groupsFile: "/config/groups.txt"          # path for curated groups list (preferred destination for `bndl groups`)
```

**Notes**
- Matching is **exact** for group names (case-sensitive by default). Consider normalizing names in `mapping` if needed.
- Regex uses .NET engine. Invalid regex → config validation error.
- These fields are CLI-only (see the scope note above). In the `m3undle-cli` tool, `bndl groups --config /path/config.yaml --profile my-profile` writes the curated group list to `filters.groupsFile` when it is set, otherwise `filters.dropListFile`. See that repo's own docs for current CLI usage.

---

## 3) `mapping`
Lightweight normalization/remap layer.

```yaml
mapping:
  tvgIdRemap:                           # map<string,string>; optional
    "NBC-NewYork": "WNBC"
  channelRename:                        # map<string,string>; optional (by exact channel name)
    "US FOX 5 (WNYW) New York": "FOX 5 New York"
  groupRename:                          # map<string,string>; optional
    "CBS Locals": "Local Networks"
  collapseLocalGroups: false            # bool; if true, merge common local group variants
```

**Behavior**
- Remaps apply **after** filtering by group/title.
- `collapseLocalGroups` is a best-effort heuristic (may be ignored until implemented).

---

## 4) `output`
Where and how to write the final artifacts.

```yaml
output:
  playlistPath: "/data/out/playlist.m3u"   # required
  epgPath: "/data/out/epg.xml"              # required if EPG used
  atomicWrites: true                         # bool, default true (tmp + fsync + rename)
  gzip: false                                # bool; if true, also write .gz alongside
  permissions:
    fileMode: "0644"                         # string, optional (POSIX-style)
    dirMode: "0755"                          # string, optional
  tmpDir: "/data/tmp"                        # string, optional (defaults next to output)
```

**Validation**
- `playlistPath` must be absolute and writable at runtime
- `epgPath` required if `inputs.epg.url` is present
- Safety: writing outside an allowed root may be blocked in Docker

---

## 5) `logging`
Controls verbosity and optional file logging.

```yaml
logging:
  level: "Information"                  # "Debug" | "Information" | "Warning" | "Error"
  file: "/data/logs/bndl-run.log"       # optional; rotate policy TBD
  format: "text"                         # "text" | "json" (CLI may override with --json)
```

---

## Environment Variable Expansion
- Any string field may contain `%NAME%`; values are read from the `.env` file by CLI-style consumers (see the `m3undle-cli` repo's `docs/spec/env_usage.md`).
- Undefined variables → validation error, unless the entire field is optional and omitted by design.
- The `%VAR%` format is shell-safe: not expanded by PowerShell, Bash, Zsh, or CMD.

**Example `.env`**
```
PROVIDER_PLAYLIST_URL=https://example.test/get.php?username=u&password=p&type=m3u_plus&output=ts
PROVIDER_EPG_URL=https://example.test/xmltv.php?username=u&password=p
```

---

## Complete Example (YAML)
```yaml
profiles:
  default:
    inputs:
      playlist:
        url: "%PROVIDER_PLAYLIST_URL%"
        headers:
          User-Agent: "M3Undle/1.0"
      epg:
        url: "%PROVIDER_EPG_URL%"
        allowCompressed: true

    filters:
      includeGroups: ["USA | News", "USA | Sports"]
      excludeGroups: ["International"]
      excludeTitleRegex: "(?i)\\b(4k|uhd)\\b"
      dropListFile: "/config/drop-groups.txt"

    mapping:
      tvgIdRemap:
        "NBC-NewYork": "WNBC"
      groupRename:
        "CBS Locals": "Local Networks"

    output:
      playlistPath: "/data/out/playlist.m3u"
      epgPath: "/data/out/epg.xml"
      atomicWrites: true
      gzip: false

    logging:
      level: "Information"
      file: "/data/logs/bndl-run.log"
```

---

## JSON Example
```json
{
  "profiles": {
    "default": {
      "inputs": {
        "playlist": {
          "url": "%PROVIDER_PLAYLIST_URL%",
          "headers": { "User-Agent": "M3Undle/1.0" },
          "timeoutSeconds": 30,
          "retries": 3
        },
        "epg": { "url": "%PROVIDER_EPG_URL%", "allowCompressed": true }
      },
      "filters": {
        "includeGroups": ["USA | News", "USA | Sports"],
        "excludeGroups": ["International"],
        "excludeTitleRegex": "(?i)\\b(4k|uhd)\\b"
      },
      "mapping": {
        "tvgIdRemap": { "NBC-NewYork": "WNBC" },
        "groupRename": { "CBS Locals": "Local Networks" }
      },
      "output": {
        "playlistPath": "/data/out/playlist.m3u",
        "epgPath": "/data/out/epg.xml",
        "atomicWrites": true,
        "gzip": false
      },
      "logging": { "level": "Information" }
    }
  }
}
```

---

A multi-profile CLI workflow example (with `bndl groups`/`bndl run` commands) previously lived here; it described the separate `m3undle-cli` tool and was removed in the 2026-07-18 documentation cleanup. See that repo's own docs for current CLI usage — the schema above (`inputs`, `filters`, `mapping`, `output`, `logging`) is unchanged and still applies there.

---

## Defaults & Behaviors (Reference)
- `inputs.playlist.timeoutSeconds` = 30; `retries` = 3; `maxDownloadMb` = 200
- `inputs.epg.allowCompressed` = true; `timeoutSeconds` = 60; `retries` = 3; `maxDownloadMb` = 500
- `filters.*` = empty lists/nulls → no filtering
- `mapping.*` absent → no remaps/renames
- `output.atomicWrites` = true; `gzip` = false
- `logging.level` = `Information`; `format` = `text`

---

## Validation & Error Messages
- On invalid or missing required fields, tools must emit a **single, clear line** with the exact key path and reason, e.g.:
  - `config:profiles.default.inputs.playlist.url is required`
  - `config:filters.excludeTitleRegex is invalid regex: unterminated group`

---

## Security Guidance
- Prefer environment variables or secret stores for URLs containing credentials.
- The application must redact values for keys named `Authorization`, `X-Api-Key`, `Password`, or URLs containing `password=` when logging.

---

## Forward Compatibility (Reserved Keys)
- `outputs[]` (plural) – future multi-sink support
- `auth:` section – pluggable credential refs
- `scheduling:` – cron/interval controls for server/daemon modes

Keep existing names stable; add new optional fields rather than renaming.
