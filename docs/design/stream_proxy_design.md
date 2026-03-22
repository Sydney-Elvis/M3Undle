# M3Undle Stream Buffering and Shared-Connection Proxy --- Implementation Design Brief

## Purpose

Design and implement a modern IPTV stream delivery layer for M3Undle
that supports:

-   HTTP-based stream delivery
-   shared upstream connections
-   lightweight buffering
-   multiple downstream subscribers on a single upstream stream
-   reconnect logic for unstable providers
-   future extensibility for provider failover, access control, and
    optional FFmpeg-based repair/transcode

This document describes **what to build**, **why it should be built this
way**, and **what to avoid**.

------------------------------------------------------------------------

# 1. Primary Goal

When multiple clients request the same channel at the same time, M3Undle
should:

-   open **one** upstream provider connection for that channel
-   keep a **small in-memory rolling buffer**
-   fan out that stream to multiple downstream clients
-   close the upstream connection when the last downstream client
    disconnects
-   recover cleanly when the upstream stream drops

The system should be implemented using a **native HTTP streaming
proxy**, not an FFmpeg‑first architecture.

Phase 1 scope clarification:

-   shared-session streaming applies to live/tune routes
-   VOD/series routes remain direct relay in phase 1

------------------------------------------------------------------------

# 2. Core Design Principles

## 2.1 Prefer native HTTP proxying over FFmpeg

Provider streams should flow through the system as:

Provider → M3Undle Proxy → Clients

FFmpeg must **not** be the default streaming mechanism.

## 2.2 Shared upstream connections

Each channel stream session represents:

-   one channel
-   one upstream connection
-   many downstream subscribers

If multiple users watch the same channel, they attach to the same
session.

## 2.3 Lightweight buffering

Initial buffering should be:

-   in-memory
-   ring-buffer based
-   short duration

Suggested target window: **3--15 seconds**.

This is **not DVR storage**.

## 2.4 Separation of responsibilities

The streaming subsystem must be independent from:

-   playlist parsing
-   provider authentication
-   XMLTV mapping
-   endpoint authentication
-   HDHomeRun emulation
-   profile management

## 2.5 Reconnect logic

The system must automatically:

-   detect upstream disconnects
-   retry connections
-   restore sessions when possible
-   terminate sessions if recovery fails

## 2.6 Optional FFmpeg

FFmpeg may be used later for:

-   stream repair
-   remuxing
-   transcoding

But the system must work **without it**.

------------------------------------------------------------------------

# 3. Non‑Goals

Do not implement the following in the first version:

-   DVR recording pipelines
-   long-term timeshifting
-   mandatory transcoding
-   complex provider load balancing
-   adaptive bitrate streaming
-   distributed cluster streaming
-   large disk‑backed buffers

The focus is **live proxy streaming**.

------------------------------------------------------------------------

# 4. High Level Architecture

Client Request → Router → ChannelSessionManager → Stream Session

If a session already exists, attach the client to it.

Otherwise create a new session.

------------------------------------------------------------------------

# 5. Core Components

## ChannelSessionManager

Responsibilities:

-   track active channel sessions
-   create sessions when needed
-   reuse existing sessions
-   remove idle sessions
-   ensure thread safety

## ChannelStreamSession

Represents a single live stream session.

Responsibilities:

-   manage upstream connection
-   own the ring buffer
-   track subscribers
-   manage reconnect logic
-   track session state

Possible states:

-   Initializing
-   Connecting
-   Live
-   Reconnecting
-   Draining
-   Closed
-   Faulted

## UpstreamStreamConnector

Responsible for provider communication.

Responsibilities:

-   open provider HTTP connection
-   apply headers/authentication
-   stream bytes continuously
-   detect failures

## RingBuffer / LiveBuffer

Stores recent stream data.

Requirements:

-   bounded size
-   concurrent read/write support
-   oldest data overwritten automatically

## SubscriberConnection

Represents one downstream client.

Responsibilities:

-   stream buffered + live data
-   detect disconnects
-   avoid blocking other subscribers

## ReconnectPolicy

Defines retry logic:

-   retry delays
-   retry limits
-   fatal error detection

## StreamHealthTracker

Tracks:

-   stream bitrate
-   reconnect counts
-   session durations

------------------------------------------------------------------------

# 6. Channel Identity

Sessions must be keyed by a stable identifier such as:

channel-id + provider-route + stream-variant

For phase 1, optimize sharing against provider stream limits:

-   key by effective upstream source identity
-   do **not** include profile identity when source identity is equal

Mandatory key fields:

-   `ProviderId`
-   `ProviderChannelId`

Do NOT include `StreamUrl` in the key.  Provider URLs frequently embed
rotating auth tokens.  `StreamUrl` is re-fetched from the database on
each reconnect attempt.

Do not rely on channel display names.

------------------------------------------------------------------------

# 7. Subscriber Fan‑Out

Requirements:

Late join behavior:

-   new subscribers receive buffered data
-   then transition to live stream

Slow subscribers:

-   must not block the entire session
-   slow connections should be disconnected if necessary

------------------------------------------------------------------------

# 8. Ring Buffer Guidelines

-   small rolling buffer
-   bounded memory
-   optimized for live streams
-   supports multiple readers

The buffer is not designed for full replay.

------------------------------------------------------------------------

# 9. Upstream Connection Behavior

Provider streams may include:

-   MPEG‑TS over HTTP
-   other long‑running byte streams

The connector must:

-   detect stalled streams
-   detect timeouts
-   restart connections when necessary

------------------------------------------------------------------------

# 10. Reconnect Logic

The system must distinguish between:

-   startup failures
-   mid‑stream interruptions
-   subscriber disconnects
-   provider authentication errors

Reconnect strategy:

-   quick initial retries
-   exponential backoff
-   configurable retry limits

Fatal errors should terminate the session immediately.

Additional reconnect constraints:

-   include explicit stalled-read detection (do not rely on infinite
    body timeout)
-   keep existing subscribers attached during bounded reconnect windows
-   reset buffer generation after reconnect to avoid mixing old/new
    bytes

Phase 1 reconnect scope:

-   stall detection + immediate retry + simple fixed-step backoff
-   session ends if outage window is exceeded
-   full `ReconnectPolicy` classification moves to Phase 2

Authorization during shared sessions:

-   authorization is checked once per request at subscriber attach time
-   active subscribers are not re-validated mid-stream
-   profile access changes affect only new requests

------------------------------------------------------------------------

# 11. Session Lifecycle

Session creation:

first subscriber triggers session start.

Active session:

continues while subscribers exist.

Idle grace period:

after last subscriber leaves, keep session alive briefly.

Teardown:

stop upstream connection and free resources.

------------------------------------------------------------------------

# 12. Logging and Metrics

Log key lifecycle events:

-   session start/stop
-   reconnect attempts
-   provider failures
-   subscriber count changes

Metrics to expose later:

-   active sessions
-   active viewers
-   reconnect counts
-   stream durations

Operational visibility must also expose:

-   active client stream detail (who is streaming what)
-   provider/upstream stream detail (state, reconnect counts, last byte)
-   per-session buffer usage (used, max, remaining)
-   shared-stream indicator (`isShared`, subscriber count)

------------------------------------------------------------------------

# 13. Endpoint Integration

The stream proxy must work with:

-   M3U streaming endpoints
-   HDHomeRun emulation
-   authenticated streaming endpoints

Phase 1 route behavior:

-   shared-session path: `/live`, `/stream`, `/tune`, `/hdhr/tune`
-   direct relay path: `/movie`, `/vod`, `/series`

HDHomeRun contract: path-param shape `/hdhr/tune/{streamKey}` unchanged.
No query-style tune variant.

Range header handling: silently ignore Range headers on live shared
routes.  Return `200 OK` with full stream.  Do not return `416` —
HDHomeRun and some players send `Range: bytes=0-` habitually.

Upstream response header passthrough allowlist:

-   `Content-Type` — required
-   `Cache-Control` — forward if present
-   all other headers blocked (especially `Content-Length`, `Set-Cookie`,
    `Authorization`, `Transfer-Encoding`, `X-*`)

Streaming logic should remain independent from endpoint logic.

------------------------------------------------------------------------

# 14. Future Features Compatibility

The design must support future additions:

-   multiple providers per channel
-   automatic provider failover
-   profile‑based access
-   multiple virtual tuners

------------------------------------------------------------------------

# 15. FFmpeg Integration (Future)

FFmpeg may be added as an optional upstream adapter:

Provider → FFmpeg → Stream Session

But the default path remains:

Provider → Native Proxy → Stream Session

------------------------------------------------------------------------

# 16. Implementation Mistakes to Avoid

Do not:

-   open a provider connection per client
-   require FFmpeg for normal operation
-   allow slow clients to block others
-   use unbounded buffers
-   mix streaming with playlist logic

------------------------------------------------------------------------

# 17. Implementation Phases

Phase 1 --- Core functionality

-   ChannelSessionManager
-   ChannelStreamSession
-   Ring buffer
-   Subscriber fan‑out
-   Session lifecycle
-   stall detection and minimal reconnect
-   basic slow-subscriber eviction (queue-full disconnect)
-   base observability endpoints for sessions/clients/providers
-   unit and integration tests (in-process fake upstream stream server)

Phase 2 --- Stability

-   reconnect logic
-   slow client handling
-   logging/metrics

Phase 3 --- Extensibility

-   provider failover
-   optional FFmpeg adapter
-   advanced monitoring

------------------------------------------------------------------------

# 18. Success Criteria

The system is successful when:

1.  Multiple clients can watch the same channel.
2.  Only one upstream connection is used.
3.  A ring buffer smooths late joins.
4.  Upstream disconnects trigger reconnect logic.
5.  Slow clients do not affect others.
6.  Sessions close after the last viewer leaves.
7.  FFmpeg is optional.
8.  Operators can see active client streams, provider stream health, and
    buffer utilization.

Failure status code contract:

-   provider auth/config error at startup → `502 Bad Gateway`
-   reconnect outage window exhausted → `503 Service Unavailable`
-   no active snapshot → `503 Service Unavailable`
-   session or provider cap reached (new unique sessions only) → `503`
    with `Retry-After: 30`
-   joins to an existing shared session are always permitted regardless
    of caps

------------------------------------------------------------------------

# 19. Configuration Model

Use layered configuration for robustness and operational control:

1.  system defaults in `appsettings.json` and
    `appsettings.{Environment}.json`
2.  environment variable overrides for deployment automation
3.  settings-page runtime overrides for approved controls only

Not every internal tuning value should be exposed in UI.

Recommended adjustable controls include:

-   streaming enable/disable
-   max concurrent sessions
-   per-session buffer limit
-   idle grace window
-   stall timeout and reconnect outage window
-   status retention window
-   provider max concurrent upstreams (global and optional per-provider)

Client IP visibility in stream status is full-value for admin operators.
