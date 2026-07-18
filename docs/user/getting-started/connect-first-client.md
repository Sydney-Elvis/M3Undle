# Connect the First Client

This walks through connecting Jellyfin, the most commonly used first client. Other clients follow the same two basic patterns (M3U/XMLTV or HDHomeRun-style) — see [Clients](../clients/jellyfin.md) for a fuller list.

## Option A: M3U + XMLTV (works with most clients)

1. In Jellyfin: **Dashboard → Live TV → Add M3U Tuner** and enter:
   ```
   http://<host>:8080/m3u/m3undle.m3u
   ```
2. Add the guide data: **Dashboard → Live TV → Add XMLTV** and enter:
   ```
   http://<host>:8080/xmltv/m3undle.xml
   ```
3. Save, then let Jellyfin refresh its guide.

This same pattern (M3U + XMLTV URL) works for NextPVR, IPTVnator, IPTV Smarters, and most other players — see [Clients](../clients/jellyfin.md) for client-specific notes.

## Option B: HDHomeRun-style tuner

M3Undle emulates a network HDHomeRun tuner so DVR applications can consume your lineup as if it were a hardware tuner.

1. In Jellyfin: **Dashboard → Live TV → Add Tuner Device → HD Homerun** and enter:
   ```
   http://<host>:5004
   ```
2. Jellyfin fetches the discovery info and channel lineup automatically.

Manual entry like this is the recommended path — some clients' auto-discovery doesn't reliably find M3Undle in Docker bridge/NAT networking. See [Clients > HDHomeRun-Compatible Clients](../clients/hdhomerun-compatible-clients.md) if you want auto-discovery working too.

## If nothing shows up

Confirm you've actually published a lineup first — see [Create the First Lineup/Profile](create-first-lineup.md). An empty M3U endpoint means no channels have been included yet, not a connection problem. See [Troubleshooting > Client Cannot Connect](../troubleshooting/client-cannot-connect.md) if the endpoint itself isn't reachable.

## Next step

You now have a working end-to-end setup. From here:

- [Core Concepts](../concepts/providers.md) explains the ideas behind providers, profiles, EPG, and stream proxying in more depth
- [Guides](../guides/build-a-lineup.md) covers shaping the lineup further — custom groups, event tracking, EPG mapping
