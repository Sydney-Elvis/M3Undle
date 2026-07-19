# Build a Lineup

A lineup is the set of channels published by a profile. Build it from **Channel Mapping**, then use **View Channels** to inspect the published result. For what groups, states, and numbering *mean*, see [Concepts > Channels and Groups](../concepts/channels-and-groups.md); this page is the how-to.

## Choose a profile

Open **Channel Mapping** and select a **Profile**. The page shows a starting channel number (**Start at #**), **View Channels**, and **Build Output**.

The chips in the group summary separate the provider catalog into:

- **mapped** groups with channels currently used
- **unmapped** groups that still need a decision
- **excluded** groups
- **new** and **removed** groups detected since an earlier provider refresh

Each chip doubles as a view filter — click it to show or hide groups in that state. Two controls are easy to confuse with the chips but do something different: **All**, **None**, and **Exclude** next to the group count are *bulk actions* on the currently visible groups — select all their channels, deselect all their channels, or exclude every visible group from output. Filter first, then apply a bulk action deliberately.

## Find things in a large catalog

- The **group-name filter** supports patterns, explained in its own help tooltip: `CA` matches a whole word, `CA*` starts-with, `*CA*` contains, `^CA` string start, `!*USA*` negates, and a space separates OR terms.
- The **cross-group channel search** finds channels by name across every group at once.
- Inside an expanded group, a channel filter narrows the list, a **Show selected channels only** toggle hides the noise, and a filter can be saved as a named **preset** for reuse.

## Work with provider groups

Each row shows the group name, provider, optional **Start #**, and mapped/total channel count. Hover over an icon-only action to see its purpose.

Each row's icon-only actions include:

- **Rename group** — renaming to an existing output name merges the groups into a combined group
- **Exclude group** / **Remove exclusion**
- **Exclude all member groups** and **Manage member groups** for a combined group
- **Group settings** (the sliders icon)
- expand or collapse mapped-channel details

A combined group displays how many provider groups it contains — for example, a **Locals** group might merge six regional provider groups into one output group. You can also create your own group from scratch with **Create custom group**.

Watch for row badges: **new** (added since last sync — click to dismiss, or dismiss all from the chip row), **missing** (a mapped group no longer in the provider feed), and **event?** (the group looks like time-limited PPV/sports content — worth a look at its Group settings).

## Set group behavior

Open **Group settings** for the group. The drawer offers:

- **Mode** — **Manual selection** (you choose which channels appear; changes require your action) or **Auto-update** (all channels in the group are always included and new ones flow through on refresh).
- **New channel handling** — **Queue for review**, **Notify only**, **Auto-add all**, **Auto-add (guide data only)**, or **Auto-add matching** with a **Match terms** field for teams/leagues/fighters keywords.
- **Track new channels** — whether new arrivals in this group are flagged during refresh.

These settings can differ by group. Read the explanation displayed below each choice before changing it — [Concepts > Channels and Groups](../concepts/channels-and-groups.md#group-settings-how-each-group-behaves-over-time) explains each policy in full.

Channels queued for review land on the **Review Queue** page (Channels → Review Queue in the navigation), which supports bulk include/exclude, a **Notify only** filter, and an **Event card view** that folds multiple quality tiers of one PPV/sports event into a single card. Events matched by your Auto-add keywords appear on the **What's On** page.

## Map and number channels

Use **Map Channels** from the profile page, or open **View Channels** and then select **Map Channels**. Choose the channels that should enter the lineup. A group's **Start #** provides the starting number for its output range.

The **View Channels** table is the published result, not the full provider catalog. It shows:

- channel number
- logo
- channel name
- output group
- EPG ID

For example, a **Locals** output group might be numbered 100 through 105 to keep local channels together. **Manage Numbers** is available from this page for numbering work, and a **Rebuild needed** chip appears when the published lineup is behind your latest edits.

Each row also has per-channel actions:

- **Edit channel** — override the channel number (leave empty for auto-numbering), override the output group, or unlock and set a **Custom EPG ID** in place of the provider's value. Edits apply to the next rebuilt snapshot.
- **Remove from output** — drop the channel from the published lineup.

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
