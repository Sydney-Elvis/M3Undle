# HDHomeRun-Compatible Clients

M3Undle emulates an HDHomeRun network tuner so DVR applications — NextPVR, Jellyfin Live TV, Emby Live TV, Plex, Channels DVR — can consume your lineup as if it were a hardware tuner. See [HDHomeRun Compatibility](../concepts/hdhomerun-compatibility.md) for the device identity, discovery, and tuner-configuration concepts; this page covers Docker networking specifically.

## Option A — Manual add (recommended)

The most reliable setup across all Docker networking modes and client applications. No discovery ports, no special networking.

1. Keep `5004:5004` and `8080:8080` published in your compose file.
2. In your DVR application, add a network tuner manually:
   - **Jellyfin**: Dashboard → Live TV → Add Tuner Device → HD Homerun → `http://<host-ip>:5004`
   - **NextPVR**: Settings → Tuners → Add → `http://<host-ip>:5004`
   - **Emby**: Live TV → Add Tuner → HDHomeRun → `http://<host-ip>:5004`
   - **Plex**: Settings → Live TV & DVR → Set Up → `http://<host-ip>:5004`
3. The client connects, fetches `discover.json` and `lineup.json`, and shows your channels.

No extra environment variables needed — HDHR is enabled by default, and the rest of its behavior can be adjusted later in **Settings → HDHomeRun**.

## Option B — Auto-discovery (best effort)

Lets some clients find M3Undle automatically, similar to a real HDHomeRun. Works best on flat LAN or host-network deployments. In Docker bridge or NAT-like setups, discovery may not reach all clients — Option A is the fallback.

Not all clients handle discovery identically. Some (NextPVR) parse the advertised base URL from the discovery response and connect on the correct port. Others (Jellyfin) may ignore the advertised URL and attempt the responder IP on port 80, which fails when M3Undle serves HDHR on port 5004 — for Jellyfin specifically, manual add (Option A) is the supported path.

1. Discovery is enabled by default. To force the startup default from Docker:
   ```yaml
   M3Undle__HdHomeRun__DiscoveryEnabled: "true"
   ```
2. Publish the discovery ports:
   ```yaml
   ports:
     - "5004:5004"
     - "8080:8080"
     - "1900:1900/udp"    # SSDP / UPnP
     - "65001:65001/udp"  # SiliconDust discovery
   ```
3. Set the advertised base URL so discovery responses point to your host, not the container's internal IP:
   ```yaml
   M3Undle__HdHomeRun__AdvertisedBaseUrl: "http://192.168.1.50:5004"
   ```
   Replace with the LAN IP of the Docker host.

!!! important "Docker bridge networking and multicast"
    SSDP relies on UDP multicast, which doesn't pass through Docker's default bridge network. Discovery may work for clients on the Docker host itself, but **clients on other machines will not see M3Undle via auto-discovery** unless you use `network_mode: host` (Option C) or a macvlan network.

## Option C — Host networking (full discovery compatibility)

For auto-discovery to work identically to a real HDHomeRun, including from other machines on the LAN:

```yaml
services:
  m3undle:
    image: ghcr.io/sydney-elvis/m3undle:beta
    network_mode: host
    environment:
      TZ: America/New_York
      M3Undle__HdHomeRun__DiscoveryEnabled: "true"
    volumes:
      - ./config:/config
      - ./data:/data
    restart: unless-stopped
```

With `network_mode: host`, the container shares the host's network stack directly — no port mapping is needed or allowed. SSDP multicast works because the container is on the real LAN interface.

!!! warning
    `network_mode: host` bypasses Docker network isolation. All container ports are exposed directly on the host. Only use this on a trusted LAN.

!!! important "Option C requires a Linux Docker host"
    On **Docker Desktop for Windows or macOS**, `network_mode: host` does not do what it does on Linux. Containers run inside a Linux VM, so "the host" is that VM rather than your Windows or Mac machine — the container joins the VM's network, not your LAN, and SSDP multicast still doesn't reach other machines. The container also becomes unreachable on `localhost` because no ports are published.

    On those platforms use **Option A (manual add)** with `AdvertisedBaseUrl` set to the machine's real LAN IP. It works regardless of networking mode and is the recommended path anyway. Auto-discovery across the LAN needs a Linux Docker host, or M3Undle running directly on the LAN via macvlan.

## Choosing a networking mode

| Setup | Discovery from host? | Discovery from LAN? | Manual add works? |
|---|---|---|---|
| Bridge (default) | Sometimes | No | **Yes** |
| Bridge + UDP ports published | Yes | Unreliable | **Yes** |
| `network_mode: host` (Linux host only) | Yes | **Yes** | **Yes** |
| `network_mode: host` (Docker Desktop) | No | No | No — no ports published |
| macvlan | Yes | **Yes** | **Yes** |

**Recommendation**: use manual add (Option A) unless you have a specific reason to need auto-discovery — it works with any Docker networking mode and any client application. If you do need auto-discovery, `network_mode: host` (Option C) is the only straightforward option that reliably supports SSDP multicast across the whole LAN, though even then some clients may not follow the advertised base URL — manual add remains the most portable fallback.

**Why multicast is tricky in Docker**: SSDP discovery uses UDP multicast on `239.255.255.250:1900`. Docker's bridge network creates a virtual network segment that multicast packets don't cross in either direction. Publishing the port (`-p 1900:1900/udp`) only helps unicast traffic — it doesn't bridge multicast.

**`AdvertisedBaseUrl` explained**: the discovery response includes a URL where the client should connect for tuning. If M3Undle is behind Docker NAT, it may auto-detect an internal address like `172.17.0.x`, which is unreachable from the LAN. Setting `AdvertisedBaseUrl` to your host's real IP ensures clients get a reachable address. Required for bridge networking with discovery; not needed with `network_mode: host`.

## Disabling HDHR entirely

```yaml
M3UNDLE_HDHR_ENABLED: "false"
```

Disables all HDHR endpoints and discovery. Port 5004 still listens (it's set at the ASP.NET level) but returns 404 for HDHR routes. Since this is a startup override, it also blocks normal HDHR management from the UI until the env var is removed and the container restarted.
