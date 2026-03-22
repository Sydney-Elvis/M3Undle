# Alpha 4 Validation Checklist

Legend: `[ ]` not run | `[x]` passed | `[!]` failed / investigate

## Stream Proxy

- [x] Shared live routes (`/live`, `/stream`, `/tune`, `/hdhr/tune`) reuse one upstream session per live channel
- [x] VOD-style routes (`/movie`, `/vod`, `/series`) stay on direct relay
- [x] Late joiners receive buffered data without breaking the active session
- [x] Slow subscribers are evicted without collapsing the shared session
- [x] Reconnect behavior works after upstream stalls
- [x] Provider stream limits still reject new sessions correctly

## Stream Settings UI

- [x] Settings page loads saved stream settings and current applied runtime settings distinctly
- [x] Saving changed stream settings marks restart required
- [x] Invalid values are rejected with visible validation errors
- [x] Runtime behavior does not change until restart
- [x] `Restart M3Undle` stops the app process cleanly
- [x] After restart, pending restart warning is cleared
- [x] After restart, saved settings are applied to runtime behavior

## HDHomeRun HTTP API

- [x] `/hdhr/discover.json` returns stable device identity and correct `TunerCount`
- [x] `/hdhr/lineup.json` returns only live channels with stable guide numbers and tune URLs
- [x] `/hdhr/lineup.xml` matches `/hdhr/lineup.json`
- [x] `/hdhr/lineup.m3u` matches `/hdhr/lineup.json`
- [x] `/hdhr/lineup_status.json` reports lineup readiness when an active snapshot exists
- [x] `/hdhr/device.xml` loads successfully from a client
- [x] Legacy aliases (`/discover.json`, `/lineup.json`, `/lineup.xml`, `/lineup.m3u`, `/lineup_status.json`, `/device.xml`, `/tune/<streamKey>`) behave the same as `/hdhr/*`

## HDHomeRun Tuner Semantics

- [x] A first `/hdhr/tune/<streamKey>` request succeeds
- [x] A second request on the same `VirtualTunerId` retunes/replaces the prior subscriber instead of consuming another tuner slot
- [x] Distinct `VirtualTunerId` values can consume different tuner slots up to configured `TunerCount`
- [x] Requests beyond configured `TunerCount` are rejected
- [x] Disconnecting playback releases the tuner slot
- [x] Restarting M3Undle clears any active tuner leases
- [x] Generic `/stream/<streamKey>` requests are not blocked by HDHomeRun tuner enforcement

## Guide / EPG

- [x] Multiple EPG sources can be fetched and parsed successfully
- [x] Source priority affects merged guide selection as expected
- [x] Duplicate programme/channel entries are not duplicated in the published XMLTV
- [x] Channel mappings persist and affect the published guide
- [ ] Published `/xmltv/m3undle.xml` matches the active lineup
