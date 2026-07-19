# Manage Profiles

For what a profile *is* and how refresh scheduling/credentials relate to it, see [Concepts > Profiles and Users](../concepts/profiles-and-users.md) first. This page is the how-to for the **Profiles** list and the individual profile detail page.

## The Profiles list

Each profile is a card showing its name, a **Published** / **Degraded** / **No output** health chip, an **Active** badge on the one profile currently serving the shared endpoints, published content counts (live/movies/series), the providers linked to it, and inline alerts for anything needing attention — groups removed from the provider, new groups or channels pending review, or a published-but-empty lineup. Active-profile cards also show which endpoint types are live (M3U, and Xtream/HDHomeRun if enabled).

Click anywhere on a card to open that profile's detail page.

## Creating a profile

Select **New Profile** and give it a unique name. A fresh profile starts with no providers linked and no output — link a provider (see [Add the First Provider](../getting-started/add-first-provider.md)) and build a lineup before it's useful.

## Activating a profile

Non-active profiles show a **Set Active** button. Activating a profile makes it the one served by the shared, unqualified endpoints (`/m3u/m3undle.m3u`, `/xmltv/m3undle.xml`) — every client pointed at those URLs immediately sees the newly active profile's lineup instead. **Set Active** is disabled until the profile has at least one provider linked.

Only one profile is active at a time; see [Profiles and Users](../concepts/profiles-and-users.md#one-active-profile) for why, and for the per-profile-endpoint feature that isn't available yet.

## Deleting a profile

The delete action removes the profile and everything tied to it — group filters, channel selections, custom groups, canonical channels, stream keys, and published snapshots. It's blocked while a refresh is in progress for that profile. There's no undo; if you're unsure, leave the profile disabled rather than deleting it.

## The profile detail page

Opening a profile shows:

- **Identity** — name, creation date, and the earliest provider expiry among its linked providers (if any is within 30 days, it's called out in warning/error color).
- **Published Output** — live/movie/series counts and last-published time, or a prompt to link a provider / trigger a refresh if there's nothing published yet. The same removed/pending-review/empty-output alerts as the list view appear here too.
- **Providers** — a table of linked providers with priority (lower number wins when providers overlap), expiry, and current fetch status. A **Manage** link jumps to the Providers page. Click a row to open that provider.
- **Refresh Schedule** — see below.
- **Published History** — every past snapshot build for this profile: date, status (active / archived / failed), live/movie/series counts, and the size of the change versus the previous snapshot. This is where to check what a given refresh actually changed, or find when a channel count shifted.

Three actions sit at the top of the page: **Map Channels** and **View Channels** jump straight into that profile's lineup work (see [Build a Lineup](build-a-lineup.md)), and **Delete** removes the profile as described above.

## Refresh Schedule (per profile)

By default a profile inherits the global refresh interval from **Settings → Schedule**. Toggle **Use profile-specific schedule** to override it with its own interval, or set it to manual-only. If your override matches the global default exactly, the page warns you that no meaningful override will actually be saved — there's no reason to store a redundant value.

This only visibly changes anything once the profile is active: refresh scheduling is a property of whichever profile is currently serving, so a non-active profile's schedule section is shown for reference but doesn't drive an actual timer until you activate it.
