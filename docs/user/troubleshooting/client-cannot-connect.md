# Client Cannot Connect

Start with the container logs and a few direct checks before touching client-side settings — most connection failures are visible from the host in under a minute.

## 1. Confirm M3Undle itself is up

```bash
docker logs m3undle --tail 200
curl -i http://<host>:8080/livez
curl -i http://<host>:8080/readyz
```

`/livez` healthy but `/readyz` not means the process is running but not ready for normal traffic yet — check startup logs and whether a snapshot has been built.

## 2. Confirm the endpoint the client is using is actually reachable

```bash
curl -I http://<host>:8080
curl -I http://<host>:8080/m3u/m3undle.m3u
curl -I http://<host>:8080/xmltv/m3undle.xml
curl http://<host>:5004/discover.json
```

| Symptom | Likely cause |
|---|---|
| Connection refused / times out | Port not published in `compose.yaml`, or a firewall between the client and host |
| `curl` works from the host but not from the client device | Networking issue between the client and host, not M3Undle — check the client is on the same network/VLAN |
| M3U endpoint returns an empty playlist | No lineup has been published yet — see [Create the First Lineup/Profile](../getting-started/create-first-lineup.md), not a connection problem |
| HDHomeRun client can't find M3Undle via auto-discovery | Use manual tuner entry with `http://<host-ip>:5004` instead — see [LAN and Reverse-Proxy Problems](lan-and-reverse-proxy-problems.md) |

## 3. Check endpoint security

If **Settings → Endpoint Security** has credentials configured, M3U/XMLTV/stream/HDHomeRun endpoints require them. A client that isn't supplying the username/password will fail to connect even though the endpoint itself is reachable. This is separate from web UI login (`M3UNDLE_AUTH_ENABLED`) — see [Security](../concepts/security.md).

## 4. Check Xtream-specific auth

For Xtream-style clients (TiviMate, GSE Player, IPTV Smarters), confirm you're using the endpoint-security username/password, not a provider's own credentials — M3Undle presents its own Xtream-compatible API, separate from any upstream provider account.

## Still stuck

Include the M3Undle version tag, your `compose.yaml` with secrets removed, the client name, which endpoint type you're using, and relevant log output when reporting an issue.
