# Administrator Notifications

M3Undle can tell you when something needs attention by **Matrix** message or **email (SMTP)**. Everything is configured on one page, **Settings → Notifications**, and each notification can use a different method — or none.

These are alerts for the person who runs M3Undle. They are separate from the in-app event panel (which keeps working exactly as before), from the account emails Identity might send, and from [Downstream Integrations](downstream-integrations.md), which are commands sent to Jellyfin, Emby or a webhook.

!!! note "Accepted is not received"
    M3Undle can only know that the Matrix homeserver or SMTP server **accepted** a message. It cannot know that anyone saw it. The history says *Accepted*, never *Delivered*, and a test message is labelled the same way.

## What you can be told about

Every row defaults to **Off**. Rows that describe a problem that can clear on its own send one **recovery** message to the method that received the alert, and can repeat as a **reminder** while the problem remains.

| Notification | Sent when | Follow-ups |
| --- | --- | --- |
| EPG source fetch failing | A guide source's real fetches keep failing past the failure delay | Recovery, reminders |
| EPG refresh overdue | A source that M3Undle itself schedules was not checked by its deadline plus the grace period | Recovery, reminders |
| EPG future coverage insufficient | Too few relevant channels have guide data for the coming hours, even if fetching works | Recovery, reminders |
| Provider fetch failing | A provider playlist refresh failed | Recovery, reminders |
| Sustained stream instability | A channel had at least three upstream failures in five minutes that stayed unresolved for two minutes | Recovery, reminders |
| Downstream refresh failing | The last command sent to a Jellyfin, Emby or webhook integration failed | Recovery, reminders |
| Breaking lineup change | A published lineup changed by more than 20% | One message |
| Failed sign-in attempts | Failed sign-ins, summarised per account in five-minute windows | One summary per window |
| Account locked | An account was locked after repeated failed sign-ins | One message |
| Application restarted | M3Undle started | One message |
| Database migrations applied | Migrations were applied during startup | One message |
| Series synchronization completed | A series sync run finished (can be noisy) | One message |

New-channel review is **not** on this page. It stays in the Channels review workflow.

### Privacy of sign-in alerts

Sign-in messages contain a count and a short account reference only. The name that was typed, the client address and the browser are never included or stored.

## Before you start

- Set `M3UNDLE_ENCRYPTION_KEY` (or `M3UNDLE_ENCRYPTION_KEYS`) so the SMTP password and Matrix token can be stored encrypted. Without a key, secrets are refused rather than stored in the clear. See [Environment Variables](../reference/environment-variables.md).
- Optionally set `M3UNDLE_EXTERNAL_BASE_URL` so messages can link back to M3Undle. If it is not set, messages simply carry no link — an internal or container address is never put in a message.

## Set up email

Open **Settings → Notifications → Methods → Email (SMTP)**.

| Field | Notes |
| --- | --- |
| SMTP server | A host name or address only — no `smtp://` and no path |
| Port | 587 for STARTTLS, 465 for TLS on connect, or whatever your server uses |
| Encryption | **Required STARTTLS** or **TLS on connect**. There is no plaintext mode, and M3Undle never falls back from TLS |
| Authentication | **Username and password** (including app passwords) or **None** for a trusted relay |
| Password | Used exactly as typed, spaces included. Leave it blank when editing to keep the saved one; tick **Remove the saved password** to clear it |
| Sender address / name | The `From` address, for example `m3undle@example.org` |
| Administrator mailboxes | One address per line, up to 10. Each address gets **its own message** |

A distribution list counts as a single mailbox. M3Undle sees that the list accepted the message, not who read it.

The server's certificate is checked like any browser would check it: it must chain to a trusted authority, match the server name, be in date, and **not be revoked**. Revocation is checked in the normal way, so a certificate authority that publishes no revocation information (no CRL or OCSP address) is rejected. A private authority must be installed in the container's trust store and publish a CRL, or you must use a certificate from a public authority.

## Set up Matrix

1. Create a bot account on your homeserver and log in as it normally to obtain an **access token**. Use a normal login, so the token belongs to a device.
2. Create a **private, unencrypted** room and invite the bot. Accept the invitation as the bot.
3. Open **Settings → Notifications → Methods → Matrix** and enter the **homeserver URL** (HTTPS), the **room ID** (it starts with `!`, for example `!abc123:example.org`; newer room versions have no `:server` part — never a `#alias`) and the token.

Messages are plain-text `m.notice` events. M3Undle does not create rooms, read the room, accept commands, or work in encrypted rooms: the test fails, and any send is refused, if the room is — or becomes — encrypted.

When you test, M3Undle looks up the bot's identity and device, checks that it has joined the room and may post, and that the room is not encrypted. Redirects from the homeserver are never followed, because the request carries your token.

## Save, test, enable

1. **Save.** Saving never sends anything and never enables the method.
2. **Test saved settings.** This sends a real test message to **every** target (each mailbox, or the room) using the saved settings. The result applies only to the exact saved revision, so editing while a slow test runs cannot mark the new settings as tested. If only some targets accept it the status is *Partly tested* and the method cannot be enabled.
3. **Enable.** Only a successfully tested method can be enabled.

Editing a method later switches it off and clears its test, so the sequence always repeats. Tests are limited to one per minute per method.

Then choose **Off, Matrix or Email** for each notification in the table and turn on **Send notifications**. A notification is never silently sent by a different method than the one you chose.

## What happens to messages

Messages are queued durably, so a restart does not lose them, and sent in the background. A slow or unreachable mail server or homeserver never delays guide refreshes, publishing or streaming, and Matrix and email progress independently.

- **Retrying.** Temporary problems are retried with growing delays (30 seconds up to 30 minutes, with some randomness), up to eight attempts. A server's own retry instruction is honoured.
- **Failed.** A rejection, a bad password, or exhausted retries stops automatic retries. Use **Retry** on the history row to start a new attempt on the same message.
- **Uncertain.** If the connection broke after the message body was sent but before the server's final reply, the server may already have accepted it. M3Undle does not retry that automatically, because it could arrive twice. **Retry** asks you to acknowledge the duplicate risk first. A message's `Message-ID` stays the same, but that is for correlation, not de-duplication.
- **Dismiss** removes a failed or uncertain record from the problem list. It does not change the health of the underlying problem.
- **Not sent** means the message was no longer current — the problem cleared first, the method or route changed, or sending was paused. Old messages are never re-addressed to a new recipient.

History is kept for 30 days by default. At most 10,000 unfinished messages are held; if that ceiling is ever reached, new ones are rejected with a visible warning rather than dropped silently.

## Thresholds

Defaults can be changed under **Timing and thresholds** and apply across notifications.

| Setting | Default | Range |
| --- | --- | --- |
| Failure delay | 10 minutes | 0–1,440 minutes |
| Overdue grace | 15 minutes | 0–1,440 minutes |
| Reminder interval | 6 hours | 1–168 hours |
| Coverage warning horizon / percentage | 12 hours / below 90% | 1–168 hours / 1–100% |
| Coverage recovery horizon / percentage | 14 hours / at least 95%, on two consecutive checks | at least the warning values |
| Allowed gap between programmes | 30 minutes | 0–120 minutes |
| History retention | 30 days | 1–365 days |

A problem that clears before the failure delay is never announced and never has a recovery message. Stream instability uses fixed thresholds (three failures in five minutes, two minutes sustained, two minutes of healthy output to recover). A guide source with **no** usable programme data at all for its relevant channels is reported as critical immediately, without waiting for the failure delay.

## Backups, restore and key rotation

- Settings archives and portable backups keep your methods, mailboxes and routes as **intent**. Passwords and tokens stay encrypted under your key.
- A restored or imported instance **does not send** until you re-test each method and turn sending on again. Test results, queued messages, history and open problems are never carried over, and the restored instance does not announce its own start.
- Rotating the encryption key (see [Manage Providers](manage-providers.md)) re-encrypts the SMTP password and Matrix token too. That does not change what a destination is, so it invalidates no test and sends nothing again.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| *Test failed (tls_failed)* | The certificate is not trusted, has the wrong name, is expired or revoked, or publishes no revocation information. The reason is written to the log as `SMTP TLS validation failed` |
| *tls_unsupported* | The server does not offer STARTTLS on that port. Use the TLS-on-connect port, or enable STARTTLS on the server |
| *auth_failed* | Wrong username or password, or the server requires an app password |
| *smtp_rejected* | The server refused a mailbox or the message. The failed row names the mailbox |
| *Matrix: room_encrypted* | Use an unencrypted private room |
| *Matrix: not_in_room / no_permission* | The bot has not joined the room, or may not post in it |
| *Matrix: no_device* | The token is not tied to a device. Log in normally to obtain one |
| *Matrix: insecure_http* | The homeserver must use HTTPS |
| Nothing is sent but routes are set | Sending is off or paused, the method is disabled or untested, or the instance was just restored and is waiting for you to turn sending back on |
| Row says *Waiting* | Hover the chip: it names what is missing |
