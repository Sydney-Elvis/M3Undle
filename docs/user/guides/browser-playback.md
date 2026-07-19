# Browser Playback

M3Undle can generate HLS for browser and Electron clients when a live stream is available only as MPEG-TS. Configure this under **Settings → Streaming → Browser Playback**.

## How browser playback works

When a browser (or Electron app) asks for a channel that the provider only offers as MPEG-TS — a format browsers can't play directly — M3Undle uses FFmpeg to convert it to HLS on the fly. FFmpeg must be installed on the M3Undle server; the top of the Browser Playback section tells you whether it was found:

> FFmpeg is available. Browser playback is active.

## Browser Playback settings

The visible controls are:

- **Enable generated HLS** — turns the browser-compatible output on or off.
- **FFmpeg Path** under **Advanced Options** — leave it blank to use `ffmpeg` from the server's `PATH`, or enter an absolute path when FFmpeg is installed elsewhere. The field shows which executable is currently active.

Browser Playback changes take effect after a restart. Select the **Apply** button in the Browser Playback section after making a change.

There are no FFmpeg codec, bitrate, resolution, HLS segment-length, playlist-length, or hardware-acceleration controls — M3Undle manages those itself.

## Related Stream Proxy settings

Browser Playback appears below **Stream Proxy** on the same **Settings → Streaming** page. These controls affect viewer sessions generally, including sessions that use generated HLS:

- **Enable Stream Proxy** — the master switch for relaying streams through M3Undle.
- Under **Session Limits**:
    - **Max Simultaneous Streams** — the maximum concurrent viewers across all channels. When the cap is reached, new tune requests receive a "tuner busy" response. Match this to your provider's connection limit.
    - **Disconnect Grace Period (sec)** — keeps the upstream connection open for this period after the last viewer leaves, so a viewer who reconnects quickly picks up without rebuffering.
- Under **Advanced Options → Buffering**:
    - **Maximum Idle Time (sec)** — hard limit: closes any idle stream beyond this, even if grace periods keep renewing. Default 120 s.
    - **Buffer per Stream (bytes)** and **Total Buffer Limit (bytes)** — bound the memory used to smooth brief provider interruptions. Defaults ≈ 4 MiB per stream, 32 MiB total.
    - **Download Chunk Size (bytes)** — how much is read from the provider at a time. Default ≈ 32 KiB.
- Under **Advanced Options → Reconnect Behaviour**:
    - **Stall Detection (sec)** — silence from the provider before the stream is treated as stalled. Default 30 s.
    - **Reconnect Window (sec)** — how long M3Undle keeps retrying after a stall before giving up. Default 75 s.
    - **Connection Timeout (sec)** — how long to wait for the provider to respond on connect. Default 15 s.

What happens during and after a reconnect — and how M3Undle adapts to channels that stall repeatedly — is explained in [Retry, Failover, and Cooldowns](../concepts/retry-failover-cooldowns.md).

The page distinguishes when settings take effect: new viewer sessions inherit Stream Proxy settings immediately, while overall limits and Browser Playback changes require a restart.

## Sizing storage for generated HLS

Generated HLS writes rolling playlists/segments under `/data/hls-work` by default. These are rolling/sliding, session-scoped, and cleaned up on session end or inactivity — not full VOD retention — but you still need to size the volume that backs `/data`.

Estimate required storage with:

```
required_bytes ≈ concurrent_generated_hls_sessions × average_bitrate_bytes_per_second × retained_seconds
```

or in Mbps terms:

```
required_gb ≈ concurrent_sessions × average_mbps × retained_seconds / 8 / 1024
```

Recommended planning:

- Start with a **2×–4× safety multiplier** over the raw estimate.
- Small/home usage: allocate **2–5 GB**.
- Multi-user usage: allocate **10–20 GB** or more, depending on bitrate and concurrency.

Examples:

- 5 sessions at 8 Mbps, 60 seconds retained: raw ~300 MB → recommended **1–2 GB**.
- 10 sessions at 12 Mbps, 90 seconds retained: raw ~1.35 GB → recommended **3–5 GB**.

If you expect heavy browser playback and want this scratch space on a separate disk, mount it directly at the internal path:

```yaml
volumes:
  - ./config:/config
  - ./data:/data
  - ./hls-work:/data/hls-work
```

## Before changing the settings

1. Confirm the page says FFmpeg is available.
2. Leave **FFmpeg Path** blank unless the executable is not available through `PATH`.
3. Match the general simultaneous-stream limit to the upstream provider's connection limit.
4. Apply changes and restart M3Undle when the page says a restart is required.
5. Reopen **Settings → Streaming** and confirm Browser Playback reports active.

## What wasn't verified

The FFmpeg availability result, generated-HLS switch, FFmpeg Path field, restart notice, and neighboring Stream Proxy controls (including the Session Limits / Buffering / Reconnect Behaviour groupings and their stated defaults) were observed directly on a live instance. No live channel was opened in the browser because that would create a shared upstream session; HLS generation, FFmpeg invocation, generated files, playback cleanup, and behavior during an active or failed session were not verified.
