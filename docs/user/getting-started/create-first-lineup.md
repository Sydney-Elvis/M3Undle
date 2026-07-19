# Create the First Lineup/Profile

Adding a provider auto-created a profile for you with the same name — you don't need to create one manually to get started. A **profile** is the named, published lineup your clients actually connect to. See [Concepts > Profiles and Users](../concepts/profiles-and-users.md) for how profiles, providers, and published output relate.

This walks through the minimum steps to get a working lineup published. For the full set of lineup-shaping options (event tracking, custom groups, per-channel overrides), see [Build a Lineup](../guides/build-a-lineup.md).

## 1. Open Channel Mapping

Go to **Channel Mapping** and select the profile you want to build. The page summarizes groups as **mapped**, **unmapped**, or **excluded** and has filters for new and removed groups — see [Concepts > Channels and Groups](../concepts/channels-and-groups.md) for what these states mean.

Provider groups are initially unmapped. Use the group-row actions to map channels from groups you want, or exclude groups you do not want. The icon-only actions have tooltips; hover over an icon before using it if its purpose is unclear.

## 2. Map channels

Select **Map Channels** (or **View Channels** followed by **Map Channels**) to choose channels for the profile. **View Channels** shows the current published lineup with channel number, name, output group, and EPG ID.

The running `v1.0.0-beta.6` UI does not present this workflow as the **Include**, **Manual review**, and **Auto-update** choices described in earlier drafts. Use the mapped/unmapped/excluded status and Map Channels screen as the source of truth for this version.

## 3. Build Output

Once you've made your selections, click **Build Output**. Changes to channel settings are pending until the next build — nothing publishes automatically as you check boxes.

## 4. Confirm it published

Return to the dashboard. Under **Selected Profile**, confirm **Status** is **Serving** and check the **Published** timestamp — see [Read the Dashboard](../guides/dashboard-overview.md) for what everything on that page means. You can also open **Profiles**, select the profile, and review **Published Output** and **Published History** — see [Manage Profiles](../guides/manage-profiles.md).

Your lineup is now live at:

- `http://<host>:8080/m3u/m3undle.m3u`
- `http://<host>:8080/xmltv/m3undle.xml`

## Next step

**[Connect the First Client →](connect-first-client.md)**
