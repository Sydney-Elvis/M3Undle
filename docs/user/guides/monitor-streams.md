# Monitor Live Streams

The **Streams** page shows what's playing right now: active upstream connections to your providers, and every client currently watching. It auto-refreshes every 3 seconds. This is the built-in live view — for historical metrics you can graph over time, see [Monitor with Prometheus / Grafana](monitor-with-prometheus-grafana.md) instead.

## Active Streams

One row per upstream connection M3Undle currently has open with a provider — remember that multiple viewers on the same channel share one row, one upstream connection (see [Stream Proxying](../concepts/stream-proxying.md)).

| Column | What it shows |
|---|---|
| **Channel** | The channel name; hover for the internal session ID. |
| **State** | The session's current lifecycle state, with a tooltip explaining it. |
| **Relay** | **Direct** (bytes passed through unchanged) or **Clean remux** (FFmpeg repackaging). A warning icon appears if relay fell back to direct — click the details icon for why. See [Retry, Failover, and Cooldowns](../concepts/retry-failover-cooldowns.md). |
| **Health** | The channel's Stable / Cautious / Unstable grade, or a dash if the upstream connection hasn't been established yet. |
| **Clients** | Total downstream viewers. If any are watching via generated HLS, the count splits into direct connections vs. HLS-tracked (inferred from segment activity, since HLS doesn't hold an open connection the way direct delivery does). |
| **Bitrate** | Current upstream bitrate from the provider. |
| **Buffer** | Ring-buffer fill — how much of the recent-history buffer is in use. Full is normal for a healthy active stream, not a warning sign. |
| **Reconnects** | Upstream reconnect attempts for this session, with the failure kind if nonzero. |
| **Running** | How long the session has been open. |
| **Last Data** | How long since the last byte arrived from the provider; turns amber past 10 seconds. |

The rightmost column has two actions: an **ⓘ** icon opening the full health/relay details dialog for that session, and — only for generated HLS sessions — a **restart** icon that tears down and disconnects the current FFmpeg session (all its viewers get disconnected; the next request starts a fresh one). Use this if a browser-playback stream gets stuck.

## Connected Clients

One row per individual downstream connection, across every active stream:

| Column | What it shows |
|---|---|
| **Client** | The player type, resolved from its User-Agent (e.g. "VLC", "IPTV Smarters"). |
| **Channel** | Which channel this client is watching, or the raw requested route if it can't be matched to a channel. |
| **IP** | The client's remote address. |
| **Source** | How the client reached M3Undle (e.g. direct, reverse proxy), if determinable. |
| **Delivery** | The transport this client is receiving — direct MPEG-TS relay or generated HLS. |
| **Status** | A computed health chip for this specific client's connection. |
| **Transfer** | Current throughput and total bytes sent. For direct clients, a client receiving noticeably less than the stream's upstream rate with a growing backlog is flagged as possibly slow or stalled. HLS clients instead show time since their last segment request. |
| **Backlog** | Chunks queued waiting to be written to this client (direct delivery only). A growing backlog can mean a slow or stalled client. |
| **Connected** | How long this client has been connected. |

## Recently Ended

Sessions that closed recently collapse into an expandable **Recently Ended** panel below the two live tables — final state, relay mode, reconnect count, last recovery attempt (and how long output was held during it), and when the session started. Useful for checking what happened to a stream right after a viewer reports it cut out.

## Nothing here

If no one is watching anything, both tables simply say so — this is a live view, not a history, so it's normal to see "No active streams" and "No connected clients" between sessions.
