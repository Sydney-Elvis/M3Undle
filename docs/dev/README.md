# Developer Documentation

This is contributor/maintainer reference for M3Undle — not user-facing. If you're looking for install/configuration/usage docs, see [DOCKER.md](../DOCKER.md), [GUI.md](../GUI.md), and [OBSERVABILITY.md](../OBSERVABILITY.md) (being migrated to a public documentation site under `docs/user/` — tracked in [issue #114](https://github.com/Sydney-Elvis/M3Undle/issues/114) if you're working on that).

None of the files below are part of the public documentation site. They live here, in the public GitHub repo, for contributors.

## Getting started as a contributor

- [BUILD.md](BUILD.md) — build and run M3Undle from source locally
- [PROJECT_BOUNDARIES.md](PROJECT_BOUNDARIES.md) — runtime/stack summary, the Core/Web/CLI ownership rules, and the Product Invariants (load-bearing facts — breaking one silently passes review and breaks in production). Read this before touching anything that crosses a project boundary. Not to be confused with `docs/design/ARCHITECTURE_MAP.md` below, which covers the system/endpoint overview, not code ownership.
- [GUI_CONSISTENCY_CONTRACT.md](GUI_CONSISTENCY_CONTRACT.md) — required chip/tooltip/color conventions for UI changes under `src/M3Undle.Web/Components`

## Project status and process

- [PROJECT_PLAN.md](PROJECT_PLAN.md) — the canonical release roadmap: per-alpha feature checklists, current Beta status and open items
- [BETA_VALIDATION_CHECKLIST.md](BETA_VALIDATION_CHECKLIST.md) — the current, still-in-progress Beta sign-off gate (detailed test steps live in `m3undle-lab/docs/SRV2_CLIENT_CHECKLIST.md`)

## Design and architecture

Precise, living specs — kept in sync with the code, not simplified for an end-user audience. See `docs/design/`:

- [ARCHITECTURE_MAP.md](../design/ARCHITECTURE_MAP.md) — process/endpoint overview and core concepts
- [DB_SCHEMA.md](../design/DB_SCHEMA.md) — full SQLite schema, including the "Appendix C — Current Constraints and Reserved Fields" active-vs-reserved pattern
- [HTTP_COMPATIBILITY.md](../design/HTTP_COMPATIBILITY.md) — the external HTTP contract (M3U/XMLTV/stream/HDHR/Xtream endpoints)
- [LINEUP_RULES.md](../design/LINEUP_RULES.md) — group review states, event tracking policies, placeholder suppression
- [NUMBERING_RULES.md](../design/NUMBERING_RULES.md) — channel numbering precedence, conflict avoidance, overflow behavior

## Release process and package specs

See `docs/spec/`:

- [config_spec.md](../spec/config_spec.md) — the `config.yaml` schema M3Undle Web imports providers from
- [core_public_api.md](../spec/core_public_api.md) — `M3Undle.Core`'s public API surface and compatibility policy
- [nuget_release_guide.md](../spec/nuget_release_guide.md) — how to publish a new `M3Undle.Core` package version
- [version_management.md](../spec/version_management.md) — Core vs. Web version files and SemVer rules

## Drift prevention

This documentation has drifted before, in specific, recurring ways — not "nobody updates it," but a handful of failure modes that keep recurring. Watch for these when editing anything above:

- A `docs/design/*` brief written before a feature is implemented must be reconciled with final behavior once that feature ships, or explicitly marked as a historical design brief that no longer reflects current behavior. An unreconciled pre-implementation brief is worse than no document — it actively misleads (this happened to `docs/design/stream_proxy_design.md`, removed in the 2026-07-18 documentation cleanup).
- Docs that reproduce the root `compose.yaml` (ports, volumes, env vars, required files) must be updated in the same PR whenever `compose.yaml` changes. Do not let example compose blocks in docs drift from the file contributors actually run.
- When a tool or subsystem is extracted out of this repo (e.g. the CLI move to `m3undle-cli`), the PR that removes it must grep `docs/` for references and delete or redirect them — do not leave docs describing a tool that no longer builds from this repository.
- Docs describing partially-implemented or forward-looking schema/behavior (see `docs/design/DB_SCHEMA.md` Appendix C for the pattern) must explicitly separate what's active today from what's reserved for later, and that split must be revisited when a reserved feature graduates to active.
- Any PR that changes a compatibility endpoint, environment variable, default value, or DB schema field that is documented in `docs/` must update that documentation in the same PR.
