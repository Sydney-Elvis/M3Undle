# Monitor System Resources

The **System Resources** page (`/system/resources`, linked from the footer's CPU indicator) shows what M3Undle can observe about the CPU, memory, storage, and streaming capacity of the environment it's running in — useful when playback is stuttering or the app feels sluggish and you want to know whether it's a resource constraint before digging into provider or client-specific troubleshooting.

Measurements refresh every 5 seconds and each card keeps a short rolling graph for the time the page has been open (history isn't persisted — it resets if you leave and come back).

## What the measurements indicate

At the top of the page, M3Undle summarizes what it can conclude from the current readings — for example that the Docker CPU limit is throttling it, that a cgroup memory limit was recently hit, or that no constraint is currently visible. This is a best-effort diagnosis, not a guarantee: M3Undle can tell when its own tasks are stalled or its own cgroup limit is being hit, but it cannot identify a competing process elsewhere on the host without separate host-level monitoring.

## CPU

Container CPU usage relative to the CPUs available to the runtime, plus the Docker CPU quota (if one is configured), how often M3Undle has recently been throttled against that quota, and CPU pressure — the share of recent time M3Undle's tasks spent waiting for CPU rather than running.

## Memory

Memory used against the Docker memory limit (if one is configured), swap usage, memory pressure, and cumulative counts of OOM kills and memory-limit hits since the container started. Any nonzero OOM kill count is worth investigating even if current usage looks fine, since it reflects something that already happened rather than current state.

## Streaming activity

Connected streaming clients, total and per-client output bitrate, and the number of active HLS relay sessions — a quick way to correlate a resource spike with how much M3Undle is actually being asked to do at that moment.

## Storage

Free space on the filesystems backing M3Undle's logs and generated-HLS directories. A volume shows a **Critically low space** indicator once it drops below roughly 5% free or 1 GiB free, whichever is more conservative — the same threshold that drives the low-disk chip in the footer (see [Read the Dashboard](dashboard-overview.md#always-visible-chrome)).

## Advanced Linux signals

On Linux hosts with cgroup v2, an additional card exposes lower-level signals not needed for everyday triage: the M3Undle process's own CPU/memory (excluding child processes and FFmpeg), host/VM load averages, virtual CPU steal (meaningful only on virtualized hosts), I/O pressure, and cumulative throttling counters since the container started.

On non-Linux hosts, or without cgroup v2, container-level facts are unavailable — the page falls back to process-level CPU and a runtime-provided memory estimate instead.
