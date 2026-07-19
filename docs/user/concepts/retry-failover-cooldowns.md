# Retry, Failover, and Cooldowns

Live IPTV sources misbehave: streams stall, connections drop, and some channels send corrupted data. M3Undle's job is to ride out as much of that as possible so your player never notices — and when a channel keeps misbehaving, to adapt automatically. This page explains what happens behind the scenes and where you can watch it or tune it.

One honest note up front: "failover" here means failing over to a *fresh connection or a cleaner relay path for the same channel*. M3Undle does not automatically switch a channel to a different provider — each profile publishes channels from its own provider.

## What happens when a stream stalls

1. **Detection.** If no data arrives from the provider for the **Stall Detection** period (default 30 seconds — shorter internal checks catch content gaps sooner), M3Undle treats the stream as stalled.
2. **Retry.** M3Undle reconnects to the provider, retrying with gradually longer pauses, for up to the **Reconnect Window** (default 75 seconds). Your player stays connected the whole time — for stall-sensitive players like browsers, M3Undle sends harmless filler packets so the player doesn't give up during the gap.
3. **Clean resume.** When the provider comes back, M3Undle doesn't just splice the new data in anywhere: it looks for a safe re-entry point (the start of a fresh video frame) so playback resumes without a garbled picture. Only if it can't find one within its search limit does it fall back to a less precise resume point.
4. **Give up gracefully.** If the provider doesn't recover within the Reconnect Window, the stream ends and viewers must tune in again.

The three timing knobs are in **Settings → Streaming → Stream Proxy → Advanced Options → Reconnect Behaviour** — see [Browser Playback](../guides/browser-playback.md#related-stream-proxy-settings) for the full settings walkthrough.

## Stream health: Stable, Cautious, Unstable

M3Undle keeps a short memory (roughly the last 24 hours) of how each channel has behaved: disconnects, recoveries, recoveries that had to use the less-precise fallback, corrupted data, and tunes that had to be forced to restart. From that history each channel gets a health grade:

- **Stable** — no meaningful trouble recently. This is the normal state.
- **Cautious** — some recent trouble (for example, a couple of disconnects or recoveries). No behavior change yet; M3Undle is watching.
- **Unstable** — repeated or serious trouble. M3Undle changes how it handles the channel (see below).

Health also heals: roughly half an hour of trouble-free viewing steps a channel's grade back down (Unstable → Cautious → Stable). These windows are built in and not configurable.

You can see the current grade in the **Health** column of the **Streams** page while a channel is playing, with a tooltip explaining the classification. The **Relay** column next to it shows how the stream is being relayed, and a details icon opens the full health and relay evidence for that session.

## What changes for an unstable channel

Two things, both automatic:

- **More careful recovery.** After a reconnect, M3Undle waits longer and searches further to find a genuinely clean resume point, and it refuses the less-precise fallback entirely — a channel that has already shown glitchy behavior gets no shortcuts.
- **Clean remux relay.** With the provider's relay policy on **Auto** (the default), an unstable channel's stream is routed through FFmpeg for a *clean remux*: the stream is repackaged into well-formed MPEG-TS without re-encoding the picture. This costs a little CPU and adds no visible quality change, but smooths over the kind of container-level corruption that makes players stutter or desync. Stable channels stay on **direct relay** — bytes passed through untouched.

The clean remux uses the same FFmpeg installation as [Browser Playback](../guides/browser-playback.md). If FFmpeg isn't available, the stream falls back to direct relay, and the Streams page shows the fallback reason.

## Overriding the automatic behavior

The relay decision is per provider: **Providers → edit (pencil icon) → Advanced Options → Relay policy**:

- **Auto** (default) — direct for stable channels, clean remux for unstable ones, as described above.
- **On** — force clean remux for every channel on this provider. Useful if a provider's streams are consistently messy.
- **Off** — force direct relay for every channel, disabling the adaptive clean-up.

See [Manage Providers](../guides/manage-providers.md#advanced-options-timeout-and-stream-format) for the surrounding fields, including **Force MPEG-TS**.

## Cooldowns

When a channel fails badly — the reconnect window runs out, or the provider rejects the connection outright — M3Undle doesn't immediately hammer the provider with fresh attempts: the channel is put on a short per-channel cooldown first, and tune requests during it are answered with a "try again shortly" response. How long depends on the kind of failure: typically 15–60 seconds (rate-limit errors sit at the longer end), capped at about 5 minutes. If the provider itself says how long to wait (a rate-limit response with a `Retry-After` value), M3Undle honors that instead.

Cooldowns protect you two ways: they avoid burning your provider connection limit on a channel that's down anyway, and they keep a broken channel from degrading service to everything else.

## Where to look when a channel misbehaves

- **Streams** page — live Health and Relay columns per active stream, with a details dialog for the full evidence.
- **[Channel Does Not Tune](../troubleshooting/channel-does-not-tune.md)** — first stop when a channel won't start at all.
- **[Monitor with Prometheus / Grafana](../guides/monitor-with-prometheus-grafana.md)** — stream failure and recovery counters, if you want history beyond the live view.

## Verification boundary

The Reconnect Behaviour settings, their defaults, and the per-provider Relay policy control (including its Auto/On/Off helper text) were verified against a live v1.0.0-beta.6 instance. The Streams-page Health and Relay columns, health grading thresholds, the 24-hour observation and clean-watch healing windows, unstable-channel recovery changes, and cooldown durations were verified against the application source code — no channel on the validated instance was unhealthy (or even playing) during validation, so the adaptive behavior itself was not observed live.
