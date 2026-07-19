# Observability

M3Undle exposes runtime measurements in Prometheus text format at `/metrics`. Open **Settings → Observability** to control whether the endpoint is available and who can scrape it.

## Endpoint controls

The observed settings are:

- **Expose metrics endpoint** — enables or disables metrics output.
- **Access mode** — controls who can request `/metrics`.
- **Allowed CIDRs** — one network per line. Loopback is always allowed; add Docker bridge or internal scraper networks when using local-only access.
- **Enable channel labels** under **Advanced Options**.

The UI offers four access modes:

- **Local only (loopback + CIDRs)**
- **Token (Bearer auth required)**
- **Public — no authentication**
- **Disabled (no endpoint)**

Select **Apply** after changing the endpoint, mode, CIDRs, or advanced options.

## Metrics tokens

When access mode is **Token**, use the **Metrics Tokens** section. Enter a **Token Name**, optionally set **Expires**, and select **Generate**. The page recommends one token per scraper so an individual scraper can be revoked independently.

The validated instance had no metrics tokens configured.

## Channel labels and cardinality

**Enable channel labels** adds `channel_name` and `channel_id` to stream metrics. The UI warns that every distinct channel increases the number of Prometheus time series and recommends avoiding this option on lineups larger than a few hundred channels.

It was disabled on the validated instance, so those labels were not present in the captured payload.

## What the endpoint currently exposes

The live output included measurements for:

- provider refreshes and availability
- lineup changes and publication
- EPG size, freshness, and unmatched channels
- active streaming sessions, upstream connections, and downstream clients
- HDHomeRun discovery and tuner use
- HTTP request count and duration
- build information and process uptime

See [Metrics](../reference/metrics.md) for the exact names present in the captured `v1.0.0-beta.6` payload.

## Access behavior observed

With **Local only** selected and no allowed CIDR matching the browser, `/metrics` returned `403`. With temporary **Public** access it returned Prometheus text with HTTP `200`. The original **Local only** setting was restored afterward, and another request again returned `403`.

## What wasn't verified

Token generation, bearer-token requests, token expiry/revocation, disabled-endpoint behavior, allowed-CIDR matching, and channel-label output were not exercised. No observability setting other than the temporary approved access-mode change was applied, and that change was reverted.
