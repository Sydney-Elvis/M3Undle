# Missing EPG

Use the **EPG** page to separate source failures from channel-mapping problems, then verify the published XMLTV endpoint.

## 1. Select the provider and check Sources

Open **EPG**, select the provider, and remain on **Sources**. Check the source count and status summary, then find the source in the table.

The table shows its kind, URL or path, priority, status, and actions. If the source is not **OK**, hover over the action icons and use **Test: fetch and parse this source now**. Correct the source settings before trying to map channels.

For a new source, select **Add Source** and verify its name, kind, URL, priority, timeout, refresh cadence, and **Enabled** state. Credentials in a URL can use `%VAR_NAME%` values from `.env`.

## 2. Check Channel Mappings

Open **Channel Mappings**. Compare the total, mapped, and unmapped counts. A mapped row should show:

- the published channel and its `tvg-id`
- an EPG source
- a matched EPG channel
- a mapping mode such as **Auto (ID)**

If a channel is unmapped or mapped incorrectly, select its edit action.

## 3. Populate source channels before a manual override

The mapping panel may say:

> No channels found in this source. Run a test fetch first to populate channels.

If so, cancel the mapping, return to **Sources**, and run the source test. Then reopen the channel mapping, choose the EPG source and guide channel, and select **Save Mapping**.

You can also use the source action **Auto-map unmatched channels using this source** after a successful fetch.

## 4. Verify the published XMLTV output

Open the XMLTV URL copied from the dashboard:

```text
http://<host>:8080/xmltv/m3undle.xml
```

It should return XML containing `<channel>` and `<programme>` elements for the published lineup. If the endpoint is unreachable, follow [Client Cannot Connect](client-cannot-connect.md). If it returns XML but the affected channel is absent, recheck that the channel is published under **View Channels** and mapped on the EPG page.

## 5. Refresh the client

After correcting the source or mapping, trigger a guide refresh in the client. M3Undle can be serving updated XML while the client still displays its cached guide.
