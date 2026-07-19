# Metrics

This catalog comes from the live `/metrics` output of M3Undle `v1.0.0-beta.6`. It lists only metric families that were actually present in that payload.

Every observed series included `otel_scope_name` and `otel_scope_version`. The tables below list additional labels that distinguish measurements.

## Provider and lineup

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_provider_refresh_duration_seconds` | histogram | `provider_id`; histogram bucket `le` |
| `m3undle_provider_up` | gauge | `provider_id` |
| `m3undle_provider_last_refresh_timestamp_seconds` | gauge | `provider_id` |
| `m3undle_provider_stream_limit` | gauge | `provider_id` |
| `m3undle_lineup_added_channels_total` | counter | `profile_id` |
| `m3undle_lineup_channels_total` | gauge | — |
| `m3undle_lineup_channels_enabled_total` | gauge | — |
| `m3undle_lineup_last_publish_timestamp_seconds` | gauge | — |

Histograms expose the standard `_bucket`, `_sum`, and `_count` series. Timestamp metrics and duration metrics declare seconds as their unit in the payload.

## Streaming

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_stream_sessions_active` | gauge | — |
| `m3undle_upstream_connections_active` | gauge | — |
| `m3undle_downstream_clients_active` | gauge | — |
| `m3undle_stream_share_ratio` | gauge | — |

Channel labels were disabled on the captured instance. The UI says **Enable channel labels** can add `channel_name` and `channel_id` to stream metrics, but no such labeled series was present in this payload.

## EPG

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_epg_channels_total` | gauge | — |
| `m3undle_epg_programs_total` | gauge | — |
| `m3undle_epg_last_refresh_timestamp_seconds` | gauge | — |
| `m3undle_epg_age_seconds` | gauge | — |
| `m3undle_epg_unmatched_channels_total` | gauge | — |

## HDHomeRun

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_hdhr_discovery_requests_total` | counter | — |
| `m3undle_hdhr_tuners_total` | gauge | — |
| `m3undle_hdhr_tuners_in_use` | gauge | — |

## HTTP

| Metric | Type | Additional labels |
|---|---|---|
| `http_requests_total` | counter | `method`, `route`, `status_code` |
| `http_request_duration_seconds` | histogram | `method`, `route`, `status_code`; histogram bucket `le` |

The observed `route` label included application pages, media endpoints, health checks, HDHomeRun endpoints, Blazor framework requests, and `404` routes. Avoid creating a separate dashboard panel for every raw route unless that level of detail is useful.

## Runtime information

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_build_info` | gauge | `build_number`, `version` |
| `m3undle_uptime_seconds` | gauge | — |

## Example queries

```promql
# Provider availability by provider ID
m3undle_provider_up

# Age of the current EPG data
m3undle_epg_age_seconds

# HTTP request rate grouped by route and response status
sum by (route, status_code) (rate(http_requests_total[5m]))

# 95th percentile HTTP request duration
histogram_quantile(
  0.95,
  sum by (le, route) (rate(http_request_duration_seconds_bucket[5m]))
)
```

## What wasn't verified

The catalog reflects one scrape while no stream was active. Metrics emitted only during active playback, failures, token use, or other unobserved states may exist but are intentionally not listed. Metric stability across future versions and channel-labeled variants were not verified.
