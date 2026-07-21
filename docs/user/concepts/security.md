# Security

M3Undle has two independent locks. Mixing them up is the most common security misconfiguration — enabling one does **not** enable the other, and most installs actually want both.

| | Protects | Turned on with |
|---|---|---|
| **UI Authentication** | The Blazor web interface itself — the screens you use to manage M3Undle | Environment variable: [`M3UNDLE_AUTH_ENABLED`](../reference/environment-variables.md#optional-authentication) (requires a container restart) |
| **Endpoint Credentials** | What your media clients connect to — M3U, XMLTV, streams, Xtream, HDHomeRun | **Settings → Security → Endpoint Credentials** in the UI (takes effect immediately, no restart) |

If you only set `M3UNDLE_AUTH_ENABLED=true`, your provider playlist and streams are still wide open to anyone who has the URL. If you only enable Endpoint Credentials, anyone who can reach the server can still open the admin UI and change your configuration. **For an instance reachable beyond a trusted LAN, set up both.**

## FAQ

**I want to require a login to access the M3Undle web UI.**
Set `M3UNDLE_AUTH_ENABLED=true` (and `M3UNDLE_ADMIN_PASSWORD` on first run) in your `compose.yaml` or `.env` file and restart the container. No UI toggle exists for this. See [UI Authentication](#ui-authentication) below.

**I want to secure my M3U/XMLTV/stream endpoints so a stranger with the URL can't see or watch my lineup.**
No environment variable needed. Go to **Settings → Security → Endpoint Credentials**, set a username and password, turn on **Enable credential enforcement**, and select **Apply**. Takes effect immediately, no restart. See [Endpoint Credentials](#endpoint-credentials) below.

**I want to secure the Xtream endpoints specifically (TiviMate, IPTV Smarters, etc.).**
Same switch as above — Endpoint Credentials covers Xtream along with M3U/XMLTV/streams; there's no separate Xtream-only credential. If you'd rather turn Xtream off entirely instead of securing it, use **Enable Xtream protocol** under **Advanced Options** — disabling it makes every Xtream route return `404` regardless of credential state.

**I want to secure HDHomeRun.**
Endpoint Credentials protects HDHomeRun too, even though the on-screen list doesn't name it explicitly — see [HDHomeRun gets both controls](#hdhomerun-gets-both-controls) below. For an extra layer, also restrict it with **Settings → HDHomeRun → Allowed Networks**.

**I want both a UI login and secured client endpoints.**
Do both steps above — they're independent switches, and turning on one does not turn on the other. This is the recommended setup for anything reachable outside a single trusted LAN.

**I forgot my admin password.**
See the recovery steps in [Environment Variables → Authentication](../reference/environment-variables.md#optional-authentication).

## UI Authentication

Open **Settings → Security** to see its current state. When UI Authentication is disabled, the page displays a warning that the interface is open to anyone who can reach the server, along with instructions to "configure ASP.NET Core Identity to enable it" — that message doesn't name the actual switch. The real one is the `M3UNDLE_AUTH_ENABLED` environment variable, set in your `compose.yaml` or `.env` file, not from a page in the UI. See [Environment Variables → Authentication](../reference/environment-variables.md#optional-authentication) for the full variable list, including admin username/password and the password-recovery workflow, and restart the container after changing it.

## Endpoint Credentials

The **Endpoint Credentials** section configures one username and password for media access. The UI identifies these protected consumers:

- M3U
- XMLTV
- streams
- Xtream-compatible players such as TiviMate and IPTV Smarters

Enter **Username** and **Password**, enable **Enable credential enforcement**, and select **Apply**. The password is stored as a hash; its plaintext is not saved.

Leave enforcement disabled only when the instance is protected by a trusted network or another appropriate access boundary. Enabling this does **not** touch UI Authentication above — see the table at the top of this page.

## Advanced options

Expand **Advanced Options** to configure:

- **Virtual Tuner ID**, used by players that support HDHomeRun tuner reuse. The UI recommends changing it only when multiple M3Undle instances require unique IDs.
- **Enable Xtream protocol**. When disabled, the UI states that all Xtream routes return `404`, regardless of credential state.

## HDHomeRun gets both controls

The Security page's visible list doesn't name HDHomeRun explicitly, but HDHomeRun endpoints (discovery, lineup, tuning) are actually covered by **both** protections: the same endpoint-credential enforcement as M3U/XMLTV/streams/Xtream when it's enabled, *and* the separate network restriction under **Settings → HDHomeRun → Allowed Networks**. Don't assume HDHomeRun is exempt from endpoint credentials just because the Security page's UI text doesn't spell it out — enabling credential enforcement protects it too. Use both screens together when exposing M3Undle beyond a single trusted LAN, since Allowed Networks is a separate, additional layer, not a replacement.

## Verify after a change

After applying endpoint credentials, test the same URL your client uses. An unauthenticated M3U, XMLTV, stream, or Xtream request should no longer behave like it did with enforcement disabled. Then configure the client with the M3Undle endpoint credential—not the upstream provider's username and password.

For how these controls are actually implemented — how credentials are hashed, how provider passwords are encrypted rather than hashed, and how the same filter covers every client route including HDHomeRun — see [Profile and Security Model](../architecture/profile-and-security-model.md).
