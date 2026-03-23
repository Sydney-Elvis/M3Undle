# Numbering Rules (tvg-chno)

---

## Alpha 5 — Current Implementation

Channel numbering in Alpha 5 is per-group with per-channel pin support.

### Precedence Order (highest wins)
1. **Pinned channel number** — explicit `channel_number` set on a `profile_group_channel_filter` row (via the edit dialog or Number Manager)
2. **Group auto-numbering** — `auto_num_start` / `auto_num_end` on the group filter; assigns sequential numbers to channels that have no pin
3. **No number** — channel appears in the output with no `tvg-chno`

### Storage
- Pinned numbers live in `profile_group_channel_filters.channel_number` (nullable integer).
- Auto-numbering bounds live in `profile_group_filters.auto_num_start` / `auto_num_end`.
- Both are applied at snapshot build time by `SnapshotBuilder`.

### Number Manager (Alpha 5)
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

---

## Beta — Planned Enhancements

> These rules describe numbering improvements planned for Beta. They are not yet implemented.

### Authoritative numbering is per-lineup
Each lineup controls its own channel numbers independently of canonical channels.

### Conflict avoidance
- Allocation skips numbers already taken (especially pinned numbers).
- Existing pinned numbers never move due to a refresh.
- The system MUST NOT renumber existing channels due to refresh; only newly added channels are placed.

### Conflict Example (planned behavior)
If:
- News group starts at 100
- Weather has a pinned channel at 105

Then:
- News fills 100–104
- Weather stays at 105
- News continues at 106+ for remaining News channels

### Overflow Rule
If a channel cannot be placed without excessive collision scanning, it is placed into an Overflow block:
- Overflow channels appear at end of lineup
- UI displays a message explaining why overflow happened
- Overflow range starts at 9000 (configurable)

### Group Ordering
Group evaluation order is determined by UI order (drag/drop).
Default ordering when a lineup is created is by Start Number ascending.
