# Playnite skip library/session sync when unlinked — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When PlayLog is not paired (no bearer token), skip all library sync HTTP, skip session queue draining without mutating the queue file, log Info lines consistent with GA import, and show a dialog for manual “Sync all games” — per [2026-05-06-playnite-skip-library-sync-unlinked-design.md](../specs/2026-05-06-playnite-skip-library-sync-unlinked-design.md).

**Architecture:** Gate **before** dequeuing library work in `Pulse.FlushPendingSyncQueues` using `PulseSettingsViewModel.IsPlayLogLinked`. Gate session drain at the start of `SessionSyncQueue.TryDrainAll` using the same token rule as elsewhere. Add **defensive** no-bearer early returns in `PulseAccountClient.SyncGamesAsync` and `DeleteGamesByPlayniteIdsAsync` only (do **not** change `PostSessionStartAsync` / `PostSessionStopAsync` success semantics). Manual sync checks link before progress/API.

**Tech stack:** C# (.NET per `Pulse.csproj`), Playnite SDK, Newtonsoft.Json, `HttpClient`.

---

## File map

| File | Role |
|------|------|
| `pulse-playnite/Pulse.cs` | Link gates for flush + manual sync; wire session queue delegate |
| `pulse-playnite/SessionSyncQueue.cs` | Bearer check at drain entry; constructor accepts token delegate |
| `pulse-playnite/PulseAccountClient.cs` | `SyncGamesAsync` + `DeleteGamesByPlayniteIdsAsync` defensive guards |

**Tests:** This repo ships no automated test project. Verification is `dotnet build` + manual Playnite checks listed per task.

---

### Task 1: `PulseAccountClient` — defensive no-bearer guards

**Files:**
- Modify: `pulse-playnite/PulseAccountClient.cs` (method `ApplyBearer` area / top of public API methods)

- [ ] **Step 1: Add a private helper** next to `ApplyBearer` (same partial class):

```csharp
private bool HasBearerToken()
{
    var token = getBearerToken != null ? getBearerToken.Invoke() : null;
    return !string.IsNullOrWhiteSpace(token);
}
```

- [ ] **Step 2: At the very start of `DeleteGamesByPlayniteIdsAsync`**, immediately after the method opening brace and before building `ids`, insert:

```csharp
if (!HasBearerToken())
{
    logger.Info("PlayLog: skip delete by playnite — not linked");
    return;
}
```

- [ ] **Step 3: At the very start of `SyncGamesAsync`**, after the opening brace, after the existing empty-list guard (keep the `gameList.Count == 0` check first), insert **after** that block returns, before `hltbBatchCounters`:

```csharp
if (!HasBearerToken())
{
    logger.Info("PlayLog: skip library sync — not linked");
    return;
}
```

**Important:** Insert the bearer check **after** the `gameList.Count == 0` early return so the existing “0 games” log behavior stays unchanged.

- [ ] **Step 4: Build**

Run from `pulse-playnite/`:

```bash
dotnet build Pulse.csproj -c Release
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add PulseAccountClient.cs
git commit -m "fix(playlog): skip games sync and delete calls when unlinked"
```

---

### Task 2: `SessionSyncQueue` — skip drain when unlinked (do not touch JSONL)

**Files:**
- Modify: `pulse-playnite/SessionSyncQueue.cs`

- [ ] **Step 1: Add a field and constructor parameter**

Add a field:

```csharp
private readonly Func<string> getBearerToken;
```

Change the constructor signature from:

`public SessionSyncQueue(string queueFilePath, PulseAccountClient accountClient)`

to:

`public SessionSyncQueue(string queueFilePath, PulseAccountClient accountClient, Func<string> getBearerToken)`

In the constructor body, after null checks, assign:

```csharp
this.getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
```

- [ ] **Step 2: At the very beginning of `TryDrainAll`**, before `lock (fileLock)`, insert:

```csharp
var token = getBearerToken != null ? getBearerToken.Invoke() : null;
if (string.IsNullOrWhiteSpace(token))
{
    logger.Info("PlayLog: skip session sync — not linked");
    return;
}
```

This ensures **no read/write/delete** of the queue file when unlinked.

- [ ] **Step 3: Build**

```bash
dotnet build Pulse.csproj -c Release
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add SessionSyncQueue.cs
git commit -m "fix(playlog): skip session queue drain when unlinked"
```

---

### Task 3: `Pulse` — wire `SessionSyncQueue` token + flush + manual sync

**Files:**
- Modify: `pulse-playnite/Pulse.cs`

- [ ] **Step 1: Pass bearer delegate into `SessionSyncQueue`**

Replace the line that constructs `sessionQueue` (currently only path + client) so the third argument matches `GameActivitySessionImporter` / client token source:

```csharp
sessionQueue = new SessionSyncQueue(
    sessionQueuePath,
    client,
    () => settings.Settings.PlayLogBearerToken?.Trim() ?? string.Empty);
```

- [ ] **Step 2: Gate `FlushPendingSyncQueues` before dequeuing**

Inside `FlushPendingSyncQueues`, **after** the `if (!settings.Settings.AutoSyncLibrary) return;` block and **before** `List<Guid> toRemove;`, add:

```csharp
if (!settings.IsPlayLogLinked)
{
    logger.Info("PlayLog: skip library sync — not linked");
    return;
}
```

Do not move this after the lock; the lock must not run when unlinked.

- [ ] **Step 3: Gate `SyncAllGames` before progress**

At the start of `SyncAllGames`, **after** the method opening brace, **before** `List<Game> allGames`, add:

```csharp
if (!settings.IsPlayLogLinked)
{
    logger.Info("PlayLog: skip library sync — not linked");
    dialogs.ShowMessage(
        "Link PlayLog in extension settings before syncing your library.",
        "PlayLog");
    return;
}
```

Use `ShowMessage` (informational), not `ShowErrorMessage`, unless product copy requires otherwise.

Then leave the existing `allGames` retrieval and progress dialog unchanged.

- [ ] **Step 4: Build**

```bash
dotnet build Pulse.csproj -c Release
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Pulse.cs
git commit -m "fix(playlog): skip library flush and manual sync when unlinked"
```

---

### Task 4: Manual verification (Playnite)

**No new files.**

- [ ] **Unlinked + auto-sync ON:** Change a game (or add/remove) with no token stored — confirm Playnite extension log contains `PlayLog: skip library sync — not linked` and no server traffic for sync (optional: proxy or server logs).
- [ ] **Unlinked + “PlayLog: Sync all games”:** Confirm dialog + same Info skip line; no progress spinner for sync.
- [ ] **Linked:** Full sync and background flush behave as before.
- [ ] **Session queue file:** With queue lines present and unlinked, startup/shutdown/game events that call `TryDrainAll` — file byte-for-byte unchanged; after pairing, drain succeeds.

---

## Self-review (plan vs spec)

| Spec requirement | Plan coverage |
|------------------|---------------|
| Skip HTTP library sync when unlinked; preserve pending IDs | Task 3 Step 2 (`FlushPendingSyncQueues` before lock) |
| Info log `skip library sync — not linked` | Task 3 Steps 2–3 |
| Manual sync: log + dialog | Task 3 Step 3 |
| Defensive `SyncGamesAsync` / delete guard | Task 1 |
| Session: skip drain, no file mutation; no fake POST success | Task 2 (entry gate only); explicit note not to change `PostSession*` |
| Logging style | Task 1 uses parallel phrasing for delete/sync skip; align copy with spec if you want one unified string (optional follow-up) |

**Gap check:** None blocking. `SyncGamesAsync` defensive log matches the flush/manual line for grep; delete path uses “skip delete by playnite — not linked” to distinguish DELETEs in logs.

---

## Execution handoff

**Plan complete and saved to** `pulse-playnite/docs/superpowers/plans/2026-05-06-playnite-skip-library-sync-unlinked.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — Dispatch a fresh subagent per task, review between tasks, fast iteration. **Required sub-skill:** `superpowers:subagent-driven-development`.

2. **Inline execution** — Run tasks in this session using checkpoints. **Required sub-skill:** `superpowers:executing-plans`.

**Which approach would you like?**
