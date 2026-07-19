# Read the Dashboard

The **Overview** page (the app's home page) is where you check that everything is working without digging into individual screens. This page explains what each part means.

## Needs Attention

When something needs your input, a banner appears at the top of the dashboard before anything else. It can combine several unrelated issues at once:

- **Provider subscription expiring/expired** — a linked provider's playlist subscription is running out. Renew it and update the provider on the **Providers** page.
- **New groups need review** — links straight to **Channel Mapping**.
- **New channels need review** — links straight to **Review Queue**.
- **Last refresh failed** — M3Undle is still serving the last known-good lineup; nothing is broken for viewers yet, but fix the provider/source issue and refresh again.

No banner means nothing needs you right now — it isn't a full health check, just outstanding action items.

## Selected Profile

This card is about one profile at a time — pick which one with the chips at the top (**Show all profiles** reveals every profile, not just the active one; a profile tagged **MAIN** is the one serving the default M3U/XMLTV endpoints).

For the selected profile you'll see, top to bottom:

- **Status** — a chip summarizing whether the profile is serving normally, degraded, or has no output yet. While a refresh is running, this becomes a live progress indicator with a **Cancel** button instead, and a series-sync line appears underneath if VOD series episodes are still being expanded in the background (episodes appear as they sync; live channels aren't affected).
- **Published Content** — Live / Movies / Series chip counts. Click any of them to jump to the **Channels** page.
- **Provider** — the provider(s) backing this profile, their max-stream limit if known, and an expiry chip if the subscription is running out.
- **Last Refresh** — the outcome (`ok` / `running` / error), when it finished, and how many channels were seen. A refresh error shows its summary here, with a note if M3Undle fell back to serving the last known-good version.
- **Next Refresh** — when the next automatic refresh is scheduled, or **Manual only** if scheduling is off for this profile.
- **Published** — when this profile's output was last built, with a chip describing the size of the change from the previous build.

Only the active (MAIN) profile shows Last Refresh/Next Refresh/Published in full; other profiles show just their own published timestamp, since refresh scheduling is a property of whichever profile is currently active.

## Endpoints

The URLs your media players actually connect to, for the profile selected above:

- **M3U Playlist** and **XMLTV Guide** — the standard endpoints, with a copy button. Non-active profiles get a `?profile=` query parameter appended automatically.
- **Xtream** — shown only if Xtream compatibility is enabled; an **Unsecured** chip appears if endpoint credentials aren't enforced, meaning any username/password is accepted. Click through to **Settings → Security** to configure it.
- **HDHomeRun** — shown only if HDHomeRun emulation is enabled: friendly name, device ID, and whether SSDP discovery is on. Click through to the [HDHomeRun](../concepts/hdhomerun-compatibility.md) page for the full endpoint list.

Each URL row can offer alternate copies (Docker-internal, external/reverse-proxy) when those base URLs are configured — see [Ports and Endpoints](../reference/ports-and-endpoints.md).

## Always-visible chrome

A few things appear on every page, not just the dashboard:

- **System Events** (top bar) — a bell-style summary of active system-level warnings, separate from the per-profile Needs Attention banner.
- **Footer status bar** — live counts that update continuously: `Streams x/y max`, `Clients n`, pending groups/channels shortcuts when nonzero, total live channel count, and an overall health indicator (Healthy / degraded / etc.). A **Status delayed** chip can appear if the counts themselves are lagging. The version number on the left opens the **About** panel — see [Client Cannot Connect](../troubleshooting/client-cannot-connect.md#still-stuck) for why that's useful when reporting an issue.

## Triggering a refresh

The **Refresh Lineup** button in the left navigation (not on the dashboard itself) manually triggers the same refresh the dashboard tracks — useful right after changing provider settings or channel mappings instead of waiting for the next scheduled run. Its progress and result appear back on this page's Status line. Refresh scheduling itself (interval, manual-only, startup catch-up) is configured in **Settings → Schedule** or per-profile — see [Profiles and Users](../concepts/profiles-and-users.md#refresh-scheduling).
