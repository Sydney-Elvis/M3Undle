# Architecture Map

## Purpose
A single unified process (`M3Undle.Web`) provides:
- Web UI (configuration + status) — Blazor Server
- REST API for UI communication (`/api/v1/*`)
- HTTP compatibility endpoints for Media Players:
  - M3U — `/m3u/m3undle.m3u` (output name locked in Core)
  - XMLTV — `/xmltv/m3undle.xml`
  - Stream proxy — `/stream/<streamKey>`
- Background refresh service that builds snapshots and serves last-known-good

## Core Concepts
- Provider: upstream source of channels. Multiple providers can be configured and browsed; one is active at a time.
- Canonical Channel: stable identity representing a channel concept, independent of provider churn. Forms the basis for lineup shaping in a future release. **Not used in V1 snapshot builds.**
- Profile: scopes a set of providers, snapshots, and stream keys to a named output. Currently a single default profile. Multiple profiles with named output endpoints are a future feature.
- StreamKey: stable token used in published `/stream/<streamKey>` URLs. **In V1**, derived from stable channel properties (tvg-id when present, otherwise `displayName + "\u001f" + streamUrl`), SHA-256 hashed with profileId, truncated to 16 base64url chars. Keys are stable across refreshes as long as the channel identity is stable.
- Snapshot: atomic published output for a profile (M3U + XMLTV + channel index JSON). Staged then promoted to active.

## Key Alpha (Minimal Proof-of-Concept) Requirements
- Single active provider: only one provider drives the published output at a time.
- Output name locked: Core publishes to `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml`. Named per-profile endpoints are a future feature.
- Last-known-good snapshots: refresh failures do not break clients. The last active snapshot continues to be served.
- Stream proxy required: published playlists reference `/stream/<streamKey>` — clients never see raw provider URLs.
- Pass-through: no group filtering, no channel numbering, no lineup shaping. All provider channels are published as-is.
- In-memory snapshot build: `SnapshotBuilder` builds the channel index directly from `ParsedProviderChannel` (in-memory M3U parse result). It does NOT write to `provider_channels` or `provider_groups`. This is a deliberate performance decision — see docs/dev/DESIGN_DECISIONS.md.
- Profile auto-creation: importing a provider automatically creates a profile with the same name, making the provider immediately functional without manual steps.

## Alpha Client Contract
- Playlist includes `url-tvg` pointing at this service's `/xmltv/m3undle.xml` endpoint.
- All stream URLs in the playlist point to this service's `/stream/<streamKey>` endpoint.
- Clients do not consume raw provider URLs.
- The output endpoint is always `/m3u/m3undle.m3u` — clients should be pointed here.

Note: Alpha publishes all provider channels as-is. Future releases will add group filtering, channel numbering, and lineup shaping.

