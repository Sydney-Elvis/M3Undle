# Downstream Integrations

M3Undle can notify a downstream media server or webhook after successful output updates so the downstream system can refresh its channels or guide data. Configure these notifications under **Settings → Integrations**.

## Supported integration types

Select **Add Integration** and choose one of the types shown by the UI:

- **Jellyfin**
- **Emby**
- **Generic Webhook**

Every type requires a friendly **Name** and has an **Enabled** switch.

## Jellyfin and Emby

Jellyfin and Emby use the same fields:

- **Base URL**, such as the media server's HTTP address
- **API Key**, described by the UI as the Jellyfin or Emby API key
- **Lineup update** trigger
- **Guide update** trigger
- **Enabled**

Both update triggers were selected by default in the Add Integration dialog. Clear a trigger if that server should not be notified for that kind of update.

## Generic Webhook

The webhook form contains:

- **Base URL** — the full URL M3Undle will send a `POST` request to
- **Headers JSON (optional)** — extra HTTP headers represented as JSON, with an authorization bearer header shown as the UI example
- **Lineup update** trigger
- **Guide update** trigger
- **Enabled**

Use **Headers JSON** when the receiver requires authentication or another custom header. Treat secrets in this field like any other credential.

## Update triggers

The dialog offers two independent checkboxes under **Trigger on**:

- **Lineup update**
- **Guide update**

Enable either one or both depending on what the downstream target needs to refresh. The Integrations page describes notifications as occurring after a successful lineup refresh.

## Profile binding

The validated Add Integration dialog did not contain a profile selector or show a profile association. This UI version therefore does not expose a way to bind an integration to an individual profile. Do not assume that a notification can be scoped per profile unless a later version adds an explicit field.

## Empty state and configured status

No integrations were configured on the validated instance. The page displayed:

> No integrations configured. Add one to enable automatic downstream notifications.

Because the list was empty, no configured-integration status, last-delivery result, error, or notification history was available to document. After adding an integration, use the fields actually displayed in its row or detail view rather than assuming a history feature exists.

## Add an integration

1. Open **Settings → Integrations**.
2. Select **Add Integration**.
3. Enter a name and select **Jellyfin**, **Emby**, or **Generic Webhook**.
4. Complete the type-specific fields.
5. Select the lineup and guide triggers you need.
6. Confirm **Enabled** is set appropriately.
7. Select **Add Integration**.

## What wasn't verified

The empty state, supported types, all type-specific fields, default trigger selections, and lack of a profile selector were observed directly. No integration was created, so delivery payloads, authentication, retry behavior, configured-row actions, status, and notification history could not be verified.
