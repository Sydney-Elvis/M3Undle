# Jellyfin

Jellyfin supports M3Undle in two modes. Either works; HDHomeRun mode gets you Jellyfin's tuner-based Live TV UI, M3U mode is simpler to set up.

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
2. Enter `http://<host-ip>:5004` manually.
3. Jellyfin fetches `discover.json` and `lineup.json` and shows your channels.

!!! warning "Use manual entry, not auto-discovery"
    Jellyfin's "Detect My Devices" auto-discovery may not find M3Undle in Docker bridge or NAT-like setups — some of Jellyfin's autodetect flows connect to the responder IP on port `80` instead of the advertised base URL on port `5004`. Manual entry with `http://<host-ip>:5004` works reliably regardless of networking mode, and is the supported path for Jellyfin specifically.

## If endpoint security is enabled

If you've turned on endpoint credentials in **Settings → Endpoint Security**, Jellyfin needs the username/password included — check how your Jellyfin version accepts credentials for M3U/XMLTV/tuner URLs (typically as part of the URL or a separate auth field, depending on version).

## Troubleshooting

- No channels appear: confirm you've actually published a lineup — see [Create the First Lineup/Profile](../getting-started/create-first-lineup.md).
- Tuner not found via auto-discovery: use manual entry as described above; see [LAN and Reverse-Proxy Problems](../troubleshooting/lan-and-reverse-proxy-problems.md) if you need discovery working across the LAN, not just from the Docker host.
- Guide data missing or misaligned: see [EPG](../concepts/epg.md).
