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

## Exclusion and Processing Cost

Excluded live groups are filtered earlier than final snapshot composition:

- live-channel sync skips channels from excluded groups
- previously synced live channels from excluded groups are deactivated
- later snapshot build stages read only active live channels

Pending groups are still catalogued so they remain reviewable in the mapping UI.
