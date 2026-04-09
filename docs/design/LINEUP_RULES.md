# Lineup Rules (Current User Model)

This document describes the lineup-shaping behavior currently implemented in the web UI.

## Group Review State

New provider groups start in `pending`.

- `pending`: visible for review, but not yet publishable
- `include`: approved for output
- `exclude`: suppressed from output and skipped earlier in live-channel sync

`pending` is the inbox state for new upstream groups. It prevents noisy groups from appearing in the published lineup before the user has looked at them.

## Group Intent

The group-level include control is still important because it means:

- include the whole provider group without checking channels individually
- allow the group to keep publishing new arrivals when it is in `auto-update` mode

That is different from manual curation, where the user only wants a subset of channels from the group.

## Group Mode

Each approved group has one of two modes:

- `select` (`manual review`): only explicitly included channels publish
- `all` (`auto-update`): live channels in the group publish automatically unless explicitly excluded or held back by event-tracking policy

## Manual Selection Behavior

In `manual review` mode, checking channels creates explicit included channel rows for only those selections.

If the group was still `pending`, checking a channel also promotes the group to `include`. This avoids a broken two-step flow where the UI shows selected channels but the snapshot still drops them because the parent group never left `pending`.

## Pending Channel Queue

For included `manual review` groups, newly discovered live channels can enter the pending review queue depending on the group's tracking policy.

- `review`: create pending queue rows
- `notify`: do not auto-publish, but also do not create queue rows
- `auto_add_*`: publish automatically according to the specific policy

## Event Tracking Policies

Volatile event groups (PPV feeds, sports event grids, rotating game slots) use an event-aware tracking policy that extends the standard group mode. The tracking policy is per-profile-group-filter and applies on top of the group mode:

### `review` (default)
- Newly arrived event channels enter the pending channel queue as normal.
- Placeholder channels (blank/empty slots) are suppressed — they never create queue rows.
- Explicit include/exclude decisions are required before an event publishes.

### `notify`
- Events do not queue pending rows and do not auto-publish.
- Useful when you want visibility that an event exists without committing it to the lineup.

### `auto_add_all`
- All populated event channels from the group are automatically published.
- Placeholder channels are still suppressed and never publish.
- Useful for weekly sports grids where all events are wanted automatically.

### `auto_add_populated`
- Publishes event channels that have a non-empty `EventContentKey` (i.e. the slot is filled with a real event name).
- A stricter form of `auto_add_all` that filters out partially-identifiable slots.

### `auto_add_matching`
- Publishes only event channels that match configured keywords or structured interest rules.
- Free-text keywords (comma/newline/pipe-separated) are matched case-insensitively against the display name, group title, and event content key.
- Structured interest rules (team / league / sport / fighter / promotion / series) take precedence over free-text keywords. A `suppress` rule blocks auto-add; an `auto_add` rule enables it.

## Structured Event Interest Rules

Per-profile interest rules provide typed recurring-interest matching beyond free-text keywords:

- `match_type`: `keyword` | `team` | `league` | `sport` | `fighter` | `promotion` | `series`
- `match_value`: the value to match (case-insensitive substring)
- `action`: `auto_add` | `notify` | `suppress`
- `priority`: evaluation order (lower = earlier)
- Optional scope: `provider_id` / `provider_group_id` to restrict to a specific group

Rules are evaluated in priority order. The first matching rule's action wins. If no rule matches, free-text keyword matching applies as fallback.

## Placeholder Suppression

Channels classified as placeholders are suppressed at every level:

- Never create pending queue rows
- Never publish under any tracking policy (including `auto_add_all`)
- Never count toward pending review totals

Placeholder detection is heuristic — display names that end in `:` or `|`, match patterns like `Event N:`, `PPV EVENT N:`, `Game N:`, or have blank content after the slot prefix are classified as placeholders.

## Richer Event Metadata

Event channels populate structured metadata during provider sync:

- `EventTitle`: normalized event name (e.g. "Eagles vs Giants")
- `EventSport`: detected sport (e.g. "football", "mma", "racing")
- `EventLeague`: detected league or promotion (e.g. "NFL", "UFC", "Formula 1")
- `EventParticipantsJson`: JSON array of participant names when "X vs Y" is detected

This metadata is available in the review queue UI and used by structured interest rule matching.

## Exclusion and Processing Cost

Excluded live groups are filtered earlier than final snapshot composition:

- live-channel sync skips channels from excluded groups
- previously synced live channels from excluded groups are deactivated
- later snapshot build stages read only active live channels

Pending groups are still catalogued so they remain reviewable in the mapping UI.
