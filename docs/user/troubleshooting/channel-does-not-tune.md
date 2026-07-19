# Channel Does Not Tune

This is different from [Client Cannot Connect](client-cannot-connect.md) — here, the client reaches M3Undle and lists channels, but a specific channel fails to play.

## 1. Confirm the channel is actually live upstream

Check the provider's status in **Providers**—the table shows last refresh, expiry, and published status. Use **Preview** to inspect provider content without changing the published lineup. A channel that vanished from the provider's own catalog will fail to tune no matter what M3Undle does.

## 2. Check the provider's stream limit

If the provider has a maximum concurrent stream limit configured and it is exhausted, new stream requests may be rejected. Open **Streams** to see **Active Streams** and **Connected Clients**; the footer also shows the current stream count and maximum. See [Stream Proxying](../concepts/stream-proxying.md).

## 3. Check the channel's stream health

Open **Streams** while reproducing the problem. The page refreshes every three seconds and lists active streams and connected clients, including each stream's **Health** (Stable / Cautious / Unstable) and **Relay** columns. See [Retry, Failover, and Cooldowns](../concepts/retry-failover-cooldowns.md) for what drives those grades.

## 4. Confirm the channel wasn't dropped by a lineup change

If the channel used to work and stopped:

- In **Channel Mapping**, check whether its provider group is mapped, unmapped, or excluded.
- Select **View Channels** to confirm the channel is still present in the published lineup.
- Confirm you ran **Build Output** after making any changes — pending channel-setting changes don't take effect until the next build.

## 5. HDHomeRun-specific: tuner exhaustion

For HDHomeRun-style clients, tuning is capped by the configured tuner count. If every tuner slot is in use by a different `VirtualTunerId`, a new tune request is rejected as busy — but re-tuning from the *same* virtual tuner replaces its own prior session rather than consuming another slot. See [HDHomeRun Compatibility](../concepts/hdhomerun-compatibility.md).

## Still not working

Check the container logs around the time you tried to tune — reconnect attempts, provider auth failures, and stall detection all log there. Include the channel name, provider, and client type when reporting an issue.
