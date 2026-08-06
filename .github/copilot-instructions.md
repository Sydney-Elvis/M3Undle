# Copilot Instructions

If `AGENTS.md` is present at the repo root, read it first — it is the canonical
instruction set and overrides or extends everything below. `AGENTS.md` is
intentionally not tracked in git (it may contain local deployment notes), so it
will not be present in GitHub-hosted environments. The instructions below
represent the safe-to-publish subset and are authoritative when `AGENTS.md` is
absent.

---

## MCP / Documentation Tools

- Always check the `mcpjungle` MCP server for available tools before using
  external services. `mslearn` and `context7` may be available through it —
  look there first.
- Use `mslearn` tools for .NET / C# / ASP.NET Core / Blazor / Entity Framework
  topics. Confirm current official docs before implementing or advising.
- Use `context7` tools for third-party libraries in scope (SQLite, MudBlazor,
  and other repo dependencies). Confirm docs and best practices.
- Match recommendations to the version actually installed or targeted in this
  project. If MCP access is unavailable, say so explicitly.

## Project Reference

- Runtime/stack summary, Core/Web/CLI ownership rules, and **Product Invariants**
  live in `docs/dev/PROJECT_BOUNDARIES.md`. Read it before touching anything
  that crosses a project boundary or a listed invariant — treat every invariant
  as binding, not a suggestion.
- Web coding-style rules (minimal APIs, `TypedResults`, scoped-service pattern,
  comment policy) are in `CONTRIBUTING.md`'s Guidelines section.
- `docs/dev/README.md` is the full contributor documentation index.

## AI Working Files

- All AI-generated planning documents, implementation plans, and design notes
  belong in `.ai_docs/` at the repo root.
- Check `.ai_docs/` for relevant context at the start of any session.
- Never create AI working files outside `.ai_docs/`.

## Quality Gate

- Build, run the relevant tests, and resolve all new warnings before considering
  a task complete — not just before a PR.
- Update tests in the same task if an API or constructor change breaks them.

## GUI Consistency

- Follow `docs/dev/GUI_CONSISTENCY_CONTRACT.md` for chip semantics, tooltip
  requirements, click affordance rules, icon-only action patterns, and alert
  consistency.
- For UI changes in `src/M3Undle.Web/Components`, treat this contract as
  required unless the user explicitly requests an exception.

## Git Policy

- Never perform git write operations (commit, push, merge, tag, branch
  create/delete, rebase, amend) without explicit user instruction in that turn.
- Completing an implementation or test run does not imply a commit should
  follow.
- Before any force-push or history rewrite (`push --force`/`--force-with-lease`,
  `reset --hard` on a shared branch, rebase of pushed commits), print the exact
  refs and remote(s) being force-pushed and state plainly that the operation is
  irreversible for anyone else who has already pulled. Only proceed after that
  is acknowledged in the same turn.

## Code Review — Scope by Change Type

When reviewing a pull request, adjust depth based on what changed:

- **Code changes** (`src/**`, `tests/**`): full review — logic, security,
  API surface, test coverage, Product Invariants.
- **Documentation only** (`docs/**`, `mkdocs.yml`, `*.md` outside `src/` and
  `tests/`): lightweight review — check for broken internal links, missing or
  incorrect nav entries in `mkdocs.yml`, and factual accuracy. Skip
  code-style, naming, and architecture concerns.
- **Mixed PRs**: apply the appropriate depth per file. Do not let a doc change
  dilute scrutiny of code changes in the same PR.
