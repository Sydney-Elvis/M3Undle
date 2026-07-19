# Build a Lineup

A lineup is the set of channels published by a profile. Build it from **Channel Mapping**, then use **View Channels** to inspect the published result.

## Choose a profile

Open **Channel Mapping** and select a **Profile**. The page shows a starting channel number, **View Channels**, and **Build Output**.

The group summary separates the provider catalog into:

- **mapped** groups with channels currently used
- **unmapped** groups that still need a decision
- **excluded** groups
- **new** and **removed** groups detected since an earlier provider refresh

Use the group-name filter or the cross-group channel search to narrow a large provider catalog. The **All**, **None**, and **Exclude** controls change which group states are shown.

## Work with provider groups

Each row shows the group name, provider, optional **Start #**, and mapped/total channel count. Hover over an icon-only action to see its purpose.

The observed actions included:

- **Rename group**
- **Exclude all member groups**
- **Manage member groups** for a combined group
- **Group settings**
- expand or collapse mapped-channel details

A combined group displays how many provider groups it contains. For example, the configured **Locals** group combined six provider groups.

## Set group behavior

Open **Group settings** for the group. On the validated profile, the drawer showed:

- **Mode: Manual selection** — you choose which channels appear, and changes require your action.
- **New channel handling: Queue for review** — newly discovered channels wait for approval.
- **Track new channels** — controls whether new arrivals are tracked for review.

These settings can differ by group. Read the explanation displayed below each choice before changing it.

## Map and number channels

Use **Map Channels** from the profile page, or open **View Channels** and then select **Map Channels**. Choose the channels that should enter the lineup. A group's **Start #** provides the starting number for its output range.

The **View Channels** table is the published result, not the full provider catalog. It shows:

- channel number
- logo
- channel name
- output group
- EPG ID

The observed lineup contained six channels numbered 100 through 105 in the **Locals** output group. **Manage Numbers** is available from this page for numbering work.

### Number Manager mode

Selecting **Manage Numbers** replaces the channel grid with a full-channel editable list:

- Each row shows the current channel number (editable), channel name, and group
- **▲ ▼** buttons swap a channel with its neighbor, transferring their numbers
- Editing a number field directly updates the value and re-sorts the list
- Changed rows are marked with an indicator
- **Apply All** saves all pending number changes to the database
- Changes take effect after a Build Output; exit the mode with **Exit Number Manager**

## Publish the changes

Return to **Channel Mapping** and select **Build Output**. Mapping edits do not change the published endpoints until an output build completes.

Confirm the result in either place:

- Dashboard: **Selected Profile**, **Status: Serving**, counts, and **Published** timestamp
- **Profiles → profile name**: **Published Output** and **Published History**

The dashboard then exposes the profile's M3U and XMLTV URLs. See [Ports and Endpoints](../reference/ports-and-endpoints.md).

## Features not exercised during this walkthrough

The existing profile already had a completed lineup. The browser walkthrough inspected its group controls, manual-selection settings, mapped channels, and published history, but did not save new selections or run **Build Output**. Destructive group actions and numbering changes were also left untouched.
