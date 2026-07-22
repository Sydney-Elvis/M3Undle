# Metrics

This catalog lists the metric families M3Undle exposes at `/metrics`. Counters and other event-driven series don't appear in a scrape until the event first occurs — a failure counter, a reconnect counter, or a tune counter reads as absent (not zero) on a deployment where that has never happened — so an idle instance's scrape will show fewer series than are listed here. That's expected: the metric is defined, it just hasn't been emitted yet.

Every series includes `otel_scope_name` and `otel_scope_version`. The tables below list additional labels that distinguish measurements.

## Provider and lineup

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_provider_refresh_duration_seconds` | histogram | `provider_id`; histogram bucket `le` |
| `m3undle_provider_refresh_failures_total` | counter | `provider_id` |
| `m3undle_provider_up` | gauge | `provider_id` |
| `m3undle_provider_last_refresh_timestamp_seconds` | gauge | `provider_id` |
| `m3undle_provider_stream_limit` | gauge | `provider_id` |
| `m3undle_provider_stream_limit_reached_total` | counter | `provider_id` |
| `m3undle_lineup_added_channels_total` | counter | `profile_id` |
| `m3undle_lineup_removed_channels_total` | counter | `profile_id` |
| `m3undle_lineup_renamed_channels_total` | counter | `profile_id` |
| `m3undle_lineup_mapping_changes_total` | counter | `profile_id` |
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
| `m3undle_stream_sessions_started_total` | counter | `provider_id`, `stream_type`, `protocol` |
| `m3undle_stream_sessions_ended_total` | counter | `provider_id`, `stream_type`, `protocol` |
| `m3undle_stream_reconnects_total` | counter | `provider_id` |
| `m3undle_stream_failures_total` | counter | `provider_id`, `result` |

**Enable channel labels** (**Settings → Observability → Advanced Options**) adds `channel_name` and `channel_id` to the active-count stream gauges (`m3undle_stream_sessions_active`, `m3undle_upstream_connections_active`, `m3undle_downstream_clients_active`) when turned on. The event counters carry only the labels shown above.

## EPG

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_epg_channels_total` | gauge | — |
| `m3undle_epg_programs_total` | gauge | — |
| `m3undle_epg_last_refresh_timestamp_seconds` | gauge | — |
| `m3undle_epg_age_seconds` | gauge | — |
| `m3undle_epg_unmatched_channels_total` | gauge | — |
| `m3undle_epg_refresh_failures_total` | counter | — |

## HDHomeRun

| Metric | Type | Additional labels |
|---|---|---|
| `m3undle_hdhr_discovery_requests_total` | counter | — |
| `m3undle_hdhr_tune_requests_total` | counter | — |
| `m3undle_hdhr_tune_failures_total` | counter | — |
| `m3undle_hdhr_tuners_total` | gauge | — |
| `m3undle_hdhr_tuners_in_use` | gauge | — |

## HTTP

| Metric | Type | Additional labels |
|---|---|---|
| `http_requests_total` | counter | `method`, `route`, `status_code` |
| `http_request_duration_seconds` | histogram | `method`, `route`, `status_code`; histogram bucket `le` |

The `route` label covers application pages, media endpoints, health checks, HDHomeRun endpoints, Blazor framework requests, and `404` routes. Avoid creating a separate dashboard panel for every raw route unless that level of detail is useful.

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

# Stream failure rate by provider
sum by (provider_id) (rate(m3undle_stream_failures_total[5m]))

# Stream reconnects (a rising rate signals a flapping upstream)
sum by (provider_id) (rate(m3undle_stream_reconnects_total[5m]))

# Provider refresh failures over the last hour
increase(m3undle_provider_refresh_failures_total[1h])
```

## Coverage

This list may not be exhaustive across future M3Undle versions — check a live `/metrics` scrape for the definitive set on your deployment.
