# Providers

A **provider** is an upstream source you configure — a URL playlist, a local file, or an Xtream Codes account. Providers can be large and noisy; M3Undle's job is to make them manageable.

## Providers are separate from what gets published

Adding a provider doesn't publish its channels by itself. Provider groups initially appear as **unmapped**, grouped the way the provider organized them. Nothing appears in your published lineup until a [profile](profiles-and-users.md) is linked to the provider, channels are mapped, and output is built — see [Build a Lineup](../guides/build-a-lineup.md).

## Multiple providers, one lineup

You can configure and browse multiple providers at once. A profile can link to more than one provider (each with a priority), letting you build one combined output lineup from several sources. The shared `/m3u/m3undle.m3u` and `/xmltv/m3undle.xml` endpoints always serve the currently *active* profile's output.

## What a provider tracks

For each provider, M3Undle tracks:

- Last refresh time and success/failure status
- Channel count seen on the last successful fetch
- Its associated profile and current published status
- An optional per-provider maximum concurrent stream limit
- A relay policy controlling how M3Undle handles that provider's stream: **Auto** (clean relay only for channels classified Unstable), **On** (always clean relay), or **Off** (direct relay only) — see [Retry, Failover, and Cooldowns](retry-failover-cooldowns.md)

Guide-source management (XMLTV) is handled separately per provider — see [EPG](epg.md).

## Your mappings survive provider changes

A channel's identity does not include the provider's host, username, password or display name. For Xtream providers it follows the provider's own stream id. This means that changing a provider's URL or password, or a channel being renamed, keeps the same channel — and everything you attached to it: selections, custom-group memberships, numbers, overrides and EPG mappings.

- **Provider outages.** A fetch that returns no live channels, or less than half of what is currently active, is treated like a failed fetch: nothing is changed and the last known lineup keeps publishing. The dashboard shows a refresh problem and an **incomplete lineup** event explains what was held. If the provider returns the same reduced lineup on three consecutive refreshes, M3Undle accepts it as a real change.
- **Channels the provider stops listing.** They become inactive but are kept. They are purged only after 30 days unseen, and only if nothing depends on them — a mapped, numbered, overridden or custom-group channel is never purged. Groups you have configured are kept even when the provider stops listing them.
- **Large changes are visible.** When a refresh deactivates more than half of a large provider's channels, a warning event is raised. Your mappings are preserved.

To be told about provider problems by Matrix or email, see [Administrator Notifications](../guides/notifications.md). The retention and held-fetch settings are in [Environment Variables](../reference/environment-variables.md).

## Provider types

See [Add the First Provider](../getting-started/add-first-provider.md) for the four Add Provider tabs (**From URL**, **From File**, **Xtream Codes**, and **Import**) and [Manage Providers](../guides/manage-providers.md) for credential security and Xtream encryption-key rotation.
