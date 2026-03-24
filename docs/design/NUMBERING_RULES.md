# Numbering Rules (tvg-chno)

---

## Current Implementation (Alpha 5)

Channel numbering is per-group with per-channel pin support and global conflict avoidance.

### Precedence Order (highest wins)
1. **Pinned channel number** — explicit `channel_number` set on a `profile_group_channel_filter` row (via the edit dialog or Number Manager)
2. **Group auto-numbering** — `auto_num_start` / `auto_num_end` on the group filter; assigns sequential numbers to channels that have no pin, skipping any numbers already taken globally
3. **Overflow** — if a group has a range configured but the range is exhausted, remaining channels are placed starting at 9000
4. **No number** — channel appears in the output with no `tvg-chno` (only when no range is configured and no pin is set)

### Storage
- Pinned numbers live in `profile_group_channel_filters.channel_number` (nullable integer).
- Auto-numbering bounds live in `profile_group_filters.auto_num_start` / `auto_num_end`.
- Group evaluation order lives in `profile_group_filters.sort_override` (nullable integer).
- All rules are applied at snapshot build time by `SnapshotBuilder.BuildChannelIndex`.

### Conflict Avoidance
Before any auto-assignment begins, all pinned numbers across **all groups** are collected into a global set. Auto-numbering then skips any number in that set, regardless of which group the pin belongs to.

Example: if News starts at 100 and Weather has a channel pinned at 105:
- News fills 100–104
- News skips 105 (occupied by Weather pin)
- News continues at 106+ for remaining channels
- Weather's pin stays at 105

### Overflow
When a group has a configured range (`auto_num_start` / `auto_num_end`) but that range is fully exhausted (all slots filled by auto-assignment or pinned), remaining channels in that group are placed in an overflow block starting at 9000. The overflow cursor also skips any numbers already taken globally.

Overflow constant: `SnapshotBuilder.OverflowRangeStart = 9000`.

Channels in groups with **no range configured** receive no number rather than overflowing — overflow only applies to groups that opted into numbering.

### Group Evaluation Order
Groups are evaluated in `sort_override` order (ascending, nulls last), then alphabetically by output name. The group with the lowest `sort_override` allocates its numbers first, which matters when ranges overlap.

`sort_override` is set via the `PATCH /api/v1/profiles/{profileId}/group-filters/{filterId}` endpoint.

> **Not yet implemented:** drag/drop UI for group reordering. `sort_override` must currently be set via the API.

### Number Manager
The Channels page provides a **Number Manager** inline mode:
- Displays all live channels sorted by current channel number
- Numbers can be edited directly or swapped using ▲ ▼ buttons
- Swapping transfers the two channels' numbers; re-sort is immediate
- **Apply All** writes all pending changes to `profile_group_channel_filters`
- Changes take effect at the next Build Output

### Snapshot Output Sort Order
Within each output group:
1. Channels with an explicit number — sorted by number ascending
2. Channels with auto-assigned numbers — placed after, in auto-number order
3. Channels with no number — sorted by display name then stream URL

### Stability Across Refreshes
- **Pinned channels** are stable: their numbers are stored in the DB and never change due to a refresh.
- **Auto-assigned channels** are recomputed from scratch each snapshot build. If the provider reorders channels or new channels appear, auto-assigned numbers may shift. To lock a channel's number across refreshes, pin it explicitly via the Number Manager.

---

## Future Work

- Drag/drop UI for group sort order (SortOverride)
- Overflow UI indicator explaining why a channel landed at 9000+
- Configurable overflow range start (currently hardcoded at 9000)
- Stable auto-assignment across refreshes without requiring manual pinning
