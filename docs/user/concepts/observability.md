# Observability

M3Undle exposes runtime measurements in Prometheus text format at `/metrics`. Open **Settings → Observability** to control whether the endpoint is available and who can scrape it.

## Endpoint controls

The available settings are:

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

## Channel labels and cardinality

**Enable channel labels** adds `channel_name` and `channel_id` to stream metrics. The UI warns that every distinct channel increases the number of Prometheus time series and recommends avoiding this option on lineups larger than a few hundred channels.

## What the endpoint currently exposes

The live output included measurements for:

- provider refreshes and availability
- lineup changes and publication
- EPG size, freshness, and unmatched channels
- active streaming sessions, upstream connections, and downstream clients
- HDHomeRun discovery and tuner use
- HTTP request count and duration
- build information and process uptime

See [Metrics](../reference/metrics.md) for the exact metric names.

## Access behavior

With **Local only** selected and no allowed CIDR matching the requester, `/metrics` returns `403`. With **Public** access, it returns Prometheus text with HTTP `200`.
