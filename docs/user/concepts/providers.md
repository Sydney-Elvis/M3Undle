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

## Provider types

See [Add the First Provider](../getting-started/add-first-provider.md) for the four Add Provider tabs (**From URL**, **From File**, **Xtream Codes**, and **Import**) and [Manage Providers](../guides/manage-providers.md) for credential security and Xtream encryption-key rotation.
