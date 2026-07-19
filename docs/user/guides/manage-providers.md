# Manage Providers

Open **Providers** to view upstream sources and their relationship to published profiles.

## Read the provider table

The **Configured Providers** table shows:

- provider name and type
- associated profile
- maximum streams
- last refresh
- expiry, when available
- current status
- actions

Select the profile chip to open its profile details.

## Edit a provider

Hover over the pencil icon and select **Edit provider settings**. The fields depend on provider type. For the observed Xtream Codes provider, the editor contained:

- **Name**
- **Server URL**
- **Username**
- masked **Password**, with a separate **Change** action
- **Include XMLTV guide from same server**
- **Associated Profile**
- **Include VOD / Movies** and **Include Series**
- **Limit concurrent streams** and **Stream limit**
- **Enabled**
- **Advanced Options**

Select **Save Changes** only after reviewing the effect on the associated profile. Passwords are not displayed in plaintext.

## Enable, disable, or delete

The action icons expose tooltips:

- **Disable this provider** toggles an enabled provider off. A disabled provider is retained rather than deleted.
- **Permanently delete this provider and all associated data** is destructive.

Do not use delete as a troubleshooting step. If you only need to stop using a source temporarily, disable it instead.

## Preview current provider content

Select **Preview** to fetch the latest provider lineup without publishing it. The preview area displays **Sample Size**, **Group Filter**, progress, and **Cancel** while the fetch is running.

Preview is useful when checking whether a group or channel still exists upstream. It does not replace **Build Output** on the **Channel Mapping** page.

## Add another provider

Select **Add Provider** to open **From URL**, **From File**, **Xtream Codes**, or **Import**. See [Add the First Provider](../getting-started/add-first-provider.md) for the fields observed on each tab.

## Credential security

- **Xtream passwords** are encrypted (AES-256-GCM) and stored in the database. The plaintext password is never persisted.
- **URL credentials via `.env`** (`%VAR_NAME%` substitution) are stored in plaintext in `/config/.env`. Restrict file permissions on the host accordingly.
- **The encryption key** (`M3UNDLE_ENCRYPTION_KEY`) should be set as an environment variable, not stored in `/config/.env` — anyone with read access to `.env` would gain access to the key.

## Rotating the encryption key

If `M3UNDLE_ENCRYPTION_KEY` is compromised, or as routine hygiene, M3Undle supports rotating it with no data loss and minimal downtime, using `M3UNDLE_ENCRYPTION_KEYS` (plural) instead of the single-value `M3UNDLE_ENCRYPTION_KEY`:

```
M3UNDLE_ENCRYPTION_KEYS=<keyId>:<base64key>[,<keyId>:<base64key>...]
```

The **first** entry is the active key, used to encrypt everything from now on. Every entry in the list can still decrypt existing data — this is what lets old and new keys coexist during a rotation. `M3UNDLE_ENCRYPTION_KEYS` takes precedence over `M3UNDLE_ENCRYPTION_KEY` if both are present.

This is fully scriptable — the steps below are exactly what an automation job would run:

1. **Generate a new key and add it to the ring, ahead of the old one:**

   ```bash
   openssl rand -base64 32
   ```

   ```yaml
   # compose.yaml — old key stays present (decrypt-only) during the transition
   environment:
     M3UNDLE_ENCRYPTION_KEYS: "2026-07:NEW_KEY_HERE,2026-01:OLD_KEY_HERE"
   ```

   Restart the container. This is a normal, brief restart — not a maintenance window; nothing is re-encrypted yet.

2. **Trigger the bulk re-encryption** (requires login if `M3UNDLE_AUTH_ENABLED` is set):

   ```bash
   curl -X POST http://<host>:8080/api/v1/encryption/rotate
   ```

   This takes a database backup (`VACUUM INTO`, saved under `/data/backups/`) and re-encrypts every stored Xtream password and downstream integration API key under the new active key, inside a single transaction — it either fully succeeds or changes nothing. Calling it again once everything is migrated is a cheap no-op.

3. **Confirm nothing is left on the old key:**

   ```bash
   curl http://<host>:8080/api/v1/encryption/status
   ```

   Look for `providersOnOtherKey` and `downstreamIntegrationsOnOtherKey` to both read `0`.

4. **After a rollback window you're comfortable with**, drop the old key from `M3UNDLE_ENCRYPTION_KEYS` and restart once more:

   ```yaml
   environment:
     M3UNDLE_ENCRYPTION_KEYS: "2026-07:NEW_KEY_HERE"
   ```

Backups written to `/data/backups/` aren't automatically pruned — clean up old ones periodically.

## What was not changed during validation

The walkthrough opened the real provider's editor and preview, but did not change credentials, enablement, profile association, content types, or stream limit. Disable and delete behavior was identified from the UI tooltips and was not executed.
