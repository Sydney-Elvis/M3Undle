# M3Undle

**Turn oversized IPTV provider lists into lineups your apps can actually use.**

M3Undle is a self-hosted IPTV lineup manager and proxy for large M3U, XMLTV, Xtream, and HDHomeRun-style provider catalogs. It helps you filter out provider groups you don't care about, collect the channels you do want into your own groups, assign stable channel numbers, and publish a smaller lineup to DVRs, media servers, browser-based players, and IPTV apps.

Works with clients such as NextPVR, Jellyfin, IPTVnator, IPTV Smarters, and other apps that consume M3U, XMLTV, Xtream, or HDHomeRun-compatible endpoints.

!!! warning "Beta status"
    M3Undle is in beta. The core workflow — streaming, HDHomeRun integration, observability, lineup management — is fully implemented. Beta focuses on broader DVR client validation, documentation, and final hardening before a stable release.

## Where to start

- New to M3Undle? Follow the complete first-run path: **[understand the workflow](getting-started/what-it-does.md)**, **[install with Docker](getting-started/install-with-docker.md)**, **[add a provider](getting-started/add-first-provider.md)**, then **[build and publish a lineup](getting-started/create-first-lineup.md)**.
- Container running but no channels visible? Starting Docker is only the installation step. You still need to **[add a provider](getting-started/add-first-provider.md)**, map channels, and build the output.
- Already configured and publishing a lineup? Jump to **[Guides](guides/build-a-lineup.md)** or **[Reference](reference/environment-variables.md)**.
- Connecting a specific client? See **[Clients](clients/jellyfin.md)**.
- Something not working? See **[Troubleshooting](troubleshooting/client-cannot-connect.md)**.

## Links

- [GitHub repository](https://github.com/Sydney-Elvis/M3Undle)
- [Releases](https://github.com/Sydney-Elvis/M3Undle/releases)
- [Docker image](https://github.com/Sydney-Elvis/M3Undle/pkgs/container/m3undle)
- [Sponsor](https://github.com/sponsors/Sydney-Elvis)

---

*M3Undle is in beta and its documentation is actively expanding. Installation, lineup management, Jellyfin and HDHomeRun setup, security, monitoring, troubleshooting, and configuration reference are covered now; remaining pages are tracked in [issue #114](https://github.com/Sydney-Elvis/M3Undle/issues/114).*
