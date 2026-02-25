# Lineup Rules (User-Facing Model)

> **Scope: Beta** — These rules describe lineup shaping behavior planned for the Beta release. Alpha publishes a single pass-through lineup at `/m3u/m3undle.m3u` with no shaping applied.

---

## Lineup = Published Channel Set

Core publishes a single lineup at a well-known endpoint:
- /m3u/m3undle.m3u
- /xmltv/m3undle.xml

## Dynamic Groups (Auto-update)
Each lineup shows a list of groups with a checkbox:
- Checked = this group is included in the lineup
- Unchecked = excluded

When a group is checked:
- channels in that provider group are automatically added/removed as they appear/disappear upstream
- this reduces user babysitting for volatile sports/PPV content

This is called "Dynamic Group" (or "Auto-update group") in the UI.

## New Channels Inbox
Newly discovered channels are NOT automatically added to lineups by default.
They appear in a "New Channels" list so the user can review.

Exception:
- if a channel belongs to an enabled Dynamic Group in a lineup, it is auto-added to that lineup.

