# Jellyfin

M3Undle currently integrates with Jellyfin's **Live TV** feature. It can provide live channels and guide data in two modes: M3U + XMLTV or HDHomeRun-compatible tuner endpoints. HDHomeRun mode gets you Jellyfin's tuner-based Live TV UI; M3U mode is simpler to set up.

## Movies and series are not Jellyfin libraries

M3Undle can ingest and publish IPTV movies and series through its Xtream-compatible API, but that does **not** make them appear in Jellyfin's native **Movies** or **Shows** libraries. The Live TV integrations below are channel and guide integrations; they do not map an IPTV VOD catalog into Jellyfin's library model.

An optional third-party Jellyfin Xtream plugin may be able to consume M3Undle's Xtream-compatible API, but M3Undle does not currently claim or test that plugin path as native Jellyfin VOD/series support. Plugin compatibility depends on the particular plugin and version.

Native Jellyfin library integration would require a future STRM export feature. M3Undle does not currently create movie `.strm` files, show/season/episode directory trees, or synchronize those files into a volume shared with Jellyfin.

## M3U + XMLTV mode

1. **Dashboard → Live TV → Add M3U Tuner**
   ```
   http://<host>:8080/m3u/m3undle.m3u
   ```
2. **Dashboard → Live TV → Add XMLTV**
   ```
   http://<host>:8080/xmltv/m3undle.xml
   ```
3. Save and let Jellyfin refresh its guide data.

## HDHomeRun mode

1. **Dashboard → Live TV → Add Tuner Device → HD Homerun**
2. Enter the **Discover JSON** base address shown on M3Undle's **HDHomeRun** page — on the default install (see [Install with Docker](../getting-started/install-with-docker.md)) this is `http://<host-ip>:5004`.
3. Jellyfin fetches `discover.json` and `lineup.json` and shows your channels.

!!! warning "Use manual entry, not auto-discovery"
    Jellyfin's "Detect My Devices" auto-discovery may not find M3Undle in Docker bridge or NAT-like setups. Use the manual **Discover JSON** URL displayed by M3Undle instead of guessing it.

!!! note "If port 5004 isn't reachable"
    M3Undle serves the same HDHomeRun endpoints on every port it's actually listening on — if your deployment only publishes port `8080` (some reverse-proxy or custom-compose setups do), the HDHomeRun endpoints work there too. Whatever the actual URL is, use exactly what's shown on M3Undle's **HDHomeRun** page rather than assuming a port.

!!! note "Validation scope"
    Jellyfin menu wording can vary by Jellyfin version. Validation of M3Undle's Live TV endpoints does not imply validation of a third-party Xtream plugin or native Movies/Shows library integration.

## If endpoint security is enabled

If you've turned on endpoint credentials in **Settings → Security → Endpoint Credentials**, Jellyfin needs the username/password included — check how your Jellyfin version accepts credentials for M3U/XMLTV/tuner URLs (typically as part of the URL or a separate auth field, depending on version).

## Troubleshooting

- No channels appear: confirm you've actually published a lineup — see [Create the First Lineup/Profile](../getting-started/create-first-lineup.md).
- Movies or shows do not appear in Jellyfin libraries: this is expected; M3Undle currently supports Jellyfin Live TV, not native VOD/series library export.
- Tuner not found via auto-discovery: use manual entry as described above; see [LAN and Reverse-Proxy Problems](../troubleshooting/lan-and-reverse-proxy-problems.md) if you need discovery working across the LAN, not just from the Docker host.
- Guide data missing or misaligned: see [EPG](../concepts/epg.md).
