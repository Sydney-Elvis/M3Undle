# Contributing

Thanks for your interest in contributing to M3Undle.

## Development

* [docs/dev/README.md](docs/dev/README.md) — start here: build/run from source, project roadmap, architecture and design specs, release process

## Before submitting

* Open an issue to discuss significant changes
* Keep changes focused and minimal

## Guidelines

* Follow existing code style and patterns
* Avoid large refactors unless discussed first
* Prefer stability and readability over clever solutions
* Read [docs/dev/PROJECT_BOUNDARIES.md](docs/dev/PROJECT_BOUNDARIES.md) before changing anything that crosses the Core/Web/CLI boundary, or that touches one of its listed Product Invariants
* For UI changes under `src/M3Undle.Web/Components`, follow [docs/dev/GUI_CONSISTENCY_CONTRACT.md](docs/dev/GUI_CONSISTENCY_CONTRACT.md)
* For new REST endpoints: use minimal APIs, not controller classes, and return `TypedResults`
* Use `IServiceScopeFactory`/`CreateAsyncScope()` when a background service needs scoped services
* Use `SaveChangesAsync(CancellationToken.None)` for error/failure-state writes that must survive cancellation
* Avoid `//` comments unless the logic is genuinely non-obvious; don't add docstrings to internal code

## Testing

* Ensure the application builds and runs
* Do not introduce breaking changes without discussion
* Run `dotnet build M3Undle.slnx` and the relevant tests for your change; for repo-wide changes, run `dotnet test --solution M3Undle.slnx`
* Leave the repo in a clean state — no new build/test warnings, and don't submit a PR expecting someone else to make it green
* If a change to a service constructor, interface, or public API breaks existing tests, update those tests as part of the same PR

## Notes

This project is currently evolving quickly. Not all contributions may be accepted, especially large or architectural changes.

M3Undle is currently in beta, which is scoped to bug fixes, security and stability hardening, documentation, and gaps identified during alpha — functionality an existing v1 feature needs in order to work safely or as documented (for example, backup/restore) — rather than new capabilities. Open an issue first if you want to propose something larger.
