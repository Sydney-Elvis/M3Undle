# EPG

M3Undle manages guide data (XMLTV) separately from the channel lineup itself, then aligns the two at publish time.

## EPG sources are per-provider

Each provider can have multiple XMLTV guide sources: the provider's own built-in XMLTV, a remote URL, or a local file. For each source you can:

- Set a priority order (which source wins when more than one covers the same channel)
- Run an on-demand test fetch and parse
- Auto-map channels from that guide source to your published channels
- Override individual channel-to-guide mappings by hand

## How the published guide gets built

The published `/xmltv/m3undle.xml` output is compiled from all *enabled* sources, merged into one guide feed, de-duplicated by channel ID, and aligned with whatever's currently in the active lineup. If a provider or Xtream panel offers its own XMLTV, you can include that instead of or alongside your other configured sources.

## Why guide IDs matter

Client apps match guide data to channels using the XMLTV channel ID (`tvg-id`). If a provider's `tvg-id` values are unstable — changing between refreshes, or not matching your published channel at all — guide data can silently stop lining up with the right channel. If that happens for a specific channel, you can set an explicit `tvg-id` override on it: see [Channels](../getting-started/create-first-lineup.md) — it's a lock-gated field (you have to unlock and confirm it) because an incorrect override breaks guide data for that channel, not just fails to fix it.

## Where this fits in your lineup

EPG mapping is independent of group inclusion/exclusion — a channel can be fully published and playable with no matching guide entry; it just won't show programme data in clients that expect it. See [Map EPG Data](../guides/map-epg-data.md) for the step-by-step workflow.
