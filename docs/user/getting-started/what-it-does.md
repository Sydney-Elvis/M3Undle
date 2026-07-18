# What M3Undle Does

M3Undle is a self-hosted IPTV lineup manager and proxy for large M3U, XMLTV, Xtream, and HDHomeRun-style provider catalogs.

Providers commonly deliver 10,000–50,000+ channels across multiple regions, languages, sports feeds, and temporary event groups. Most of that is irrelevant to any one household — wrong language, duplicate regions, categories nobody watches. M3Undle exists to give you explicit control over what actually gets published, and to keep that publication stable over time.

## The core workflow

1. **Add a provider** — a playlist URL, a local file, or an Xtream Codes account.
2. **Exclude the provider groups you don't want** — international packages, duplicate regions, categories you never watch.
3. **Build your own output groups** (for example, `Locals`) from channels across one or more provider groups.
4. **Number and order** those channels the way you want clients to see them.
5. **Publish the lineup.**
6. **Point your clients at M3Undle** instead of the raw provider — NextPVR, Jellyfin, IPTVnator, IPTV Smarters, or anything else that consumes M3U, XMLTV, Xtream, or HDHomeRun-compatible endpoints.

Instead of every client parsing the full 30,000-channel provider list, each client sees only the lineup you built.

## What that gets you

- **A published, versioned lineup.** M3Undle builds versioned output and serves the last-known-good version — if a refresh fails, your clients keep working on what was already published.
- **A stream proxy, not a redirect.** Client stream URLs point at M3Undle, never at the raw provider URL. Provider credentials are never exposed to clients, and the same upstream connection is shared across multiple viewers of the same channel.
- **Stable channel identity.** Published stream keys and channel numbers stay stable across refreshes, so DVR mappings and client configurations don't break just because a provider reordered their playlist.
- **Visibility into what's happening.** Health probes, Prometheus-compatible metrics, and admin diagnostics are built in — see [Observability](../concepts/observability.md).

## What it isn't

M3Undle doesn't provide playlists, streams, channels, or guide data of its own. It's a management and proxy layer on top of sources you configure yourself. You're responsible for the legality and licensing of any source you add.

!!! note
    **Emby** and **Plex** support Live TV and DVR only with a paid subscription (Emby Premiere and Plex Pass, respectively). Full compatibility with M3Undle has not been validated without those subscriptions.

## Next step

**[Install with Docker →](install-with-docker.md)**
