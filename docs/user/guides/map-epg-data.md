# Map EPG Data

Open **EPG** to manage guide sources and connect published channels to guide entries. Select the provider at the top of the page before working with its sources.

## Review Sources

The **Sources** tab lists each source's name, kind, URL or path, priority, status, and actions. Lower priority numbers are used first.

The source action tooltips are:

- **Edit this source's settings**
- **Test: fetch and parse this source now**
- **Auto-map unmatched channels using this source**
- **Delete this source and all its fetch history and channel mappings**

Run the test action after adding or changing a source. A successful fetch populates the guide-channel choices used by manual mapping.

## Add a source

Select **Add Source**. The observed URL-source form contained:

- **Name**
- **Kind**, described as how the source is fetched
- **URL**, including support for `%VAR_NAME%` credentials from `.env`
- **Priority**
- **Timeout (seconds)**
- **Refresh cadence**, which can override the global schedule for this source
- **Enabled**
- **Advanced (authentication, custom headers)**

Select **Add Source** to save it, then use the source's test action to fetch and parse it.

## Check automatic mappings

Open **Channel Mappings**. The page shows total, mapped, and unmapped counts. Each row contains:

- channel name
- `tvg-id`
- EPG source
- matched EPG channel
- mapping mode
- actions

On the observed profile, all six published channels were mapped in **Auto (ID)** mode.

## Override a mapping

Select the edit action for the channel. The **Map — channel name** panel shows the channel, its current `tvg-id`, an **EPG Source** choice, and the guide-channel choice.

If the panel says **No channels found in this source. Run a test fetch first to populate channels**, cancel the edit, return to **Sources**, and test the relevant source. After guide channels are available, select the correct source and channel, then choose **Save Mapping**.

## Verify the published guide

Return to **Channel Mappings** and confirm that the mapped count and the affected row are correct. The dashboard's XMLTV URL should return XML containing the published channels:

```text
http://<host>:8080/xmltv/m3undle.xml
```

See [Missing EPG](../troubleshooting/missing-epg.md) if the source is healthy but a client still has no programme data.

## What was not changed during validation

The existing provider source reported **OK**, and its six published channels showed automatic mappings. The add-source and manual-map panels were inspected and cancelled; no new guide source or override was saved.
