# HDHomeRun Compatibility

M3Undle presents the active profile as a virtual HDHomeRun device. Compatible clients can discover it on the network or use its discovery URL directly.

## Device identity

Open **HDHomeRun** to see the identity clients receive:

- **Friendly name**
- **Device ID**
- **Model**
- **HDHR tuners**
- **Base URL**

Some clients display the device ID rather than the friendly name. Compare the ID in the client with the **Device ID** on this page. NextPVR may append a tuner index, such as `-0` or `-1`, which the UI identifies as normal behavior.

## Discovery and manual setup

The **Discovery Status** section reports whether HDHR, SSDP, and SiliconDust discovery are enabled. Discovery may work without entering a URL, but Docker or routed networks can prevent broadcast discovery from reaching the client.

For manual setup, use the **Discover JSON** value displayed under **HDHR Endpoints**. The same section supplies copyable values for:

- **Discover JSON**
- **Device XML**
- **Lineup JSON**
- **Lineup Status**

Do not construct these URLs from an assumed port. The documented default install publishes a dedicated port `5004` for this (see [Install with Docker](../getting-started/install-with-docker.md)), but the instance this page was validated against was configured differently — its endpoints were reachable under the main `:8080/hdhr/` path instead, with `5004` not published. Whichever your deployment is, use the values actually shown on its **HDHomeRun** page rather than assuming either port.

## Configure emulation

Open **Settings → HDHomeRun**. The page displays the effective tuner count, whether the stream limit is enforced, and the provider stream limit. Configurable fields include:

- **Enable HDHomeRun emulation**
- **Tuner Count Override**; blank uses the provider-derived value shown by the UI
- **Friendly Name**
- **Discovery (master)**
- **SSDP (UPnP)**
- **Allowed Networks**, one CIDR per line

The UI states that HDHomeRun setting changes take effect after a restart. Loopback is always allowed. Leaving **Allowed Networks** blank allows all connections, which the UI recommends only on a trusted LAN.

## Tuner count and provider limit

The settings page distinguishes the effective HDHomeRun tuner count from the provider stream limit. On the observed instance both were `2`, and stream-limit enforcement was active. Check this page rather than assuming the two values are always identical.

## Verification boundary

The browser retrieved the discovery, device, lineup, and lineup-status endpoints and confirmed that the lineup advertised the six published channels. No separate DVR client was available to test network auto-discovery or simultaneous tuner use.
