# Add the First Provider

A **provider** is an upstream source you configure — a playlist URL, a local file, or an Xtream Codes account. See [Concepts > Providers](../concepts/providers.md) for how providers relate to profiles and published output.

Open the web UI, go to **Providers**, and select **Add Provider**. The dialog has four tabs: **From URL**, **From File**, **Xtream Codes**, and **Import**.

## URL or file playlist — no extra setup

On **From URL**, enter a name and an `http://` or `https://` playlist URL. On **From File**, enter a name and use **Browse** to select an `.m3u` or `.m3u8` file on the server. Both tabs also offer an optional XMLTV URL.

To keep credentials out of the database, put them in `/config/.env` and reference them with `%VAR_NAME%` placeholders in the URL:

```env
# /config/.env
MY_PASSWORD=supersecret
```

```
http://my.server:8080/get.php?username=alice&password=%MY_PASSWORD%
```

## Optional quick test with IPTV.org

If you want to evaluate M3Undle before entering provider credentials, [IPTV.org](https://github.com/iptv-org/iptv) publishes playlists of publicly available streams that require no account. They are useful for confirming that M3Undle can fetch a remote playlist, import channels, build a lineup, and proxy playback.

Choose a smaller playlist for your country, language, category, or region from the official [IPTV.org playlist directory](https://github.com/iptv-org/iptv/blob/master/PLAYLISTS.md). For example, the United States country playlist is:

```text
https://iptv-org.github.io/iptv/countries/us.m3u
```

In M3Undle:

1. Open **Providers**, select **Add Provider**, then choose **From URL**.
2. Enter `IPTV.org test` as the name and paste the selected playlist URL.
3. Add the provider and wait for its first refresh to finish.
4. Continue to [Create the First Lineup/Profile](create-first-lineup.md), map a few channels, and select **Build Output**.
5. Play a published channel to exercise the complete path through M3Undle.

Avoid the global `index.m3u` playlist for a first test; a smaller playlist imports faster and is easier to explore. IPTV.org is an independent third-party project, not part of M3Undle. Individual public streams may be offline, geo-blocked, intermittent, or not available 24/7, so one failed channel does not necessarily indicate a problem with your installation.

## Xtream Codes — requires an encryption key

Xtream Codes providers store the password encrypted in the database (AES-256-GCM), which requires `M3UNDLE_ENCRYPTION_KEY` to be set (see [Install with Docker](install-with-docker.md)).

If you paste an M3U URL that happens to embed Xtream-style credentials, M3Undle can detect this automatically and offer to upgrade the provider to native Xtream mode without re-entering credentials.

## Import from config.yaml

If you have a `config.yaml` in `M3UNDLE_CONFIG_DIR` (normally `/config` in the Docker setup), the **Import** tab lists providers discovered in that file. Select an existing provider from the list to import it.

## What happens after you add a provider

- M3Undle auto-creates a profile with the same name, so the provider is immediately functional without extra steps.
- Provider groups appear as **unmapped** until you decide how they should be handled. Nothing publishes until channels are mapped and you build the output. See [Build a Lineup](../guides/build-a-lineup.md).
- Optional settings on the provider include a per-provider stream limit, a fetch timeout, and a relay policy for how M3Undle handles unstable upstreams — most installs can leave these at their defaults for now.

## Next step

**[Create the First Lineup/Profile →](create-first-lineup.md)**
