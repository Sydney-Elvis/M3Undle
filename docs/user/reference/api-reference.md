# API Reference

M3Undle has a management REST API (`/api/v1/...`) covering providers, profiles, channels, EPG sources, downstream integrations, and site settings. Every management endpoint requires authentication via the same cookie-based UI login — there's no separate API key for it.

## Interactive reference is a Development-only feature

The interactive Scalar API reference and the raw OpenAPI JSON document are only available when the app is running with `ASPNETCORE_ENVIRONMENT=Development` — they're intentionally disabled on a standard production deployment, including the default Docker image, which runs in `Production` mode. This isn't a missing feature or a bug; it's deliberate, since the full management API surface isn't something that should be publicly discoverable by default.

If you're running from source with `ASPNETCORE_ENVIRONMENT=Development` (see [Build and Run From Source](https://github.com/Sydney-Elvis/M3Undle/blob/main/docs/dev/BUILD.md)):

- Interactive reference: `http://<host>:8080/scalar`
- Raw OpenAPI 3.0 JSON: `http://<host>:8080/openapi/management.json`

Both require you to be logged in to the web UI first (via `/Account/Login`) — the reference itself documents this and lets you authenticate from within the Scalar UI.

## On a standard production deployment

Neither `/scalar` nor `/openapi/management.json` is reachable — both return `404`, by design. There's currently no generated, publicly-hosted API reference for the management API. A build-time OpenAPI generation step for the documentation site itself (producing a static reference without needing Development mode) is planned — see Milestone 4 of the documentation plan — but hasn't shipped yet.

If you need to know what a specific management endpoint does today, the most reliable source is the endpoint definitions themselves in `src/M3Undle.Web/Api/` in the repository.
