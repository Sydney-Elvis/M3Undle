# Channel Does Not Tune

This is different from [Client Cannot Connect](client-cannot-connect.md) — here, the client reaches M3Undle and lists channels, but a specific channel fails to play.

## 1. Confirm the channel is actually live upstream

Check the provider's status in **Provider** — last refresh success/failure, and whether the source channel is still active. A channel that vanished from the provider's own catalog will fail to tune no matter what M3Undle does.

## 2. Check the provider's stream limit

If the provider has a maximum concurrent stream limit configured and it's currently exhausted, new *unique* channel sessions are rejected — but joining a channel someone else is already watching always succeeds regardless of the cap, since it shares the existing upstream connection. Check **Streams** in the web UI for current active sessions and provider limits. See [Stream Proxying](../concepts/stream-proxying.md).

## 3. Check the channel's stream health

M3Undle classifies channels as **Stable**, **Cautious**, or **Unstable** based on observed disconnects and recoveries. A channel that's been flaky will show its health history and current relay policy decision in the stream monitor. See [Retry, Failover, and Cooldowns](../concepts/retry-failover-cooldowns.md) for what these states mean and how relay policy (Auto/On/Off) affects them.

## 4. Confirm the channel wasn't dropped by a lineup change

If the channel used to work and stopped:

- Check whether its provider group is still **Included** — an excluded group's channels are deactivated, not just hidden.
- Check whether the channel itself was individually excluded during review.
- Confirm you ran **Build Output** after making any changes — pending channel-setting changes don't take effect until the next build.

## 5. HDHomeRun-specific: tuner exhaustion

For HDHomeRun-style clients, tuning is capped by the configured tuner count. If every tuner slot is in use by a different `VirtualTunerId`, a new tune request is rejected as busy — but re-tuning from the *same* virtual tuner replaces its own prior session rather than consuming another slot. See [HDHomeRun Compatibility](../concepts/hdhomerun-compatibility.md).

## Still not working

Check the container logs around the time you tried to tune — reconnect attempts, provider auth failures, and stall detection all log there. Include the channel name, provider, and client type when reporting an issue.
