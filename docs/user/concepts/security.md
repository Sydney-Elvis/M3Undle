# Security

M3Undle separates access to the web interface from credentials used by media clients. Open **Settings → Security** to see their current state.

## UI Authentication

The **UI Authentication** section reports whether the web interface requires sign-in. On the observed instance it was disabled, and the page warned that the interface was open to anyone who could reach the server.

The screen directs administrators to configure ASP.NET Core Identity to enable UI authentication. It does not offer UI-login fields in this settings panel.

## Endpoint Credentials

The **Endpoint Credentials** section configures one username and password for media access. The UI identifies these protected consumers:

- M3U
- XMLTV
- streams
- Xtream-compatible players such as TiviMate and IPTV Smarters

Enter **Username** and **Password**, enable **Enable credential enforcement**, and select **Apply**. The password is stored as a hash; its plaintext is not saved.

Leave enforcement disabled only when the instance is protected by a trusted network or another appropriate access boundary. UI authentication and endpoint credentials are independent: enabling one does not imply that the other is enabled.

## Advanced options

Expand **Advanced Options** to configure:

- **Virtual Tuner ID**, used by players that support HDHomeRun tuner reuse. The UI recommends changing it only when multiple M3Undle instances require unique IDs.
- **Enable Xtream protocol**. When disabled, the UI states that all Xtream routes return `404`, regardless of credential state.

## HDHomeRun gets both controls

The Security page's visible list doesn't name HDHomeRun explicitly, but HDHomeRun endpoints (discovery, lineup, tuning) are actually covered by **both** protections: the same endpoint-credential enforcement as M3U/XMLTV/streams/Xtream when it's enabled, *and* the separate network restriction under **Settings → HDHomeRun → Allowed Networks**. Don't assume HDHomeRun is exempt from endpoint credentials just because the Security page's UI text doesn't spell it out — enabling credential enforcement protects it too. Use both screens together when exposing M3Undle beyond a single trusted LAN, since Allowed Networks is a separate, additional layer, not a replacement.

## Verify after a change

After applying endpoint credentials, test the same URL your client uses. An unauthenticated M3U, XMLTV, stream, or Xtream request should no longer behave like it did with enforcement disabled. Then configure the client with the M3Undle endpoint credential—not the upstream provider's username and password.

## Verification boundary

The settings and explanatory text were inspected directly. Credential enforcement was not enabled on the shared instance, so authenticated request formats and failure response codes were not exercised.
