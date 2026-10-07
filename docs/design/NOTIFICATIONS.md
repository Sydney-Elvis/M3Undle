# Administrator Notifications — Design

Optional SMTP and Matrix alerts for the person who runs M3Undle, selected per notification on one Settings page. This document describes the shipped behaviour. User-facing instructions are in the [Administrator Notifications guide](../user/guides/notifications.md); the schema is in [DB_SCHEMA.md](DB_SCHEMA.md#notifications).

## Principles

- **Optional and inert by default.** On upgrade and on a new install, sending is off, both methods are disabled and unverified, and every row is Off.
- **Capture is atomic with the business outcome; sending never is.** Producers stage observations/occurrences in the same `SaveChanges` as the result they describe. No producer waits on a transport, so an outage of either method cannot delay refresh, publishing or streaming.
- **At-least-once with an honest boundary.** A transport result of *Accepted* means the server accepted the message, nothing more. When acceptance may already have happened (SMTP final reply lost) the delivery is **Uncertain** and is never retried automatically.
- **Identity is explicit.** Routing, incident health and deliveries are keyed by stable IDs, never by SMTP subjects, Matrix message types or UI event rows. Dismissing or expiring an in-app event changes nothing here.
- **Secrets are write-only.** The SMTP password and Matrix token are encrypted with the existing key ring, appear only as presence flags in API/page contracts, and are decrypted only inside a short-lived provider configuration object that cannot serialize or print.

## Catalog

Twelve rows cover the thirteen existing system-event types (failure/recovery pairs are one row and one route) plus two new EPG conditions:

| Key | Lifecycle | Subject | Capture |
| --- | --- | --- | --- |
| `epg.fetch_failed` | incident | EPG source | `epg.source_check` observation per real check |
| `epg.refresh_overdue` | incident | EPG source | derived from the scheduler rules and `last_checked_utc` |
| `epg.coverage_insufficient` | incident | EPG source | `epg_notification_coverage` facts |
| `provider.fetch_failed` | incident | provider | `provider.fetch` observation with the fetch run |
| `stream.unstable` | incident | provider/channel | existing `stream_channel_health_events` + live session view |
| `downstream.refresh_failed` | incident | integration | `downstream.command` observation with the result |
| `lineup.breaking_change` | one-time | profile/snapshot | occurrence before the publish commit |
| `security.login_failed` | one-time (windowed) | account reference | `security.login` observations, summarised per closed 5-minute window |
| `security.account_locked` | one-time | account reference | occurrence per lockout end time |
| `system.restarted`, `system.migrations_applied` | one-time | boot | startup capture after migrations/restore |
| `series.sync_completed` | one-time | run | occurrence at run completion |

One-time occurrences are retained only if sending is allowed and the row is routed **at the business commit**. They are never replayed after a later enable.

## EPG evidence

Only a **real** upstream check is evidence: a download, a genuine HTTP 304, a local file read, or a failure. Reusing the on-disk cache because the cadence window has not elapsed (`EpgFetchDisposition.CacheReused`) records nothing — no timestamps, no fetch run, no failure cleared, no recovery event. A single `EpgSourceOutcomeRecorder` persists every real outcome (scheduled, build-only and manual); source columns, the fetch run and the observation commit together, and UI events are published afterwards, best effort.

- **Fetch failing**: opens after the failure delay of continuous failure; a real success or 304 resolves it.
- **Overdue**: `EpgCheckSchedule` states when the runtime itself promised a check (provider-linked source, enabled provider linked to an enabled profile, active profile on an interval schedule): the deadline is the last real check plus one schedule interval (plus the source cadence when longer). Manual schedules, standalone sources and pre-upgrade timestamps never produce *overdue*. Any completed real attempt resolves it independently of failure.
- **Coverage**: `EpgWindowCoverageAnalyzer` (Core) requires continuous guide data through the warning horizon with gaps up to the allowed gap, for the distinct XMLTV channels mapped from live channels in each profile's committed publication. With no relevant channels the state is *not monitored*; with none having any current or future data it is **critical** and opens without the failure delay; recovery needs the recovery thresholds on two consecutive evaluations.

## Incidents

`NotificationIncidentService` is idempotent and safe to rerun after a crash, configuration change or resume.

- At most one active incident per condition and subject; recurrence increments the generation.
- An incident becomes *sustained* after the delay (or immediately when critical), and only then produces an opening. A problem that clears first is never announced and has no recovery.
- Opening, reminder and recovery decisions are **per target and generation**. A recovery is queued only after that target accepted the opening, and never to an identity that did not. An opening whose acceptance is unknown never produces an unconditional recovery.
- Resolution suppresses unattempted opening/reminder deliveries; an opening still in flight at resolution gets its recovery once its acceptance is persisted. Removing or disabling the subject *closes* the incident without a success notice.
- Pausing suppresses unclaimed work with a reason; on resume, recoveries suppressed by the pause for accepted openings are restored. Route or configuration changes suppress unclaimed deliveries bound to the old revision and never re-address a payload.

## Delivery

States: `Pending → Claimed → Accepted | RetryScheduled | Failed | Uncertain`, plus `Suppressed` and `Dismissed`.

- Claims are atomic `UPDATE … WHERE revision = … AND state IN (…)`; the transport-start time is recorded separately before any I/O; no database transaction is open during I/O.
- Before claiming, the delivery's bound destination, configuration, identity and route are re-validated.
- Retries: at most eight attempts per cycle, 30 s base, 30 min cap, ±20 % jitter, a longer server instruction wins. Permanent and configuration failures stop automatic retries; an explicit **Retry** starts a new recorded cycle on the same delivery.
- An expired claim with no transport start returns to *Pending*. With a transport start it becomes *Uncertain* for SMTP, or is retried for a transport that declares idempotent retry (Matrix transaction IDs).
- One loop per provider kind, so one stuck transport only delays its own queue.

## Providers

**SMTP** (MailKit): required STARTTLS or TLS on connect, default certificate validation including revocation (a certificate authority that publishes no CRL/OCSP is rejected — this is deliberate), no downgrade or bypass, credentials sent only after TLS, one envelope recipient per delivery, stable `Message-ID`, header-injection stripping. The DATA phase is the outcome boundary: before the body is sent a failure is a clean retry; a definite 4xx/5xx is classified by status; a failure after the body was sent but before the final reply is **Uncertain**. A failure while closing the connection after acceptance never undoes it.

**Matrix**: plain-text `m.notice` into one private unencrypted room. HTTPS unless the isolated-lab runtime gate and the per-setup flag are both set. Redirects are never followed. The test resolves the bot identity and device, checks membership, power levels and room encryption, and the room is re-checked for encryption before every send. The transaction ID is derived from the delivery, room, device and payload so a retry is deduplicated by the homeserver and nothing else can reuse it. `Retry-After` is preferred over `retry_after_ms`. Room IDs are opaque: older versions end in `:server`, newer ones do not.

## Configuration and verification

Save never sends or enables. A test sends real messages to every target and is bound to the exact saved configuration revision; an edit during the test leaves the new revision unverified. Any change switches the destination off and clears its test. Enabling requires the current revision to be verified; a route can only select an enabled, verified method. Tests are limited to one per minute per method and retry/dismiss to ten per minute, with a server-wide ceiling that also holds when UI authentication is off.

## Access and API

`/api/v1/notifications` follows the existing `UiAccess` policy (so it honours optional UI authentication) and does not touch the anonymous compatibility routes. Every mutation must carry the `X-Requested-With` header, which a cross-site form cannot set — the same mechanism the backup upload uses. The Blazor page calls the same `NotificationsPageService`, so validation, revision conflicts and throttling are enforced once.

## Backup, restore and keys

Settings archive document version 2 carries methods, mailboxes, routes and policy as intent (version 1 imports as disabled and empty; unknown versions are rejected; the encrypted envelope version stays 2). Imports and restores clear verification and set `requires_activation`, discard operational state, and are inert until re-tested and resumed. Key rotation re-encrypts both credentials without changing any revision. A normal process restart is not a restore and preserves pending work.

## Limits and non-goals

No quiet hours or digests, no multiple named setups, no new-channel review email, no inbound Matrix commands, no end-to-end encryption, no OAuth for SMTP, no bounce or read tracking, no automatic cross-provider failover, and no monitoring of the application's own host.
