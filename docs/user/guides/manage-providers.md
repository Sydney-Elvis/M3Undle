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

## What was not changed during validation

The walkthrough opened the real provider's editor and preview, but did not change credentials, enablement, profile association, content types, or stream limit. Disable and delete behavior was identified from the UI tooltips and was not executed.
