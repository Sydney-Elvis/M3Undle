# Profiles and Users

A **profile** is a named, published lineup — the thing your clients actually connect to. It scopes a set of linked providers, a set of channel-mapping decisions, and a published version history under one name. For the day-to-day screens (creating, activating, and deleting profiles), see [Manage Profiles](../guides/manage-profiles.md).

## One active profile

Only one profile is active at a time. The active profile is what serves the shared, unqualified endpoints:

- `http://<host>:8080/m3u/m3undle.m3u`
- `http://<host>:8080/xmltv/m3undle.xml`

You can configure multiple profiles, but only one publishes to those shared endpoints at a time — switching the active profile switches what every client connected to those URLs sees. Named per-profile output endpoints (so multiple profiles could publish simultaneously to distinct URLs) are a planned future feature, not available yet.

## What a profile shows you

The profile detail page shows:

- Profile name, creation time, and active/published status
- Linked providers, priority, expiry, and status
- The effective refresh schedule (inherited from the global default, or overridden per profile)
- Last-published time and published history
- Live, movie, and series counts

## Refresh scheduling

By default, a profile inherits the global refresh schedule from Settings. It can override that with its own interval (or manual-only) instead. For the *active* profile specifically, its effective schedule also drives startup catch-up behavior — whether a refresh runs automatically at startup if the last published snapshot is older than the configured interval.

## "Users" here means endpoint credentials, not accounts

M3Undle doesn't have per-viewer user accounts. What's configurable is:

- **UI authentication** — whether logging into the web UI itself requires a password (`M3UNDLE_AUTH_ENABLED`)
- **Endpoint security** — a single username/password that client-facing endpoints (M3U, XMLTV, stream, HDHomeRun) can require

See [Security](security.md) for how these two are configured independently.

## Deleting a profile

Deleting a profile removes all of its associated data — group filters, channel selections, custom groups, canonical channels, stream keys, snapshots, and provider links. It's blocked while a snapshot refresh is in progress for that profile.
