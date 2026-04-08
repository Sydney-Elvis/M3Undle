# Architecture Map

## Purpose
A single unified process (`M3Undle.Web`) provides:
- Web UI (configuration + status) — Blazor Server
- Internal application services used directly by Blazor components (no loopback HTTP required)
- REST API (`/api/v1/*`) for management/integration clients and external tooling
- HTTP compatibility endpoints for Media Players:
  - M3U — `/m3u/m3undle.m3u` (output name locked in Core)
  - XMLTV — `/xmltv/m3undle.xml`
  - Stream proxy — `/stream/<streamKey>`
- Background refresh service that builds snapshots and serves last-known-good

## Core Concepts
- Provider: upstream source of channels. Multiple providers can be configured and browsed; the active profile determines which linked provider data is used for published output.
- Canonical Channel: stable identity representing a channel concept, independent of provider churn. Forms the basis for lineup shaping in a future release. **Not used in V1 snapshot builds.**
- Profile: scopes a set of providers, published versions, and stream keys to a named output (display name + output name). User-facing profile management via `/profiles` and profile detail pages. Multiple named profiles with distinct output endpoints are a future feature.
- StreamKey: stable token used in published `/stream/<streamKey>` URLs. **In V1**, derived from stable channel properties (tvg-id when present, otherwise `displayName + "\u001f" + streamUrl`), SHA-256 hashed with profileId, truncated to 16 base64url chars. Keys are stable across refreshes as long as the channel identity is stable.
- Snapshot: atomic published output for a profile (M3U + XMLTV + channel index JSON). Staged then promoted to active.

## Key Alpha Requirements

These constraints held for Alpha 1 (pass-through) and continue to apply in current releases unless noted:

- Single active profile: one profile drives the shared published output at a time.
- Output name locked: Core publishes to `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml`. Named per-profile endpoints are a future feature.
- Last-known-good snapshots: refresh failures do not break clients. The last active snapshot continues to be served.
- Stream proxy required: published playlists reference `/stream/<streamKey>` — clients never see raw provider URLs.
- Pass-through (Alpha 1 only): no group filtering, no channel numbering, no lineup shaping. As of Alpha 2 these features are implemented.
- In-memory snapshot build: `SnapshotBuilder` builds the channel index directly from `ParsedProviderChannel` (in-memory M3U parse result). It does NOT write to `provider_channels` or `provider_groups`. This is a deliberate performance decision.
- Profile auto-creation: importing a provider automatically creates a profile with the same name, making the provider immediately functional without manual steps.

## Alpha Client Contract
- Playlist includes `url-tvg` pointing at this service's `/xmltv/m3undle.xml` endpoint.
- All stream URLs in the playlist point to this service's `/stream/<streamKey>` endpoint.
- Clients do not consume raw provider URLs.
- The output endpoint is always `/m3u/m3undle.m3u` — clients should be pointed here.

Note: Alpha 1 published all provider channels as-is (pass-through). Alpha 2 added group filtering and channel numbering. Alpha 5 added channel reorder, custom tvg-id, HLS compatibility, CORS, dashboard redesign, and profile UX. New-channel inbox and dynamic groups remain planned.

