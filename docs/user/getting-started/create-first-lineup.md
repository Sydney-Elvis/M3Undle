# Create the First Lineup/Profile

Adding a provider auto-created a profile for you with the same name — you don't need to create one manually to get started. A **profile** is the named, published lineup your clients actually connect to. See [Concepts > Profiles and Users](../concepts/profiles-and-users.md) for how profiles, providers, and published output relate.

This walks through the minimum steps to get a working lineup published. For the full set of lineup-shaping options (event tracking, custom groups, per-channel overrides), see [Build a Lineup](../guides/build-a-lineup.md).

## 1. Review provider groups

Go to **Channel Mapping**. Every group from your provider starts in **pending** — parked, not yet publishable. For each group you actually want, mark it:

- **Include** — channels from this group appear in your output
- **Exclude** — group is ignored entirely
- Leave anything you haven't decided on yet as **pending**

## 2. Choose a group mode

For each included group, pick how new channels in it get handled:

- **Manual review** — only channels you explicitly check are published
- **Auto-update** — active channels publish automatically unless you exclude them

For a first pass, manual review on a small number of groups is the easiest way to see exactly what you're publishing.

## 3. Select channels (manual review groups)

Within an included manual-review group, check the individual channels you want. You can set a channel number and an output group per channel as you go, or leave numbering to auto-assignment — see [Build a Lineup](../guides/build-a-lineup.md) for how auto-numbering and pinned numbers interact.

## 4. Build Output

Once you've made your selections, click **Build Output**. Changes to channel settings are pending until the next build — nothing publishes automatically as you check boxes.

## 5. Confirm it published

Check the dashboard for the current published version and last refresh status. If a refresh ever fails, M3Undle keeps serving the last known-good version rather than breaking your clients.

Your lineup is now live at:

- `http://<host>:8080/m3u/m3undle.m3u`
- `http://<host>:8080/xmltv/m3undle.xml`

## Next step

**[Connect the First Client →](connect-first-client.md)**
