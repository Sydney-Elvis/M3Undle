<div align="center">

# M3Undle

**Turn oversized IPTV provider lists into lineups your apps can actually use.**

[![Build](https://img.shields.io/github/actions/workflow/status/Sydney-Elvis/M3Undle/dotnet.yml?branch=main&style=flat-square&label=build)](https://github.com/Sydney-Elvis/M3Undle/actions/workflows/dotnet.yml)
[![Release](https://img.shields.io/github/v/release/Sydney-Elvis/M3Undle?include_prereleases&style=flat-square)](https://github.com/Sydney-Elvis/M3Undle/releases/latest)
[![License](https://img.shields.io/github/license/Sydney-Elvis/M3Undle?style=flat-square)](LICENSE)

[**Sponsor**](https://github.com/sponsors/Sydney-Elvis) | [**Buy Me a Coffee**](https://buymeacoffee.com/jake1164s) | [**Changelog**](https://github.com/Sydney-Elvis/M3Undle/releases) | [**Docker**](https://github.com/Sydney-Elvis/M3Undle/pkgs/container/m3undle)

</div>

---

M3Undle is a self-hosted IPTV lineup manager and proxy for large M3U, XMLTV, Xtream, and HDHomeRun-style provider catalogs.

It helps you filter out provider groups you do not care about, collect the channels you do want into your own groups, assign stable channel numbers, and publish a smaller lineup to DVRs, media servers, browser-based players, and IPTV apps.

Works with clients such as NextPVR, Jellyfin, Emby, Plex, IPTVnator, IPTV Smarters, and other apps that consume M3U, XMLTV, Xtream, or HDHomeRun-compatible endpoints.

![M3Undle dashboard showing system status, active profile, published channel counts, and output URLs](docs/images/readme-dashboard.png)

> [!IMPORTANT]
> **Alpha Status**
>
> M3Undle is in the final alpha stage: the main workflow is implemented, most planned features are in place, and the remaining Alpha 6 work is focused on user experience, provider onboarding, profile and refresh behavior, stream stability, monitoring, client compatibility, and first-run documentation.
>
> It is stable enough for real LAN testing and personal use, but it is still alpha software. Expect rough edges and possible provider or client-specific issues before beta.

## Run it

M3Undle is published to GitHub Container Registry.

Pull the current alpha image:

    docker pull ghcr.io/sydney-elvis/m3undle:alpha

Create a working directory:

    mkdir m3undle
    cd m3undle
    mkdir config

Create `compose.yaml`:

    services:
      m3undle:
        image: ghcr.io/sydney-elvis/m3undle:alpha
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

Generate an encryption key:

    openssl rand -base64 32

Paste the generated value into `M3UNDLE_ENCRYPTION_KEY`, then start M3Undle:

    docker compose up -d

Open the web UI:

    http://<host>:8080

Port `8080` serves the web UI, M3U, XMLTV, Xtream, and general compatibility endpoints. Port `5004` serves HDHomeRun-compatible tuning.

The `config` folder is bind-mounted so configuration files stay easy to inspect and back up. Runtime state, logs, snapshots, and browser playback working files use the Docker-managed `m3undle_data` volume.

Use `alpha` for the latest alpha build. Pin a specific version, such as `v1.0.0-alpha.5`, if you want repeatable updates.

`latest` is not used during alpha or beta. It will be introduced no earlier than the release candidate track.

For full Docker options, including local M3U file mounts, authentication, HDHomeRun discovery, and advanced networking, see [docs/DOCKER.md](docs/DOCKER.md).


## First workflow

Start by reducing the provider catalog to something your clients should actually see.

1. Add a provider.
2. Exclude provider groups you do not want, such as international packages, duplicate regions, or categories you never watch.
3. Create your own output group, such as `Locals`.
4. Add selected channels from one or more provider groups into that output group.
5. Number and order those channels the way you want them to appear.
6. Publish the lineup.
7. Point NextPVR, Jellyfin, Emby, Plex, IPTVnator, IPTV Smarters, or another client at the published output.

Instead of making every client parse the full provider list, M3Undle publishes only the lineup you built.

![Channel Mapping page with group filter applied, showing mapped and unmapped groups with channel counts](docs/images/readme-filter.png)

![Custom Locals group with channel search showing selected local channels with assigned numbers](docs/images/readme-channel-search.png)

## What it does

### Catalog cleanup

M3Undle lets you exclude provider groups you do not want, browse what remains, and filter channels by keyword, glob, or regex.

### Custom lineups

Create your own output groups, add selected channels from provider groups, rename groups for the published output, and control channel numbering, pinning, and order.

### Guide and mapping

Manage EPG sources, merge XMLTV data, set guide priority, override `tvg-id` values, and review new channels before they appear in the published lineup.

### Client outputs

Publish the same managed lineup through M3U, XMLTV, HDHomeRun-compatible, and Xtream-compatible endpoints.

### Streaming

Proxy live streams through M3Undle, hide provider credentials from clients, share live streams across multiple downstream clients, and monitor active stream sessions.

![Stream Monitor showing two active sessions with buffer usage and three connected clients sharing streams](docs/images/readme-streams.png)

### Profiles and publishing

Use named profiles, switch the active published profile, keep published history, and fall back to last-known-good output when needed.

### Administration

Configure refresh schedules, endpoint security, optional UI authentication, downstream notifications, provider stream limits, and service behavior from the web UI or Docker configuration.

## Client endpoints

After publishing a lineup, point your clients at M3Undle instead of the raw provider URL.

| Client type | URL |
|---|---|
| M3U playlist | `http://<host>:8080/m3u/m3undle.m3u` |
| XMLTV guide | `http://<host>:8080/xmltv/m3undle.xml` |
| HDHomeRun-style tuner | `http://<host>:5004` |
| Xtream-style API | `http://<host>:8080` |

For HDHomeRun-style clients, manual tuner setup is usually the most reliable option. Use `http://<host>:5004`.

For Xtream-style clients, add M3Undle as the server URL and use the endpoint credentials configured in M3Undle.

## Minimal configuration

Most settings can be changed later from the web UI. For a first run, only a few Docker settings matter.

| Setting | Required | Default | Purpose |
|---|---:|---|---|
| `TZ` | No | Host/default timezone | Sets timestamps for logs and scheduled refresh behavior. |
| `M3UNDLE_ENCRYPTION_KEY` | Required for Xtream providers | None | Encrypts stored Xtream provider passwords. Keep this value backed up. |
| `/config` | Yes | None | Human-readable configuration files and optional provider credential placeholders. |
| `/data` | Yes | None | Database, snapshots, logs, runtime state, and temporary browser playback files. |
| `5004` | Recommended | `5004` | HDHomeRun-compatible tuning endpoint. |
| `8080` | Yes | `8080` | Web UI and general client endpoints. |

The README example bind-mounts `./config` so it is easy to inspect and back up. Runtime data uses a Docker-managed volume.

UI authentication and endpoint security are configured separately. The UI can be protected with environment variables, while client-facing endpoint credentials are managed inside M3Undle.

See [docs/DOCKER.md](docs/DOCKER.md) for advanced Docker options.

## Security and usage notes

M3Undle is designed for self-hosted use on a trusted network.

For first-run testing, the web UI can run without authentication. Before exposing it outside your LAN, enable UI authentication, configure endpoint security, and put it behind a reverse proxy or firewall rules you trust.

Client-facing endpoints are separate from the web UI. Use endpoint credentials for M3U, XMLTV, stream, HDHomeRun, and Xtream access when clients need to connect from outside a trusted network.

Provider credentials should stay in M3Undle. Published stream URLs are proxied so clients do not need direct provider URLs.

Respect your provider stream limits. M3Undle can help share live streams and apply configured limits, but it cannot make an upstream provider allow more connections than your account supports.

You are responsible for the sources you configure and for following the terms that apply to those sources.

## Troubleshooting

Start with the container logs:

    docker logs m3undle --tail 200

Check that the web UI is reachable:

    curl -I http://<host>:8080

Check that the M3U endpoint is reachable:

    curl -I http://<host>:8080/m3u/m3undle.m3u

Check that the XMLTV endpoint is reachable:

    curl -I http://<host>:8080/xmltv/m3undle.xml

Check the HDHomeRun discovery endpoint:

    curl http://<host>:5004/discover.json

Common first checks:

| Problem | Check |
|---|---|
| Web UI does not load | Confirm the container is running and port `8080` is mapped. |
| No channels appear in a client | Publish a lineup first, then check the M3U endpoint directly. |
| XMLTV guide is missing | Confirm an EPG source is configured and the guide has been published. |
| HDHomeRun client cannot find M3Undle | Add the tuner manually with `http://<host>:5004`. Auto-discovery depends on Docker networking and multicast. |
| Xtream provider fails to save | Confirm `M3UNDLE_ENCRYPTION_KEY` is set and has not changed since the provider was added. |
| Browser playback fails | Check stream status in the web UI and confirm the `/data` volume is writable. |
| Streams stop or fail to start | Check provider limits, active stream sessions, and container logs. |

When reporting an issue, include the M3Undle version tag, Docker compose file with secrets removed, client name, endpoint type, and relevant logs.

## Roadmap

Current work is focused on finishing Alpha 6, then moving into beta testing.

Planned release path:

1. Alpha 6: final feature cleanup, diagnostics, monitoring, stream hardening, and UI polish.
2. Beta: broader testing, documentation cleanup, client compatibility work, and bug fixes.
3. Release candidate: final validation and packaging.
4. v1.0.0: stable release.

After v1.0.0, planned work moves toward more advanced provider handling:

- multiple sources per profile
- fallback sources
- per-provider VPN

## Support

M3Undle is built as an open-source project and is currently focused on getting through alpha, beta, and v1.0.0.

If you find it useful and want to support the work:

<a href="https://buymeacoffee.com/jake1164s"><img src="docs/images/violet-button.png" alt="Buy Me A Coffee" width="200"></a>

You can also [sponsor M3Undle on GitHub](https://github.com/sponsors/Sydney-Elvis).

## Development and contributing

M3Undle is built with .NET and the web UI is server-side Blazor.

For now, contribution work is expected to be issue-driven. If you want to help, start with an open issue or file a new one describing the problem before opening a larger pull request.

Basic local workflow:

    git clone https://github.com/Sydney-Elvis/M3Undle.git
    cd M3Undle
    dotnet restore
    dotnet build
    dotnet test

Before submitting a pull request:

- keep changes focused
- include tests when practical
- avoid mixing formatting-only changes with functional changes
- describe the client, provider type, and endpoint path involved if the change affects playback or compatibility

More detailed contributor docs will be added as the project moves toward beta.

## Disclaimer

> [!IMPORTANT]
> M3Undle does not provide playlists, streams, channels, guide data, subscriptions, or other media content.
>
> M3Undle is a self-hosted management and proxy tool. It only works with sources that you configure, such as provider URLs, local playlist files, XMLTV guide sources, and client credentials.
>
> You are responsible for the legality, licensing, and terms of use for any source you add to M3Undle. Do not use M3Undle to access, distribute, or restream content you are not authorized to use.

## License

M3Undle is licensed under the Apache License 2.0. See [LICENSE](LICENSE).


