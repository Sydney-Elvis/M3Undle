# Add the First Provider

A **provider** is an upstream source you configure — a playlist URL, a local file, or an Xtream Codes account. See [Concepts > Providers](../concepts/providers.md) for how providers relate to profiles and published output.

Open the web UI and go to **Provider**, then add a new provider using one of these three types.

## URL or file playlist — no extra setup

Paste any `http://` or `https://` playlist URL, or use the file browser for a local `.m3u` file.

To keep credentials out of the database, put them in `/config/.env` and reference them with `%VAR_NAME%` placeholders in the URL:

```env
# /config/.env
MY_PASSWORD=supersecret
```

```
http://my.server:8080/get.php?username=alice&password=%MY_PASSWORD%
```

## Xtream Codes — requires an encryption key

Xtream Codes providers store the password encrypted in the database (AES-256-GCM), which requires `M3UNDLE_ENCRYPTION_KEY` to be set (see [Install with Docker](install-with-docker.md)).

If you paste an M3U URL that happens to embed Xtream-style credentials, M3Undle can detect this automatically and offer to upgrade the provider to native Xtream mode without re-entering credentials.

## Import from config.yaml

If you have a `config.yaml` in `/config`, M3Undle can import providers from it directly via the Add Provider dialog. Useful if you're migrating from a file-based workflow.

## What happens after you add a provider

- M3Undle auto-creates a profile with the same name, so the provider is immediately functional without extra steps.
- Every provider group starts in a **pending** state — nothing publishes until you review it. See [Build a Lineup](../guides/build-a-lineup.md).
- Optional settings on the provider include a per-provider stream limit, a fetch timeout, and a relay policy for how M3Undle handles unstable upstreams — most installs can leave these at their defaults for now.

## Next step

**[Create the First Lineup/Profile →](create-first-lineup.md)**
