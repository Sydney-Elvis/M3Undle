# Browser Playback

M3Undle can generate HLS for browser and Electron clients when a live stream is available only as MPEG-TS. Configure this under **Settings → Streaming → Browser Playback**.

## How browser playback works

The settings page states that M3Undle uses FFmpeg to transcode an MPEG-TS-only stream to HLS on demand so a browser can play it natively. FFmpeg must be installed on the M3Undle server.

The validated instance reported:

> FFmpeg is available. Browser playback is active.

## Browser Playback settings

The visible controls are:

- **Enable generated HLS** — enables the generated browser-compatible output.
- **FFmpeg Path** under **Advanced Options** — leave it blank to use `ffmpeg` from the server's `PATH`, or enter an absolute path when FFmpeg is installed elsewhere.

The observed field was blank and the UI reported the active executable as `ffmpeg`. Browser Playback changes take effect after a restart. Select the **Apply** button in the Browser Playback section after making a change.

No FFmpeg codec, bitrate, resolution, HLS segment-length, playlist-length, or hardware-acceleration controls were exposed in this screen.

## Related Stream Proxy limits

Browser Playback appears below **Stream Proxy** on the same Streaming settings page. These controls affect viewer sessions generally, including sessions that may require generated HLS:

- **Max Simultaneous Streams** — the maximum concurrent viewers across all channels. The UI says new tune requests receive a tuner-busy response when the hard cap is reached.
- **Disconnect Grace Period (sec)** — keeps the upstream connection open for this period after the last viewer leaves.
- **Maximum Idle Time (sec)** — closes an idle stream after the configured hard limit.
- **Buffer per Stream (bytes)** and **Total Buffer Limit (bytes)** — bound the memory used to smooth brief provider interruptions.
- **Download Chunk Size (bytes)** — controls the amount read at a time.
- **Stall Detection (sec)**, **Reconnect Window (sec)**, and **Connection Timeout (sec)** — control general upstream timeout and reconnection behavior.

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

The FFmpeg availability result, generated-HLS switch, FFmpeg Path field, restart notice, and neighboring Stream Proxy limits were observed directly. No live channel was opened in the browser because that would create a shared upstream session; HLS generation, FFmpeg invocation, generated files, playback cleanup, and behavior during an active or failed session were not verified.
