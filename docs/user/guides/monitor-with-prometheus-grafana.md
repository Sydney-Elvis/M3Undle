# Monitor with Prometheus / Grafana

M3Undle publishes Prometheus-compatible metrics at `/metrics`. Configure access in M3Undle first, then add it as a scrape target in Prometheus and use that Prometheus server as a Grafana data source.

## 1. Enable the endpoint

Open **Settings → Observability** and enable **Expose metrics endpoint**. The page displays the scrape path:

```text
/metrics
```

Choose an **Access mode** appropriate for the scraper:

- **Local only (loopback + CIDRs)** for a scraper on the host, Docker network, or another explicitly allowed internal network.
- **Token (Bearer auth required)** when the scraper should authenticate with a generated token.
- **Public — no authentication** only when another trusted network boundary protects the endpoint.
- **Disabled (no endpoint)** to stop exposing metrics.

For local-only access, add the Prometheus network to **Allowed CIDRs**, one CIDR per line. Loopback is always allowed. Select **Apply**.

## 2. Confirm access from the scraper network

From the same network namespace as Prometheus, request:

```bash
curl -i http://<m3undle-host>:8080/metrics
```

A permitted request returns HTTP `200` and Prometheus text beginning with `# TYPE` declarations. A request outside the configured local networks returns `403`.

## 3. Add a Prometheus scrape job

For a scraper permitted by **Local only**, a minimal job is:

```yaml
scrape_configs:
  - job_name: m3undle
    metrics_path: /metrics
    static_configs:
      - targets:
          - m3undle:8080
```

Use a hostname reachable from the Prometheus container. **Settings → Endpoint URLs** recommends the Compose service form `http://m3undle:8080` for containers on the same Docker network.

If you select **Token**, generate a token in **Metrics Tokens** and configure Prometheus to send it as a bearer token. Consult the Prometheus version's configuration reference for the supported secret-file setting.

## 4. Check useful queries

After Prometheus has scraped M3Undle, start with:

```promql
m3undle_provider_up
m3undle_stream_sessions_active
m3undle_epg_age_seconds
m3undle_epg_unmatched_channels_total
m3undle_hdhr_tuners_in_use
rate(http_requests_total[5m])
```

For latency percentiles, use the `http_request_duration_seconds` histogram, grouped by its `route`, `method`, and `status_code` labels as needed.

## 5. Add Prometheus to Grafana

In Grafana, add the Prometheus server—not M3Undle's `/metrics` endpoint—as a Prometheus data source. Build panels from the exact names in the [Metrics reference](../reference/metrics.md).

Keep high-cardinality dimensions under control. M3Undle's **Enable channel labels** option adds channel identifiers to stream measurements and carries an explicit warning for lineups larger than a few hundred channels.
