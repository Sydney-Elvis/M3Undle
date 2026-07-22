# Profile and Security Model

[Profiles and Users](../concepts/profiles-and-users.md) and [Security](../concepts/security.md) cover the day-to-day mechanics. This page goes one level deeper: how a profile actually scopes what gets published, how activation switching works, and the concrete mechanisms — not just the settings-page description — behind each of M3Undle's security controls.

## Profiles: scope, not identity

A profile is a named bundle of:

- one or more **linked providers**, each with a priority (used to order refresh work, not to merge or fail over between them — each profile still publishes each linked provider's channels independently),
- the **group and channel decisions** (included/excluded/pending, custom groups, numbering ranges) that determine what that profile's snapshot contains,
- its own **EPG channel mappings**, and
- its own **refresh schedule override**, if you've set one.

Exactly one profile is **active** at a time, enforced at the database level — activating a profile deactivates every other profile in the same operation, not as a side effect you have to remember to trigger yourself. The active profile is what the fixed-name client endpoints (`/m3u/m3undle.m3u`, `/xmltv/m3undle.xml`, `/stream/<streamKey>`, `/hdhr/*`) actually serve. A non-active profile still refreshes on its own schedule and keeps its own snapshot history — switching which profile is active is close to instant, since it's just changing which profile's already-built snapshot the fixed endpoints point at, not triggering a fresh build.

Named, per-profile output endpoints (so two profiles could be reachable at different URLs simultaneously, instead of one active profile at a time) are reserved in the schema (`profiles.output_name`) but not implemented — every profile publishes to the same fixed path when it's active.

## Two independent locks

M3Undle separates *who can configure the server* from *who can watch your channels*, and the two controls don't overlap:

| | Protects | Mechanism |
|---|---|---|
| **UI Authentication** | The Blazor admin interface | `M3UNDLE_AUTH_ENABLED` environment variable, checked at the ASP.NET Core middleware level; requires a restart to change |
| **Endpoint Credentials** | M3U, XMLTV, streams, Xtream, HDHomeRun | A username/password pair configured at runtime in **Settings → Security**, enforced per-request by an endpoint filter |

These aren't two views onto the same check — they're structurally different mechanisms (framework-level auth middleware vs. a request filter attached to specific route groups), which is exactly why enabling one has no effect on the other. See [Security](../concepts/security.md) for the operator-facing walkthrough of when you need which.

## The client endpoint filter

Every client-facing route — M3U, XMLTV, stream relay, Xtream, and HDHomeRun alike — is registered through the same shared route-group extension, which attaches one endpoint filter ahead of the actual handler. That filter resolves credentials (when enforcement is on) before the request reaches any route-specific logic, and rejects with `401` and a `WWW-Authenticate` challenge on missing or invalid credentials, or `503` if there's no active profile to serve yet.

Because HDHomeRun routes are registered through that same shared extension, **HDHomeRun gets endpoint-credential enforcement too** — it isn't a separate, unprotected surface, even though the Security page's own UI text doesn't name it explicitly. HDHomeRun additionally gets a second, independent layer on top: a network-based allow-list (**Settings → HDHomeRun → Allowed Networks**) checked by its own filter. The two layers are additive, not alternatives — see [Security → HDHomeRun gets both controls](../concepts/security.md#hdhomerun-gets-both-controls).

## How credentials are actually stored

Endpoint credential passwords, the UI admin password, and metrics tokens are never stored in a reversible form — each is run through ASP.NET Core Identity's password hasher (PBKDF2, a configurable iteration count) and only the hash is persisted. There is no code path that reads a stored credential back out as plaintext; verifying a login means hashing the attempt and comparing hashes, not decrypting anything.

Provider passwords and downstream-integration API keys are a different case: those have to be *usable* later (M3Undle has to actually authenticate to your Xtream panel or webhook target), so they're **encrypted, not hashed** — reversible by design, using AES-256-GCM with a key you control:

- The encryption key comes from `M3UNDLE_ENCRYPTION_KEY` (or `M3UNDLE_ENCRYPTION_KEYS` for a rotatable multi-key ring — see [Manage Providers → Rotating the encryption key](../guides/manage-providers.md#rotating-the-encryption-key)). Without one configured, M3Undle simply can't store a new encrypted secret.
- Each ciphertext is tagged with the ID of the key that encrypted it, so old secrets stay decryptable after you rotate to a new active key — you only lose access to a secret if you remove its specific key ID from the ring entirely.
- AES-GCM's authentication tag means a ciphertext decrypted with the wrong key fails loudly (a clear error) rather than silently returning garbage — there's no ambiguous "did that actually work" state.

## What's schema-present but not active

A few database tables exist for a future, more capable profile/channel model but aren't populated or read by the current publish pipeline — worth knowing so a look at the schema doesn't suggest functionality that isn't actually there:

- **`canonical_channels`** / **`channel_sources`** — a stable, provider-independent channel identity with ranked fallback sources. Today, stream keys and channel identity are derived directly from provider-channel data (see [System Overview](system-overview.md#core-concepts)), not from these tables.
- **`profiles.merge_mode`** — always `single` in current behavior; `merged` and `redundancy-ready` are schema-valid but have no implemented behavior.
- **`profile_providers.priority`** — currently just ordering metadata for refresh sequencing, not a user-facing "prefer this provider" behavior.

None of this is a roadmap commitment — it's schema built ahead of the features that would use it, left in place rather than stripped out.
