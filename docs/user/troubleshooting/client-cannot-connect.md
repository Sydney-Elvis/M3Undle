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
curl http://<host>:5004/discover.json    # default HDHomeRun port
curl http://<host>:8080/discover.json    # also works if only 8080 is published
```

| Symptom | Likely cause |
|---|---|
| Connection refused / times out | Port not published in `compose.yaml`, or a firewall between the client and host |
| `curl` works from the host but not from the client device | Networking issue between the client and host, not M3Undle — check the client is on the same network/VLAN |
| M3U endpoint returns an empty playlist | No lineup has been published yet — see [Create the First Lineup/Profile](../getting-started/create-first-lineup.md), not a connection problem |
| HDHomeRun client can't find M3Undle via auto-discovery | Open M3Undle's **HDHomeRun** page and use its **Discover JSON** URL for manual tuner entry — it works on whichever port(s) your deployment actually publishes, so don't assume 5004 specifically — see [LAN and Reverse-Proxy Problems](lan-and-reverse-proxy-problems.md) |

## 3. Check endpoint security

If **Settings → Security → Endpoint Credentials** has enforcement enabled, M3U/XMLTV/stream and Xtream endpoints require the configured credential. A client that isn't supplying the username/password will fail even though the server itself is reachable. This is separate from **UI Authentication**—see [Security](../concepts/security.md).

## 4. Check Xtream-specific auth

For Xtream-style clients (TiviMate, GSE Player, IPTV Smarters), confirm you're using the endpoint-security username/password, not a provider's own credentials — M3Undle presents its own Xtream-compatible API, separate from any upstream provider account.

## Still stuck

Include the M3Undle version, your `compose.yaml` with secrets removed, the client name, which endpoint type you're using, and relevant log output when reporting an issue. Find the exact version, build commit, and build date by clicking the version number in the footer, which opens an **About** panel.
