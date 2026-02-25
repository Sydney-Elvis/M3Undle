# M3Undle CLI

The M3Undle CLI is designed to make massive provider catalogs manageable.

Many providers deliver playlists containing 10,000–50,000+ channels across multiple regions, languages, and temporary event feeds. The CLI helps you inspect, filter, and generate controlled output from those catalogs in a repeatable and predictable way.

It is useful for:

- Reducing large playlists to a curated subset
- Discovering and reviewing provider groups
- Generating clean M3U files
- Filtering XMLTV data
- Replacing ad-hoc shell or Python scripts
- Automating repeatable playlist workflows

The CLI is deterministic:
Same inputs → same outputs.
No hidden reordering. No unexpected renumbering.

---

## Installation

Build from source:

    dotnet build

Run:

    dotnet run --project src/M3Undle.Cli

(Adjust path as needed.)

---

## Environment Configuration

Provider credentials can be stored in a `.env` file:

    PROVIDER_BASE_URL=http://provider.example.com
    PROVIDER_USERNAME=your_username
    PROVIDER_PASSWORD=your_password

Environment variables are supported for secure automation workflows.

---

## Core Workflow

The CLI follows a simple model:

1. Fetch provider data
2. Inspect available groups
3. Define inclusion rules
4. Generate filtered output

Each step is explicit and repeatable.

---

## Common Commands

### Fetch Provider Playlist

    bndl fetch

Downloads and stores the raw provider playlist locally.

---

### List Groups

    bndl groups

Displays available provider groups so you can understand the catalog before filtering.

---

### Generate Filtered M3U

    bndl build-m3u --include "USA | News" --include "USA | Sports"

Generates a filtered M3U file based on selected groups.

---

### Generate Filtered XMLTV

    bndl build-xmltv --include "USA | News"

Filters XMLTV data to match the included channels.

---

## Filtering Philosophy

The CLI emphasizes clarity:

- Group-based inclusion
- Explicit allow-lists
- Repeatable generation
- No automatic reclassification
- No implicit channel movement

You decide what stays.
Everything else is excluded.

---

## When to Use the CLI

The CLI is ideal when:

- You prefer file-based workflows
- You want automation via cron or scripts
- You are testing or evaluating a provider
- You want to reduce a large playlist to something manageable

For web-based configuration, snapshot publishing, and HTTP endpoints, see `docs/SERVICE.md`.
