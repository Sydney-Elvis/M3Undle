# Numbering Rules (tvg-chno) — Locked Behavior

> **Scope: Beta** — These rules describe channel numbering behavior planned for the Beta release. Alpha passes through channel numbers as-is from the provider.

---

## Authoritative numbering is per-lineup
Each lineup controls its own channel numbers (tvg-chno).
Canonical channels do not own global channel numbers.

## Numbering UI (simple model)
In a lineup, each enabled group has:
- a checkbox (Dynamic Group)
- a Start Number (e.g., NFL starts at 700)

Channels may also have a pinned number (explicit number set by user).

## Precedence Order (highest wins)
1) Pinned channel number (explicit)
2) Group auto-numbering from Start Number
3) Unassigned channels (placed after numbered content)
4) Overflow (when placement cannot be resolved)

## Allocation Rules
- Group numbering uses "Start Number only" (no hard cap).
- Allocation picks the next available number upward.
- Allocation skips numbers already taken (especially pinned numbers).
- Existing pinned numbers never move.
- The system MUST NOT renumber existing channels due to refresh; only newly added channels are placed.

## Conflict Example (locked expectation)
If:
- News group starts at 100
- Weather has pinned channel at 105

Then:
- News fills 100–104
- Weather stays at 105
- News continues at 106+ for remaining News channels

## Overflow Rule
If a channel cannot be placed without excessive collision scanning, it is placed into an Overflow block:
- Overflow channels appear at end of lineup
- They are labeled as Overflow in UI
- UI displays a message explaining why overflow happened

Overflow placement uses a dedicated overflow range (example: starting at 9000; implementation may make this configurable).

## Group Ordering
Group evaluation order is determined by UI order (drag/drop).
Default ordering when a lineup is created is by Start Number ascending.
