# Playnite: skip library sync when unlinked + client logging

**Status:** Approved for implementation planning  
**Module:** v1.2.0  
**Repo:** `pulse-playnite` only  
**Base branch:** `release 1.2.0`  
**Notion:** [Playnite: skip library sync when unlinked + client logging](https://www.notion.so/3567c180d8bf81ccb4a8faf76df90d61)

## Background

`pulse-server` is adding structured `games_sync` observability (including whether a bearer token was present). On the Playnite extension, **library sync** (`SyncAllGames`, `FlushPendingSyncQueues` / `ExecutePendingSyncWork`) currently calls `SyncGamesAsync` and delete-by-playnite **without** checking link state. **Game Activity import** already skips when unlinked with `PlayLog: skip GA import — not linked`.

If the bearer token is empty, requests go out **without** `Authorization`, which confuses server-side diagnosis and can **block** the durable session queue (first line retries indefinitely on failure).

## Goals

1. **Skip HTTP** for library sync and session drain when PlayLog is not linked (no non-whitespace bearer token).
2. **Log at Info** on skip, with wording consistent with the GA import line so support can grep Playnite extension logs.
3. **Manual “PlayLog: Sync all games”:** when unlinked, **log + a user-visible dialog** that linking is required (no progress dialog, no API calls).
4. **Do not** drop or ACK queued work without a successful post when unlinked.

## Non-goals

- Changes in `pulse-server` or other repos.
- Stopping enqueue of session lines while unlinked (unless a future task wants to cap file growth).
- Replacing or extending server observability beyond what the client already sends once linked.

## Definition of “linked”

Linked means `PlayLogBearerToken` is **non-null, non-empty, and not whitespace** after trim—same effective rule as `PulseSettingsViewModel.IsPlayLogLinked`.

## Design

### Library sync (automatic)

**Where:** `Pulse.FlushPendingSyncQueues`.

**Behavior:** After the existing `AutoSyncLibrary` check, if **not linked**:

- Log at **Info**: `PlayLog: skip library sync — not linked` (same tone as GA import).
- **Return immediately** without moving items out of `gameIdsToRemove` / `gameIdsToUpdate`. Pending lists stay intact for a later flush when linked.

**Where:** `Pulse.ExecutePendingSyncWork` is only reached when linked if the above gate is placed **before** queue extraction; do not clear pending ID lists before the link check.

**Defensive guard:** At the start of `PulseAccountClient.SyncGamesAsync` and `PulseAccountClient.DeleteGamesByPlayniteIdsAsync`, if there is no bearer token, log at **Info** and **return** without HTTP. This prevents anonymous calls if a future code path invokes these methods without the Pulse-level check.

### Library sync (manual menu)

**Where:** `Pulse.SyncAllGames`.

**Behavior:** If **not linked**, before progress or `SyncGamesAsync`:

- Log at **Info** (same line as auto-sync skip or a clearly paired message).
- Show a **small dialog** via Playnite’s dialog API: user must link PlayLog before syncing the library.
- Do not start the global progress dialog or call the API.

### Session start/stop queue

**Where:** `SessionSyncQueue.TryDrainAll` (or equivalent single entry point used for drain).

**Behavior:** If **not linked**:

- Log at **Info**: `PlayLog: skip session sync — not linked`.
- **Return without** reading, modifying, or deleting the JSONL queue file. Queued lines remain for a later drain after linking.

**Do not** change `PostSessionStartAsync` / `PostSessionStopAsync` to return “success” without HTTP on unlinked: that would cause `ProcessLine` to treat the line as processed and **remove** it without posting.

### Logging consistency

- **Level:** Info for all “skipped because unlinked” paths (library auto, library manual context may also use Error only for real failures).
- **Wording:** Matches GA import style; include `PlayLog:` prefix and `— not linked` where applicable.

## Manual verification

- Unlinked, auto-sync on: library changes accumulate; timer/shutdown flush logs skip and **does not** call sync/delete APIs; pending lists are not cleared on skip.
- Unlinked, **Sync all games**: dialog appears, Info log present, no HTTP.
- Linked: existing behavior unchanged for flush, manual sync, and session drain.
- Unlinked with lines in session queue file: drain skips and file unchanged; after linking, drain proceeds.

## Related

- Server design (context only): `pulse-server` `docs/specs/2026-05-04-games-sync-observability-design.md`
