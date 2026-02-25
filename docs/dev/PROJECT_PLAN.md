# M3Undle Plan

## Product Goal
A self-hosted IPTV lineup manager that:
- connects to an IPTV provider
- publishes client-friendly endpoints (M3U, XMLTV, stream proxy)
- prevents DVR churn by keeping stable channel identity and stable numbering
- gives users clear control over what gets published

Published as:
- /m3u/m3undle.m3u
- /xmltv/m3undle.xml

## Release Milestones

See [ROADMAP.md](ROADMAP.md) for goals and scope per milestone.
See [DEV_CHECKLIST.md](DEV_CHECKLIST.md) for detailed task tracking.

| Milestone | Focus |
|-----------|-------|
| Alpha 1   | Functional pass-through — provider config, snapshot serving, stream relay |
| Alpha 2   | Filtering & mapping — group/channel rules, channel rename/reorder |
| Alpha 3   | Buffering & DVR integration — FFmpeg/VLC, HDHR emulation |
| Alpha 4   | Pro plugin hooks & endpoint security |
| Beta      | Feature finalization, testing, documentation |

## Design Documents
- [docs/design/ARCHITECTURE_MAP.md](../design/ARCHITECTURE_MAP.md)
- [docs/design/DB_SCHEMA.md](../design/DB_SCHEMA.md)
- [docs/design/HTTP_COMPATIBILITY.md](../design/HTTP_COMPATIBILITY.md)
- [docs/design/LINEUP_RULES.md](../design/LINEUP_RULES.md)
- [docs/design/NUMBERING_RULES.md](../design/NUMBERING_RULES.md)
- [docs/dev/ROADMAP.md](ROADMAP.md)
- [docs/dev/DEV_CHECKLIST.md](DEV_CHECKLIST.md)
- [docs/dev/DESIGN_DECISIONS.md](DESIGN_DECISIONS.md)

