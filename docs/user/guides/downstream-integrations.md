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

The Add Integration dialog does not contain a profile selector or show a profile association — this version has no way to bind an integration to an individual profile. Do not assume that a notification can be scoped per profile unless a later version adds an explicit field.

## Empty state

With no integrations configured, the page displays:

> No integrations configured. Add one to enable automatic downstream notifications.

## Add an integration

1. Open **Settings → Integrations**.
2. Select **Add Integration**.
3. Enter a name and select **Jellyfin**, **Emby**, or **Generic Webhook**.
4. Complete the type-specific fields.
5. Select the lineup and guide triggers you need.
6. Confirm **Enabled** is set appropriately.
7. Select **Add Integration**.
