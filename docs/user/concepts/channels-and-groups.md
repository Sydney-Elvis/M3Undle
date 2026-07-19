# Channels and Groups

Your provider publishes a catalog of channels organized into **groups** ("USA NEWS", "UK SPORTS", "Locals", and so on) — often far more than you want. A real provider catalog can hold hundreds of groups and tens of thousands of channels. Your **lineup** is the slice of that catalog you choose to publish to your own players, renumbered and organized the way you want.

Channel mapping is the second stop after adding a provider: the provider gives you the raw catalog, and **Channel Mapping** is where you decide what to keep. Nothing you pick reaches your players until you select **Build Output** — mapping decisions are drafts until then.

## Group states

Every provider group is in one of three states, summarized in the chips at the top of the Channel Mapping page:

- **Mapped** — the group has at least one channel selected for the lineup. The chip also shows the total channels in use (for example, "1 mapped · 6 ch used").
- **Unmapped** — no decision yet: no channels selected, group not excluded. Fresh providers start with everything unmapped.
- **Excluded** — you've ruled the group out. Excluded groups stay out of your way but can be un-excluded at any time.

Each chip is also a view filter — click it to show or hide groups in that state.

M3Undle also tracks catalog *changes* between provider refreshes:

- **New** — a group the provider added since the last sync. New groups carry a badge on their row; a dismiss control clears all new flags at once.
- **Removed** — a mapped group that disappeared from the provider feed. Its row shows a **missing** badge so you notice that channels you were publishing may be gone.
- **event?** — M3Undle's hint that a group looks like it contains time-limited channels (pay-per-view, individual sports fixtures). For these, the badge suggests reviewing **Group settings → New channel handling** (see below), because event groups churn constantly.

## Group settings: how each group behaves over time

Every group (including custom and combined groups) has its own settings, opened from the sliders icon on its row. This is where you control what happens as the provider's catalog changes underneath you.

**Mode** — who decides what's in the group:

- **Manual selection** — you pick which channels appear in the output; only checked channels are included. Changes require your action.
- **Auto-update** — all channels in the group are always included, and new channels flow through automatically on refresh. Good for stable groups you want wholesale.

**New channel handling** — what happens when the provider adds channels to this group:

- **Queue for review** — new channels wait in the [Review Queue](#the-four-channel-pages) for your approval before appearing in output.
- **Notify only** — you're notified when new channels appear; nothing is added automatically.
- **Auto-add all** — every new channel is added to the output automatically on refresh.
- **Auto-add (guide data only)** — new channels are added automatically only if they come with EPG guide data; channels with no guide info are skipped.
- **Auto-add matching** — only new channels matching your **Match terms** are added automatically. Enter teams, leagues, or fighters separated by commas, `|`, or new lines. With no terms entered, nothing auto-adds — the field warns you.

**Track new channels** — when on, new channels the provider adds to this group are flagged during refresh so you can review them. It does not affect channels you've already selected.

These settings can differ per group — a locals group on Manual selection with Queue for review, a PPV group on Auto-add matching with your teams as keywords, and a trusted movie group on Auto-update can all coexist in one profile.

## Custom and combined groups

You aren't limited to the provider's grouping:

- **Custom groups** — create your own group from the Custom Groups section and move channels into it.
- **Rename or merge** — renaming a provider group to an output name that already exists merges them into one **combined group**. The validated instance's "Locals" group combined six provider groups into one published group of local stations.
- Combined groups get extra row actions: **Manage member groups** (see and manage what's inside), **Move to different group** and **Remove from this group** for individual members, and **Exclude all member groups** in one click.

Your players only ever see the output group name — the provider's original grouping is your private organizing tool.

## Channel numbers

Numbering follows a precedence order (highest wins):

1. **A pinned number** — set on an individual channel (through the Channels page edit dialog or the Number Manager). Pins are stored permanently and never move on refresh.
2. **The group's Start #** — each group row has an optional starting number; channels in the group are numbered sequentially from there, automatically skipping any number already pinned anywhere else.
3. **The page's "Start at #" value** — the default starting point Channel Mapping uses when suggesting numbers for groups without their own Start #. This is a per-profile convenience remembered by your browser, not a published setting.

If a group's configured range fills up, remaining channels overflow into numbers starting at 9000. Auto-assigned (non-pinned) numbers can shift when the provider reorders or adds channels — pin a channel's number if it must never move.

## The four channel pages

Channel work is spread across four pages, all under Channels in the navigation:

| Page | What it's for |
|---|---|
| **Mapping** (Channel Mapping) | Decide: map, exclude, group, and number the provider catalog. |
| **Channels** (View Channels) | Inspect: the published lineup as your players see it — numbers, logos, names, output groups, EPG IDs — with per-channel edits and the Number Manager. |
| **Review Queue** | Approve: pending channels from groups set to Queue for review, with bulk include/exclude and an **Event card view** that folds multiple quality tiers of the same PPV/sports event into one card. |
| **What's On** | Discover: upcoming events matched by your Auto-add tracking keywords across all groups. |

On the **Channels** page, each row can be edited (pencil icon): override the channel number (empty = auto-numbering), override the output group, or — after unlocking it — set a custom EPG ID in place of the provider's value. Rows can also be removed from the output entirely.

## Nothing publishes until you build

Mapping edits, group settings, numbering changes, and per-channel overrides all accumulate as pending work. The published lineup only changes when a **Build Output** completes:

- Expanded mapped channels on the Mapping page are marked **Live — in current output** or **Pending rebuild**.
- The Channels page shows a **Rebuild needed** chip when the published lineup is behind your edits.
- Per-channel edits note that they apply to the next rebuilt snapshot.

This is deliberate: you can reorganize freely, then publish one coherent change.

## Where to go next

- [Build a Lineup](../guides/build-a-lineup.md) — the step-by-step walkthrough of the Mapping page.
- [Create the First Lineup/Profile](../getting-started/create-first-lineup.md) — the minimum path for a first-time setup.
- [EPG](epg.md) — how guide data attaches to the channels you've mapped.

## Verification boundary

Group states and badges (including **missing** and **event?**), the Group Settings drawer with its exact Mode and New channel handling option labels, custom/combined group behavior ("Locals" with six member groups), the Channels page columns and toolbar, the Review Queue controls, and the What's On empty state were all observed on a live v1.0.0-beta.6 instance with a real 117-group provider catalog. The numbering precedence, overflow-at-9000 behavior, refresh stability of pinned vs. auto-assigned numbers, and the browser-local nature of "Start at #" were verified against source code. No selections, group settings, numbers, or builds were changed during validation.
