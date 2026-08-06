# Browse the Movies & Series Catalog

Xtream providers with **Movies** and/or **Series** enabled expose VOD and series categories separate from live channels. **Catalog** (Channels → Catalog) is where you browse what a provider actually has before deciding whether that content is worth including — it doesn't publish or filter anything itself.

For how catalog browsing relates to Channel Mapping and published output, see [Concepts > Channels and Groups](../concepts/channels-and-groups.md#movies-and-series-catalog); this page is the how-to.

## Choose a profile

Open **Catalog** and select a **Profile** from the dropdown, same as other Channels pages. The category list reflects providers linked to that profile.

If no categories appear, check that:

- the provider's **Movies** or **Series** toggle is enabled (Providers → provider → edit), and
- the provider has completed at least one refresh since that toggle was turned on.

Xtream providers only fetch catalog categories when the corresponding content type is enabled — there's nothing to browse for a disabled type until you enable it and refresh.

## Find a category or title

The Catalog page combines two searches:

- The left field filters the **category list** by category or provider name.
- The right field searches **titles** across every category currently matching the left filter and the Movies/Series chips.

The **Movies** and **Series** chips toggle which content type is shown; click a chip again to clear the filter back to both types (or click both to hide everything). With no title search, the page lists categories — name, type, discovered item count, distinct title count, provider, last-seen, and refresh status. With a title search, it lists matching titles instead, each showing its category and provider so identically named movies and series stay distinguishable.

Only movie and series (parent) titles are searched — episode titles inside a series are not indexed and won't match.

## Open a category

Click a category name to see its titles, paginated and searchable. A **Series** category lists distinct series with their discovered episode count, not one row per episode; opening a series title from there shows its full season/episode breakdown.

## Inspect a title

Click a title to open its detail page:

- Poster artwork (when the provider supplies it), fetched on demand through M3Undle rather than linked directly to the provider.
- Plot, genre, release date, director, cast, rating, and duration, when the provider supplies them.
- For series, a season-by-season, episode-by-episode list with episode plots and air dates where available.

If a provider doesn't return usable metadata, the page falls back to what was indexed (title, category, provider) and shows a notice explaining that metadata is unavailable — this isn't an error, just a provider limitation.

## What this page does not do

- **It doesn't change what's published.** There's no Build Output step for catalog content — a provider's Movies/Series toggle controls the catalog output your Xtream-compatible clients see. Category and title browsing here is inspection only.
- **It doesn't support per-category or per-title filtering.** You can't exclude one Movies or Series category while keeping others; the only lever today is the provider-level Movies/Series toggle.
- **It doesn't play content.** This is a browse and metadata surface, not a player.

## Where to go next

- [Concepts > Channels and Groups](../concepts/channels-and-groups.md) — how catalog browsing fits alongside live channel mapping.
- [Jellyfin client guide](../clients/jellyfin.md) — what publishing Movies/Series through the Xtream-compatible API does and doesn't get you in Jellyfin.
