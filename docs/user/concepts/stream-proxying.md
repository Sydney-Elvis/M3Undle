# Stream Proxying

Every published stream URL points at M3Undle, never at the raw provider. This isn't just convenience — it's a security guarantee: provider stream URLs typically embed credentials directly (`http://provider/{username}/{password}/stream.ts`), so a client that received the raw provider URL would receive those credentials too.

## Relay, not redirect

M3Undle *relays* the stream — it opens the upstream connection itself and pipes the bytes through — rather than redirecting the client to the provider. A redirect would hand the client the real provider URL (and its embedded credentials) on every request. M3Undle never does this.

## Shared upstream connections

When multiple clients request the same channel at the same time, M3Undle opens **one** upstream provider connection and fans it out to every subscriber, instead of opening a separate provider connection per viewer. This matters directly for your provider stream limit: five people watching the same channel counts as one upstream connection, not five.

A small in-memory buffer smooths over late joiners and brief upstream hiccups without needing a large disk-backed cache.

## Handling unstable providers

Some providers are noisier than others — brief stalls, connection drops, occasional bad data. M3Undle watches each channel and grades its recent health as **Stable**, **Cautious**, or **Unstable**, then automatically adapts how it handles that channel: unstable channels get a more careful reconnect strategy and can be routed through an FFmpeg clean-up step, all without you touching anything. The full explanation — including how to see a channel's current health on the **Streams** page and how to override the automatic behavior per provider — is in [Retry, Failover, and Cooldowns](retry-failover-cooldowns.md).

## Browser and HDHomeRun compatibility

For live MPEG-TS delivery, M3Undle can send null-packet keepalives during short upstream gaps to keep stall-sensitive external players (like browsers) connected — but HDHomeRun-only sessions are left as plain upstream MPEG-TS, since DVR transcode pipelines expect unmodified content. Browser playback that needs HLS gets a generated HLS session created on demand, fed from the same shared internal relay other viewers of that channel are already using — it doesn't open a second upstream connection.

## Where you configure this

Session limits, buffer size, and reconnect behavior are all configurable in **Settings → Streaming**, under the **Stream Proxy** section — see [Guides > Browser Playback](../guides/browser-playback.md) for a walkthrough of those controls, and [Reference > Environment Variables](../reference/environment-variables.md) for the advanced env/config-only controls. The per-provider stream format and relay policy options are covered in [Guides > Manage Providers](../guides/manage-providers.md).

For how the shared session, subscriber fanout, and reconnect logic are actually built, see [Architecture > Stream Pipeline](../architecture/stream-pipeline.md).
