# EPG

M3Undle manages guide data (XMLTV) separately from the channel lineup itself, then aligns the two at publish time.

## EPG sources are per-provider

Open **EPG** to manage guide data per provider. The page has **Sources** and **Channel Mappings** tabs. Sources may include the provider's own XMLTV, a remote URL, or a local file. The source table shows kind, URL/path, priority, and status.

- Set a priority order (which source wins when more than one covers the same channel)
The **Channel Mappings** tab shows each published channel's `tvg-id`, EPG source, matched EPG channel, and mapping mode. Use **Edit** on a row to override a mapping.

## How the published guide gets built

The published `/xmltv/m3undle.xml` output is compiled from all *enabled* sources, merged into one guide feed, de-duplicated by channel ID, and aligned with whatever's currently in the active lineup. If a provider or Xtream panel offers its own XMLTV, you can include that instead of or alongside your other configured sources.

## Why guide IDs matter

Client apps match guide data to channels using the XMLTV channel ID (`tvg-id`). If a provider's IDs change or do not match your published channels, guide data can stop lining up correctly. Use **EPG → Channel Mappings → Edit** to review or override a specific mapping.

## Where this fits in your lineup

EPG mapping is independent of whether a channel is present in the published lineup—a published channel can be playable with no matching guide entry; it just will not show programme data in clients that expect it. See [Map EPG Data](../guides/map-epg-data.md) for the step-by-step workflow.
