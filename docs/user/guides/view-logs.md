# View Logs

The **Logs** page streams M3Undle's application log live in the browser — useful for watching what happens in real time while you reproduce a problem, without shelling into the server.

## Reading the live log

Each line shows a timestamp, a level badge (the first three letters of the level, uppercased: FAT / ERR / WAR / INF / DEB / VER), an optional event category in brackets (e.g. `[Refresh]`, `[HDHR]`), and the message. Exceptions render indented underneath their line in the error color.

The page auto-scrolls to the newest entry as it arrives. If you scroll up to read older lines, auto-scroll pauses automatically — a **Resume scrolling** button appears so you can jump back to the live edge deliberately instead of fighting the page while you're reading.

## Filtering

- **Search** — filters by any text across the timestamp, level, category, message, and exception. Multiple words are treated as separate required terms (all must match), not a literal phrase.
- **Level toggles** — one chip for each of the six levels (Fatal/Error/Warning/Information/Debug/Verbose), shown from the moment the page loads regardless of whether that level has actually appeared yet — a level with nothing logged just shows a count of 0. Click a chip to hide or show that level; the count next to it always reflects the current search filter too.
- The **shown** count at the top reflects both filters combined.

## What you're looking at: buffer sizes and persistence

The Logs page is a live view backed by a small in-memory buffer on the server (the last 200 entries at the time you load the page), which then keeps streaming new entries in as they happen, capped at 500 in the browser at once. It is **not** the full application history and resets when M3Undle restarts.

For anything you need to keep or search after a restart, the real log lives on disk: M3Undle writes rolling daily log files to `/data/logs/app-*.log` inside the container (10 MB per file by default, 31 files retained). That's the file to pull if you're filing a bug report that needs more history than this page can show, or if you want to `grep` logs from the host instead of the browser.

## Verification boundary

The live-streaming behavior, filter controls, level chips, and auto-scroll/Resume behavior were observed on a live v1.0.0-beta.6 instance (149 entries shown at the time, spanning a full refresh cycle) — that instance's Debug and Verbose chips showed 0 with no entries of either level present, consistent with the chips being pre-seeded rather than built from what's been seen. The exact badge abbreviation logic (`level[..3]`, so Debug → DEB and Verbose → VER) and the pre-seeded six-level chip list were confirmed against `Logs.razor` rather than read off the screen. The in-memory buffer size (200 entries backfilled, 500-entry browser cap) and the on-disk log path/rotation settings were verified against `Program.cs` and `InMemoryLogStore.cs` rather than the UI, since neither is visible on the page itself.
