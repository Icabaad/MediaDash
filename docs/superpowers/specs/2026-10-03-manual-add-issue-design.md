# Manual "Add to issues" from the file browser

**Status:** design approved, pending implementation plan
**Date:** 2026-10-03

## Problem

Scanners discover issues on a schedule, filtered by the user's configured ceilings. Sometimes the user knows a specific file wants fixing (e.g. a bloated encode they'd like to shrink) but doesn't want to loosen the global quality ceiling to make the scanner pick it up. Today there's no manual path: the user has to either edit settings globally or re-encode manually outside MediaDash.

Add a right-click / long-press context menu on file-browser rows that lets the user add a file to the issue queue under a chosen type, routed through the existing fixer pipeline.

## Scope

**In scope (v1):**
- Context menu on `.mdFileRow` entries in the Files tab.
- Four manual issue types: `Quality`, `HeavyTranscode`, `Misplaced`, `FormatCompatibility`.
- "Protect this path" as a sibling action (writes a `ProtectionRule`, not an `Issue`).
- Single-file operation (bulk-select context menu is out).

**Out of scope (v1):**
- All other `IssueType` values. They either need rich scanner-only metadata (Duplicate, SubtitleLanguage, LargeTrickplay, EmbeddedCoverArt) or are telemetry-driven in ways manual add can't honestly reproduce (FailedTranscode).
- Bulk context menu on multi-selected rows.
- Keyboard shortcuts.
- Audit log for manual adds — the Dismiss flow already lets the user undo, and the Issues tab shows the row with its Detected timestamp.

## UX

### Trigger

- Desktop: `contextmenu` event on `#mdFileList`, dispatched via `data-name` on the originating `.mdFileRow`.
- Touch: `touchstart` + 500ms hold with no movement ≥10px.

One floating menu element lives in the Files tab template. Single listener, event delegation. Dismisses on outside click, `Escape`, or scroll of `#mdFileList`.

Only enabled when the row is in a writable context (not `d.IsRoot`, not `d.IsRecycleBin`, not `d.IsLogsDir` — same gate as per-row action buttons at `files-tab.js:85`).

### Menu structure

```
Add to issues  ▸     Quality
Protect this path    HeavyTranscode
                     Misplaced…
                     FormatCompatibility
```

- `Quality`, `HeavyTranscode`: single click → POST → toast.
- `Misplaced…`: opens the existing `openPicker(currentFilePath, cb)` folder picker. User picks target directory; `targetPath = joinPath(dir, name)` goes in the request.
- `FormatCompatibility`: single click → POST. Backend derives `converterKey` + `targetExt` from `FormatMatrix.Classify(path)`. If the matrix returns no row, backend returns 400 with message *"No converter available for `.xyz` files"* and the UI shows that in a toast.
- `Protect this path`: opens a small inline dialog with Note (textarea), ScannersMask (default `*`), FixersMask (default `*`). Submit → POST → toast.

### Feedback

Toast via the existing notification helper (if present) or `window.alert` fallback matching `fileMutate`'s pattern. Message:

> *Added as Quality. [Open Issues tab]*

The link switches to the Issues tab (reuse whatever tab-switch mechanism `page-controller.js` already exposes).

## Backend

### New endpoint: `POST MediaDash/Issues/Manual`

```jsonc
// request
{
  "Path": "D:/Media/Movies/Big.Buck.Bunny.mkv",
  "Type": "Quality" | "HeavyTranscode" | "Misplaced" | "FormatCompatibility",
  "TargetPath": "D:/Media/TV/..."     // required iff Type == "Misplaced"
}
// responses
// 201 { "Id": 42 }
// 400 problem+json  — bad type, missing TargetPath for Misplaced, no FormatMatrix row
// 403               — path outside library (LibraryGuard)
// 404               — path exists on disk but no Jellyfin library item (FindByPath returned null)
```

Controller: new method on `MediaDashController` (inherits elevation, DI, routing conventions already in place).

### Reuse: `POST MediaDash/Protection`

Already exists at `MediaDashController.cs:965` (`CreateProtection([FromBody] ProtectionRuleDto)`). The context menu's "Protect this path" dialog submits to it directly — no backend work.

### Storage

Call `IssueStore.ReplaceDetectedIssues(type, [issue], scannedPaths: [path])`. The single-path overload at `IssueStore.cs:43` already:
- Deletes Detected rows for `(type, path)` before insert, so repeated adds are idempotent.
- Suppresses the insert when a Queued or Dismissed row for `(type, path)` already exists (lines 72-82) — matches scanner semantics; user sees "nothing happened" if they add something they already dismissed, which is the right behaviour.

No schema change. No new SQL.

### ItemId resolution

```csharp
var item = _libraryManager.FindByPath(path, isFolder: false);
if (item is null)
    return NotFound(new ProblemDetails {
        Detail = "That file isn't in a Jellyfin library — run a Jellyfin library scan first."
    });
if (!_guard.IsInsideLibrary(path))
    return StatusCode(403, "That path is outside your library folders.");
```

Same pattern as `TranscodeLogScanner.cs:170`. `_guard` is the existing `LibraryGuard`.

### Per-type `DetailsJson` and `SuggestedFix`

| Type | `DetailsJson` | `SuggestedFix` |
|---|---|---|
| Quality | `{}` | `"Re-encode to the configured quality ceiling."` |
| HeavyTranscode | `{}` | `"Re-encode to a direct-play codec/bitrate."` |
| Misplaced | `{"targetPath": "<from request>"}` | `"Move to <targetPath>."` |
| FormatCompatibility | `{"converterKey": "<matrix>", "targetExt": "<matrix>"}` | `"Convert .<srcExt> → .<targetExt>."` |

Common fields:
- `SizeSavings = 0` — unknown without a probe; fixers re-compute at run.
- `Status = Detected` — same as scanner output. Approve / Dismiss / auto-queue flows work unchanged.
- `Confidence = null` — scanner also uses null for non-Duplicate types.
- `DetectedAtUtc = DateTime.UtcNow`.

### Input validation

- `Type`: must parse to one of the four allowed values via a dedicated enum (not `IssueType.TryParse` directly — don't want to accept `Duplicate` or any other scanner-only type). 400 otherwise.
- `Path`: non-empty, otherwise 400.
- `TargetPath`: required when `Type == Misplaced`; must be a directory inside the library (via `LibraryGuard`); 400/403 otherwise.
- `FormatCompatibility`: if `FormatMatrix.Classify(path)` returns null, 400 with the extension in the message.

## Frontend

### Files changed

- `Configuration/scripts/tabs/files-tab.js` — add context-menu element to the template string in `bindFiles`'s view, add listener setup, add submit handlers. Reuses `openPicker`, `deps.api`, `deps.esc`, `joinPath`.
- `Configuration/configPage.html` — append the hidden menu + protect-dialog markup (one `<div>` each, display:none by default).
- `Configuration/styles/*.css` — a small block for the menu (absolute-positioned, border, shadow, hover states matching the existing Jellyfin dashboard look).
- `Configuration/scripts/api-client.js` — add a helper for `POST Issues/Manual` matching the file's existing wrapper style. The protect call reuses whatever wrapper already covers `POST Protection`.

### Files-tab wiring (sketch)

```js
// one listener via delegation
listEl.addEventListener('contextmenu', function (e) {
    var row = e.target.closest('.mdFileRow');
    if (!row || !isWritableContext()) return;
    e.preventDefault();
    openContextMenu(e.clientX, e.clientY, {
        name: row.dataset.name,
        isDir: row.dataset.isdir === '1'
    });
});
```

Touch:
```js
var pressTimer = null, pressStart = null;
listEl.addEventListener('touchstart', function (e) {
    var row = e.target.closest('.mdFileRow');
    if (!row) return;
    pressStart = { x: e.touches[0].clientX, y: e.touches[0].clientY };
    pressTimer = setTimeout(function () {
        openContextMenu(pressStart.x, pressStart.y, { name: row.dataset.name, isDir: row.dataset.isdir === '1' });
    }, 500);
});
listEl.addEventListener('touchmove', cancelPress);
listEl.addEventListener('touchend', cancelPress);
function cancelPress() { if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; } }
```

Folders show only "Protect this path" (the four Issue types operate on files).

## Testing

Minimum runnable checks:

1. **Backend unit test** (`Jellyfin.Plugin.MediaDash.Tests/Api/`): for each of the four types + the Protect endpoint, assert:
   - Happy path writes the expected row (`Status = Detected`, correct `DetailsJson`).
   - 404 when `FindByPath` returns null.
   - 403 when path outside library.
   - 400 when request is malformed per type.
   - Idempotency: calling twice for `(Quality, path)` leaves one Detected row, not two.
   - Suppression: calling for a path that already has a Queued row returns 201 but inserts nothing (matches scanner behaviour); test asserts row count.

2. **FormatMatrix refusal test**: POST `FormatCompatibility` for a `.mkv` (which has no converter row) returns 400 with the extension in the message.

3. **Fixer round-trip (manual, from the PLAN.md §7 verification cadence)**: on the fixture library, manually add a Quality issue via the context menu, run the fixer in dry-run, confirm it plans the re-encode; then run live, confirm the file is replaced and the recycle bin receives the original.

No new frameworks. Unit tests use the existing `IssueStore` + in-memory SQLite harness that other API tests already use.

## Risks & non-risks

- **Risk:** a user manually adds `Quality` on a file already under the ceiling → fixer runs, decides there's nothing to do, logs it. Not a bug — this is the fixer's existing refusal path (`FixReason` enum has values for it). The user learns from the Issues tab that the row completed with no action. Acceptable.
- **Risk:** `Misplaced` target folder deleted between picker and fix run → existing `MediaSorterFixer` already returns `FixReason.SourceMissing` with a clear message. Covered.
- **Non-risk:** item ID drift. We resolve at create-time; if the library item is later re-indexed with a new Guid, the fixer re-resolves from `issue.Path` at run time (every fixer does this).
- **Non-risk:** path traversal / write to arbitrary disk. `LibraryGuard` is in the critical path server-side. The context menu never writes files — it only writes DB rows.

## Open questions

None at design time. Any follow-ups surface during the implementation plan.
