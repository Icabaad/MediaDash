# Manual "Add to Issues" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a right-click / long-press context menu on file-browser rows that lets the user add a single file to the issue queue under one of four manual issue types (Quality, HeavyTranscode, Misplaced, FormatCompatibility), plus a sibling "Protect this path" action that writes a `ProtectionRule`.

**Architecture:** New `POST MediaDash/Issues/Manual` endpoint on `MediaDashController` writes through the existing `IssueStore.ReplaceDetectedIssues` path (idempotent, respects Queued/Dismissed suppression). Core request→Issue logic extracted to a static `BuildManualIssue` helper so it unit-tests without controller/library-manager mocking (same pattern used by `PatchApprovedInMixedLibrary` at `MediaDashController` for the Approve endpoint). Frontend uses one shared menu element in `files-tab.js` with event delegation on `#mdFileList`. "Protect this path" reuses existing `POST MediaDash/Protection` at `MediaDashController.cs:965`.

**Tech Stack:** .NET 9 (`Jellyfin.Plugin.MediaDash`), xUnit for tests, vanilla JS for the dashboard tab (no framework / no JS test runner — frontend verified manually against the fixture library per `PLAN.md` §7).

**Spec:** `docs/superpowers/specs/2026-10-03-manual-add-issue-design.md`

**Knowingly lazy deviation from spec:** the toast shown after a successful manual add is plain text (*"Added as Quality. Switch to the Issues tab to review."*) instead of a clickable deep-link to the Issues tab. The tab-switch API is undocumented across `page-controller.js`; shipping the plain-text version avoids reverse-engineering it. Add a real deep-link when a user asks for it.

**Version policy:** This is a feature (3rd-digit bump per `project_mediadash_roadmap.md`), bundled into the next 1.0.8.x release. **Do not** bump `manifest.json` or `Directory.Build.props` versions in this branch — version bump + release is a separate per-step-approved workflow.

---

## File Structure

### Create

- `Jellyfin.Plugin.MediaDash/Api/ManualIssueRequest.cs` — request DTO + the `ManualIssueType` enum (deliberately narrower than `IssueType` so we can't accept Duplicate / SubtitleLanguage / etc.).
- `Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs` — unit tests for the static `BuildManualIssue` helper.
- `Jellyfin.Plugin.MediaDash.Tests/ManualIssueStoreRoundTripTests.cs` — integration test that round-trips through `IssueStore`.

### Modify

- `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs` — add static `BuildManualIssue` + `POST Issues/Manual` method.
- `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` — append hidden `<div>` for the context menu, submenu, and protect dialog.
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/files-tab.js` — context-menu wiring + per-type submit + long-press.
- `Jellyfin.Plugin.MediaDash/Configuration/styles/*.css` — small block for menu positioning / visuals (identify the right file during Task 6).
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/api-client.js` — add one wrapper for `POST Issues/Manual`.
- `CHANGELOG.md` — one bullet per completed feature (one bullet for the whole context-menu feature is fine).

### Not touched

- `IssueStore.cs` — reuses `ReplaceDetectedIssues` as-is.
- Protection endpoint — already exists at `MediaDashController.cs:965`.
- All fixers — manual issues flow through the existing fixer path untouched.

---

## Task 1: Request DTO + narrow enum

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Api/ManualIssueRequest.cs`

- [ ] **Step 1: Create the DTO file**

```csharp
namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// The subset of <see cref="Data.IssueType"/> that is safe to create manually from the file
/// browser context menu. Intentionally narrower than the full enum — Duplicate / SubtitleLanguage /
/// LargeTrickplay / etc. need scanner-only metadata and cannot be constructed from a path alone.
/// </summary>
public enum ManualIssueType
{
    /// <summary>Re-encode to the configured quality ceiling.</summary>
    Quality = 0,

    /// <summary>Re-encode to a direct-play codec/bitrate.</summary>
    HeavyTranscode = 1,

    /// <summary>Move to a user-chosen target folder.</summary>
    Misplaced = 2,

    /// <summary>Convert to a client-renderable format via the FormatMatrix.</summary>
    FormatCompatibility = 3
}

/// <summary>
/// Payload for POST MediaDash/Issues/Manual.
/// </summary>
public sealed class ManualIssueRequest
{
    /// <summary>Gets or sets the absolute path of the file the user right-clicked.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the issue type the user picked from the dropdown.</summary>
    public ManualIssueType Type { get; set; }

    /// <summary>Gets or sets the destination folder for a <see cref="ManualIssueType.Misplaced"/> add. Required for that type only.</summary>
    public string? TargetPath { get; set; }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run (from `C:\dev\mediadash`):
```powershell
dotnet build Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash/Api/ManualIssueRequest.cs
git commit -m "Add ManualIssueRequest DTO + ManualIssueType enum"
```

---

## Task 2: Static `BuildManualIssue` helper (TDD, happy paths)

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs` (append new static method)
- Create: `Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs`

The helper returns `(Issue? issue, string? problem)` — `issue` populated on success, `problem` populated with a user-visible message on validation failure. Keeps the controller method a one-liner.

- [ ] **Step 1: Write failing tests for the four happy paths**

Create `Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs`:

```csharp
using System;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ManualIssueBuildTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Build_Quality_SetsPathTypeAndEmptyDetails()
    {
        var req = new ManualIssueRequest { Path = "/lib/a.mkv", Type = ManualIssueType.Quality };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.NotNull(issue);
        Assert.Equal(IssueType.Quality, issue!.Type);
        Assert.Equal("/lib/a.mkv", issue.Path);
        Assert.Equal(ItemId, issue.ItemId);
        Assert.Equal(IssueStatus.Detected, issue.Status);
        Assert.Equal("{}", issue.DetailsJson);
        Assert.Equal(0, issue.SizeSavings);
        Assert.Null(issue.Confidence);
        Assert.False(string.IsNullOrEmpty(issue.SuggestedFix));
    }

    [Fact]
    public void Build_HeavyTranscode_SetsPathTypeAndEmptyDetails()
    {
        var req = new ManualIssueRequest { Path = "/lib/b.mp4", Type = ManualIssueType.HeavyTranscode };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.HeavyTranscode, issue!.Type);
        Assert.Equal("{}", issue.DetailsJson);
    }

    [Fact]
    public void Build_Misplaced_WritesTargetPathIntoDetails()
    {
        var req = new ManualIssueRequest
        {
            Path = "/lib/Movies/show.s01e01.mkv",
            Type = ManualIssueType.Misplaced,
            TargetPath = "/lib/TV/Show/Season 01/show.s01e01.mkv"
        };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.Misplaced, issue!.Type);
        using var doc = JsonDocument.Parse(issue.DetailsJson);
        Assert.Equal("/lib/TV/Show/Season 01/show.s01e01.mkv",
                     doc.RootElement.GetProperty("targetPath").GetString());
    }

    [Fact]
    public void Build_FormatCompatibility_PopulatesConverterKeyAndTargetExtFromMatrix()
    {
        // .mobi is a known FormatMatrix row (ebook); converter resolves to a target extension.
        var req = new ManualIssueRequest { Path = "/lib/book.mobi", Type = ManualIssueType.FormatCompatibility };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.FormatCompatibility, issue!.Type);
        using var doc = JsonDocument.Parse(issue.DetailsJson);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("converterKey").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("targetExt").GetString()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter "FullyQualifiedName~ManualIssueBuildTests"
```
Expected: compile error — `MediaDashController.BuildManualIssue` is not defined.

- [ ] **Step 3: Add the helper to `MediaDashController`**

Open `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs`. Find the existing static helper `PatchApprovedInMixedLibrary` (used by the Approve endpoint — grep for the name to locate it) and insert the new static method directly above or below it. Add:

```csharp
/// <summary>
/// Builds the <see cref="Data.Issue"/> that <see cref="ManualIssueRequest"/> describes, or returns
/// a human-readable problem message when the request is invalid for the picked type. Pure function —
/// no DB, no library manager — so it unit-tests directly.
/// </summary>
/// <param name="request">The deserialized request body. Must be non-null.</param>
/// <param name="itemId">The Jellyfin item id the caller resolved from <see cref="ManualIssueRequest.Path"/>.</param>
/// <returns>A tuple of (issue, problem). Exactly one is non-null.</returns>
public static (Data.Issue? Issue, string? Problem) BuildManualIssue(
    ManualIssueRequest request,
    Guid itemId)
{
    if (request is null || string.IsNullOrWhiteSpace(request.Path))
    {
        return (null, "Request body is missing a Path.");
    }

    var now = DateTime.UtcNow;
    switch (request.Type)
    {
        case ManualIssueType.Quality:
            return (new Data.Issue
            {
                Type = Data.IssueType.Quality,
                ItemId = itemId,
                Path = request.Path,
                DetailsJson = "{}",
                SuggestedFix = "Re-encode to the configured quality ceiling.",
                SizeSavings = 0,
                Status = Data.IssueStatus.Detected,
                DetectedAtUtc = now
            }, null);

        case ManualIssueType.HeavyTranscode:
            return (new Data.Issue
            {
                Type = Data.IssueType.HeavyTranscode,
                ItemId = itemId,
                Path = request.Path,
                DetailsJson = "{}",
                SuggestedFix = "Re-encode to a direct-play codec and bitrate.",
                SizeSavings = 0,
                Status = Data.IssueStatus.Detected,
                DetectedAtUtc = now
            }, null);

        case ManualIssueType.Misplaced:
            if (string.IsNullOrWhiteSpace(request.TargetPath))
            {
                return (null, "A target folder is required when adding a Misplaced issue.");
            }
            var misplacedDetails = System.Text.Json.JsonSerializer.Serialize(new { targetPath = request.TargetPath });
            return (new Data.Issue
            {
                Type = Data.IssueType.Misplaced,
                ItemId = itemId,
                Path = request.Path,
                DetailsJson = misplacedDetails,
                SuggestedFix = "Move to " + request.TargetPath + ".",
                SizeSavings = 0,
                Status = Data.IssueStatus.Detected,
                DetectedAtUtc = now
            }, null);

        case ManualIssueType.FormatCompatibility:
            var row = Scanners.FormatMatrix.Classify(request.Path);
            if (row is null || string.IsNullOrEmpty(row.TargetExtension) || row.ConverterKey == "none")
            {
                var ext = System.IO.Path.GetExtension(request.Path);
                return (null, "No converter is registered for '" + ext + "' files.");
            }
            var fcDetails = System.Text.Json.JsonSerializer.Serialize(new
            {
                converterKey = row.ConverterKey,
                targetExt = row.TargetExtension
            });
            return (new Data.Issue
            {
                Type = Data.IssueType.FormatCompatibility,
                ItemId = itemId,
                Path = request.Path,
                DetailsJson = fcDetails,
                SuggestedFix = "Convert " + System.IO.Path.GetExtension(request.Path) + " to " + row.TargetExtension + ".",
                SizeSavings = 0,
                Status = Data.IssueStatus.Detected,
                DetectedAtUtc = now
            }, null);

        default:
            return (null, "Unknown manual issue type: " + request.Type + ".");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter "FullyQualifiedName~ManualIssueBuildTests"
```
Expected: `Passed! - Failed: 0, Passed: 4`.

If `Build_FormatCompatibility` fails because `.mobi` isn't in the matrix the way the test assumes, open `Scanners/FormatMatrix.cs`, pick any row where `ConverterKey != "none"` and `TargetExtension != null`, and swap the extension in the test accordingly.

- [ ] **Step 5: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs
git commit -m "Add BuildManualIssue helper + happy-path tests"
```

---

## Task 3: Validation-failure tests for the helper (TDD, red-only pass unneeded)

The helper already handles failures; these tests pin the contract.

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs`

- [ ] **Step 1: Append failure tests**

Add to the existing `ManualIssueBuildTests` class:

```csharp
[Fact]
public void Build_RejectsEmptyPath()
{
    var (issue, problem) = MediaDashController.BuildManualIssue(
        new ManualIssueRequest { Path = "", Type = ManualIssueType.Quality }, ItemId);
    Assert.Null(issue);
    Assert.NotNull(problem);
}

[Fact]
public void Build_Misplaced_RejectsMissingTargetPath()
{
    var (issue, problem) = MediaDashController.BuildManualIssue(
        new ManualIssueRequest { Path = "/lib/a.mkv", Type = ManualIssueType.Misplaced, TargetPath = null }, ItemId);
    Assert.Null(issue);
    Assert.Contains("target", problem!, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Build_FormatCompatibility_RejectsExtensionWithNoConverter()
{
    // .mkv has no FormatMatrix row that produces a conversion target — it's a video container
    // that Jellyfin already renders. If this ever changes, pick another unknown ext.
    var (issue, problem) = MediaDashController.BuildManualIssue(
        new ManualIssueRequest { Path = "/lib/a.mkv", Type = ManualIssueType.FormatCompatibility }, ItemId);
    Assert.Null(issue);
    Assert.Contains(".mkv", problem!, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run tests**

Run:
```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter "FullyQualifiedName~ManualIssueBuildTests"
```
Expected: all 7 tests pass. If `Build_FormatCompatibility_RejectsExtensionWithNoConverter` fails, the matrix shape has changed — pick a different unknown extension and update the test.

- [ ] **Step 3: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash.Tests/ManualIssueBuildTests.cs
git commit -m "Pin BuildManualIssue validation-failure contract"
```

---

## Task 4: `POST Issues/Manual` endpoint

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs`

- [ ] **Step 1: Add the controller method**

Find the existing POST endpoints near `[HttpPost("Issues/{id}/Approve")]` (around line 226). Add this new method next to the other `Issues/*` POSTs so the surface area is grouped:

```csharp
/// <summary>
/// Creates a Detected issue from the file-browser context menu. Request fields and allowed types
/// documented on <see cref="ManualIssueRequest"/> and <see cref="ManualIssueType"/>. Routes through
/// the same <see cref="Data.IssueStore.ReplaceDetectedIssues"/> path scanners use, so Queued and
/// Dismissed rows for the same (type, path) suppress the insert (user asked never to see it, or
/// already approved it — manual add respects both).
/// </summary>
/// <param name="request">The manual-issue payload.</param>
/// <returns>201 with the new issue id, or an appropriate error status.</returns>
[HttpPost("Issues/Manual")]
[ProducesResponseType(StatusCodes.Status201Created)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public ActionResult<object> CreateManualIssue([FromBody] ManualIssueRequest request)
{
    if (request is null || string.IsNullOrWhiteSpace(request.Path))
    {
        return BadRequest("Request body is missing a Path.");
    }

    // Guard: path must be inside a configured library root. Same policy every mutating file op
    // uses — see FileBrowserController. Keeps us from recording issues against arbitrary disk paths.
    if (!_libraryGuard.IsInsideLibrary(request.Path))
    {
        return StatusCode(StatusCodes.Status403Forbidden, "That path is outside your library folders.");
    }

    var item = _libraryManager.FindByPath(request.Path, false);
    if (item is null)
    {
        return NotFound("That file isn't tracked in a Jellyfin library — run a Jellyfin library scan first.");
    }

    var (issue, problem) = BuildManualIssue(request, item.Id);
    if (issue is null)
    {
        return BadRequest(problem);
    }

    // Pass scannedPaths so ReplaceDetectedIssues scopes its DELETE to this single path.
    _issueStore.ReplaceDetectedIssues(issue.Type, new[] { issue }, scannedPaths: new[] { issue.Path });

    // Round-trip to read back the id (the single-item insert either wrote exactly one row or was
    // suppressed by an existing Queued/Dismissed row — both are "accepted" from the user's view).
    var existing = _issueStore.GetIssues(type: issue.Type, status: null);
    long id = 0;
    foreach (var row in existing)
    {
        if (row.Path == issue.Path && (row.Status == Data.IssueStatus.Detected || row.Status == Data.IssueStatus.Queued))
        {
            id = row.Id;
            break;
        }
    }

    return StatusCode(StatusCodes.Status201Created, new { Id = id });
}
```

Verify the field names used (`_libraryGuard`, `_libraryManager`, `_issueStore`) match what's already declared on the controller. If any name differs, use the actual field name — don't invent new fields.

- [ ] **Step 2: Build**

```powershell
dotnet build Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs
git commit -m "Add POST Issues/Manual endpoint"
```

---

## Task 5: Round-trip test through `IssueStore` (idempotency + suppression)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/ManualIssueStoreRoundTripTests.cs`

Follows the pattern of `Formats/FormatCompatibilityApproveTests.cs` — temp SQLite file, `[Collection("MediaDashDb")]`, direct store usage.

- [ ] **Step 1: Write the test file**

```csharp
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class ManualIssueStoreRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-manual-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly IssueStore _store;

    public ManualIssueStoreRoundTripTests()
    {
        _db = new MediaDashDb(_dbPath);
        _store = new IssueStore(_db.ConnectionFactory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void AddingQualityTwice_LeavesOneDetectedRow()
    {
        var itemId = Guid.NewGuid();
        var path = "/lib/a.mkv";
        var req = new ManualIssueRequest { Path = path, Type = ManualIssueType.Quality };

        var (issue1, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { issue1! }, scannedPaths: new[] { path });

        var (issue2, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { issue2! }, scannedPaths: new[] { path });

        var rows = _store.GetIssues(type: IssueType.Quality);
        Assert.Single(rows);
        Assert.Equal(IssueStatus.Detected, rows[0].Status);
    }

    [Fact]
    public void AddingQuality_WhenAlreadyQueued_IsSuppressed()
    {
        var itemId = Guid.NewGuid();
        var path = "/lib/a.mkv";
        var req = new ManualIssueRequest { Path = path, Type = ManualIssueType.Quality };

        var (first, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { first! }, scannedPaths: new[] { path });
        var detectedId = _store.GetIssues(type: IssueType.Quality).Single().Id;
        _store.BulkUpdateOpenIssueStatus(new[] { detectedId }, IssueStatus.Queued);

        // Second manual add while Queued: should NOT insert a second row.
        var (second, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { second! }, scannedPaths: new[] { path });

        var rows = _store.GetIssues(type: IssueType.Quality);
        Assert.Single(rows);
        Assert.Equal(IssueStatus.Queued, rows[0].Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter "FullyQualifiedName~ManualIssueStoreRoundTripTests"
```
Expected: `Failed: 0, Passed: 2`.

If `MediaDashDb.ConnectionFactory` isn't a public property (test authors in the existing suite may expose it differently), peek at `Formats/FormatCompatibilityApproveTests.cs:66-90` and copy the exact access pattern used there.

- [ ] **Step 3: Run the full test suite**

```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj
```
Expected: no new failures introduced by the new code.

- [ ] **Step 4: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash.Tests/ManualIssueStoreRoundTripTests.cs
git commit -m "Round-trip tests: idempotency + Queued suppression for manual add"
```

---

## Task 6: Context menu markup + CSS

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/configPage.html`
- Modify: pick the right file under `Jellyfin.Plugin.MediaDash/Configuration/styles/` — grep for `.mdFileRow` to find where files-tab styles live.

- [ ] **Step 1: Append hidden menu markup**

Open `configPage.html`. Find the markup that owns the Files tab (search for `mdFileList`). Just before the closing `</div>` of the Files tab container, insert:

```html
<!-- Manual add + protect context menu. One shared element for the whole Files tab. -->
<div id="mdFileCtxMenu" class="mdCtxMenu" style="display:none;">
    <div class="mdCtxItem" data-ctx="issues" role="button" tabindex="0">
        <span class="material-icons">push_pin</span>
        <span>Add to issues</span>
        <span class="material-icons mdCtxArrow">chevron_right</span>
    </div>
    <div class="mdCtxItem" data-ctx="protect" role="button" tabindex="0">
        <span class="material-icons">shield</span>
        <span>Protect this path</span>
    </div>
    <div id="mdFileCtxSubmenu" class="mdCtxSubmenu" style="display:none;">
        <div class="mdCtxItem" data-type="Quality" role="button" tabindex="0">Quality (re-encode to shrink)</div>
        <div class="mdCtxItem" data-type="HeavyTranscode" role="button" tabindex="0">HeavyTranscode (re-encode for direct play)</div>
        <div class="mdCtxItem" data-type="Misplaced" role="button" tabindex="0">Misplaced&hellip; (pick target folder)</div>
        <div class="mdCtxItem" data-type="FormatCompatibility" role="button" tabindex="0">FormatCompatibility (convert)</div>
    </div>
</div>

<!-- Protect-this-path dialog, triggered from the context menu. -->
<div id="mdProtectDialog" class="mdDialog" style="display:none;">
    <div class="mdDialogInner">
        <h3>Protect this path</h3>
        <p id="mdProtectPath" class="fieldDescription"></p>
        <label>Note (optional)
            <textarea id="mdProtectNote" rows="2"></textarea>
        </label>
        <label>Block scanners (<code>*</code> = all)
            <input id="mdProtectScanners" type="text" value="*" />
        </label>
        <label>Block fixers (<code>*</code> = all)
            <input id="mdProtectFixers" type="text" value="*" />
        </label>
        <div class="mdDialogActions">
            <button is="emby-button" type="button" id="mdProtectCancel" class="raised">Cancel</button>
            <button is="emby-button" type="button" id="mdProtectAccept" class="raised button-submit">Protect</button>
        </div>
    </div>
</div>
```

- [ ] **Step 2: Find the right CSS file**

Run (via the Grep tool, not raw grep in the shell):
- Pattern: `\.mdFileRow`
- Path: `Jellyfin.Plugin.MediaDash/Configuration/styles`
- Output mode: `files_with_matches`

Note the file that comes back; it owns the Files tab styles.

- [ ] **Step 3: Append menu + dialog styles to that file**

```css
/* Manual-add / protect context menu — shared element positioned to the right-click point. */
.mdCtxMenu {
    position: fixed;
    z-index: 10000;
    background: #1f2027;
    border: 1px solid rgba(255,255,255,0.1);
    border-radius: 4px;
    box-shadow: 0 8px 20px rgba(0,0,0,0.5);
    min-width: 14em;
    padding: .25em 0;
    font-size: .9em;
}
.mdCtxItem {
    display: flex;
    align-items: center;
    gap: .55em;
    padding: .5em .85em;
    cursor: pointer;
    color: #e5e7eb;
}
.mdCtxItem:hover,
.mdCtxItem:focus {
    background: rgba(255,255,255,0.08);
    outline: none;
}
.mdCtxArrow {
    margin-left: auto;
    font-size: 1.1em;
    opacity: .6;
}
.mdCtxSubmenu {
    position: absolute;
    left: 100%;
    top: 0;
    background: #1f2027;
    border: 1px solid rgba(255,255,255,0.1);
    border-radius: 4px;
    box-shadow: 0 8px 20px rgba(0,0,0,0.5);
    min-width: 18em;
    padding: .25em 0;
}
.mdDialog {
    position: fixed;
    inset: 0;
    background: rgba(0,0,0,0.6);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 10000;
}
.mdDialogInner {
    background: #1f2027;
    border-radius: 6px;
    padding: 1.25em;
    max-width: 32em;
    width: 90%;
    display: flex;
    flex-direction: column;
    gap: .75em;
}
.mdDialogInner label {
    display: flex;
    flex-direction: column;
    gap: .25em;
}
.mdDialogActions {
    display: flex;
    gap: .5em;
    justify-content: flex-end;
}
```

- [ ] **Step 4: Build to pick up the embedded resource**

```powershell
dotnet build Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: builds clean. HTML / CSS live as embedded resources in the plugin; they're validated at build only insofar as the files are present.

- [ ] **Step 5: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash/Configuration/configPage.html Jellyfin.Plugin.MediaDash/Configuration/styles/
git commit -m "Add context-menu + protect-dialog markup and styles"
```

---

## Task 7: Context-menu open/close + per-type submit in `files-tab.js`

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/files-tab.js`

This is the chunky task — one script section that wires:
1. Right-click delegation to open the menu at cursor.
2. Outside-click / Escape / scroll to close.
3. "Add to issues" → submenu → per-type POST.
4. "Protect this path" → protect dialog → POST.
5. Long-press for touch (500ms, cancel on move/leave).

- [ ] **Step 1: Add the menu logic**

Inside `window.MediaDash.createFilesTab = function (deps) { ... }`, directly above `return { ... }` at the bottom (around line 504), add:

```javascript
// ---------- Context menu (manual add + protect) ----------
var ctxMenu = deps.view.querySelector('#mdFileCtxMenu');
var ctxSubmenu = deps.view.querySelector('#mdFileCtxSubmenu');
var protectDialog = deps.view.querySelector('#mdProtectDialog');
var ctxTargetName = '';
var pressTimer = null;
var pressOrigin = null;

function isWritableContext() {
    if (!currentFileListing) return false;
    return !currentFileListing.IsRoot && !currentFileListing.IsRecycleBin && !currentFileListing.IsLogsDir;
}

function openCtxMenu(clientX, clientY, name, isDir) {
    if (!isWritableContext()) return;
    ctxTargetName = name;
    // Folders can be protected but not added as an Issue — hide the Issues item.
    ctxMenu.querySelector('[data-ctx="issues"]').style.display = isDir ? 'none' : 'flex';
    ctxSubmenu.style.display = 'none';
    ctxMenu.style.display = 'block';
    // Clamp to viewport so the menu never clips off-screen.
    ctxMenu.style.left = Math.min(clientX, window.innerWidth - 240) + 'px';
    ctxMenu.style.top = Math.min(clientY, window.innerHeight - 180) + 'px';
}

function closeCtxMenu() {
    ctxMenu.style.display = 'none';
    ctxSubmenu.style.display = 'none';
    ctxTargetName = '';
}

function openProtectDialog(path) {
    deps.view.querySelector('#mdProtectPath').textContent = path;
    deps.view.querySelector('#mdProtectNote').value = '';
    deps.view.querySelector('#mdProtectScanners').value = '*';
    deps.view.querySelector('#mdProtectFixers').value = '*';
    protectDialog.style.display = 'flex';
}
function closeProtectDialog() { protectDialog.style.display = 'none'; }

function submitManualIssue(type, extraFields) {
    var full = joinPath(currentFilePath, ctxTargetName);
    var body = { Path: full, Type: type };
    if (extraFields) Object.keys(extraFields).forEach(function (k) { body[k] = extraFields[k]; });
    closeCtxMenu();
    deps.api.ajax({
        type: 'POST',
        url: ApiClient.getUrl('MediaDash/Issues/Manual'),
        contentType: 'application/json',
        data: JSON.stringify(body)
    }).then(function () {
        showToast('Added as ' + type + '. Switch to the Issues tab to review.');
    }, function (xhr) {
        var msg = (xhr && xhr.responseText) ? xhr.responseText.replace(/^"|"$/g, '') : 'Could not add issue.';
        window.alert(msg);
    });
}

function showToast(msg) {
    // Jellyfin dashboard exposes a global Dashboard.alert / toast helper on most pages. Fall back to
    // window.alert when it isn't available (keeps parity with fileMutate's error path).
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
        window.Dashboard.alert(msg);
    } else {
        window.alert(msg);
    }
}

function bindCtxMenu() {
    var listEl = deps.view.querySelector('#mdFileList');

    // Desktop right-click, delegated.
    listEl.addEventListener('contextmenu', function (e) {
        var row = e.target.closest('.mdFileRow');
        if (!row) return;
        e.preventDefault();
        openCtxMenu(e.clientX, e.clientY, row.getAttribute('data-name'), row.getAttribute('data-isdir') === '1');
    });

    // Touch long-press (500ms with <10px drift).
    listEl.addEventListener('touchstart', function (e) {
        var row = e.target.closest('.mdFileRow');
        if (!row || e.touches.length !== 1) return;
        var t = e.touches[0];
        pressOrigin = { x: t.clientX, y: t.clientY, name: row.getAttribute('data-name'), isDir: row.getAttribute('data-isdir') === '1' };
        pressTimer = setTimeout(function () {
            if (!pressOrigin) return;
            openCtxMenu(pressOrigin.x, pressOrigin.y, pressOrigin.name, pressOrigin.isDir);
            pressOrigin = null;
        }, 500);
    });
    function cancelPress() {
        if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; }
        pressOrigin = null;
    }
    listEl.addEventListener('touchmove', function (e) {
        if (!pressOrigin || e.touches.length !== 1) return;
        var t = e.touches[0];
        if (Math.abs(t.clientX - pressOrigin.x) > 10 || Math.abs(t.clientY - pressOrigin.y) > 10) cancelPress();
    });
    listEl.addEventListener('touchend', cancelPress);
    listEl.addEventListener('touchcancel', cancelPress);

    // Outside-click / Escape / scroll closes the menu.
    document.addEventListener('click', function (e) {
        if (ctxMenu.style.display === 'none') return;
        if (!ctxMenu.contains(e.target)) closeCtxMenu();
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') closeCtxMenu();
    });
    listEl.addEventListener('scroll', closeCtxMenu, true);

    // "Add to issues" opens the submenu.
    ctxMenu.querySelector('[data-ctx="issues"]').addEventListener('click', function (e) {
        e.stopPropagation();
        ctxSubmenu.style.display = 'block';
    });

    // "Protect this path" opens the dialog.
    ctxMenu.querySelector('[data-ctx="protect"]').addEventListener('click', function (e) {
        e.stopPropagation();
        var full = joinPath(currentFilePath, ctxTargetName);
        closeCtxMenu();
        openProtectDialog(full);
    });

    // Submenu type picks.
    Array.from(ctxSubmenu.querySelectorAll('.mdCtxItem')).forEach(function (item) {
        item.addEventListener('click', function (e) {
            e.stopPropagation();
            var type = item.getAttribute('data-type');
            if (type === 'Misplaced') {
                var pickTargetFor = ctxTargetName;
                closeCtxMenu();
                openPicker(currentFilePath, function (targetDir) {
                    if (!targetDir) return;
                    // Re-set ctxTargetName so submitManualIssue uses it (closeCtxMenu cleared it).
                    ctxTargetName = pickTargetFor;
                    submitManualIssue('Misplaced', { TargetPath: joinPath(targetDir, pickTargetFor) });
                    ctxTargetName = '';
                });
            } else {
                submitManualIssue(type, null);
            }
        });
    });

    // Protect dialog buttons.
    deps.view.querySelector('#mdProtectCancel').addEventListener('click', closeProtectDialog);
    deps.view.querySelector('#mdProtectAccept').addEventListener('click', function () {
        var path = deps.view.querySelector('#mdProtectPath').textContent;
        var body = {
            Kind: 'folder',
            Target: path,
            ScannersMask: deps.view.querySelector('#mdProtectScanners').value || '',
            FixersMask: deps.view.querySelector('#mdProtectFixers').value || '',
            Note: deps.view.querySelector('#mdProtectNote').value || ''
        };
        deps.api.ajax({
            type: 'POST',
            url: ApiClient.getUrl('MediaDash/Protection'),
            contentType: 'application/json',
            data: JSON.stringify(body)
        }).then(function () {
            closeProtectDialog();
            showToast('Protected ' + path + '.');
        }, function (xhr) {
            var msg = (xhr && xhr.responseText) ? xhr.responseText.replace(/^"|"$/g, '') : 'Could not add protection.';
            window.alert(msg);
        });
    });
}
```

- [ ] **Step 2: Call `bindCtxMenu()` from `bindFiles()`**

Inside the existing `bindFiles` function (bottom of the file), as the last line before its closing brace, add:

```javascript
bindCtxMenu();
```

- [ ] **Step 3: Confirm the `ProtectionRuleDto` field names**

Open `Jellyfin.Plugin.MediaDash/Api/ProtectionRuleDto.cs` and compare its property names to the body built in Step 1 (`Kind`, `Target`, `ScannersMask`, `FixersMask`, `Note`). If any differ, update the JS body to match. If the DTO uses different casing (JSON serializer settings vary), inspect how the Settings tab's existing protection-rule editor POSTs — the match must be exact.

- [ ] **Step 4: Build**

```powershell
dotnet build Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: builds clean.

- [ ] **Step 5: Commit**

```powershell
git add Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/files-tab.js
git commit -m "Wire context menu + per-type submit + long-press"
```

---

## Task 8: Manual verification against the local Jellyfin test instance

**Files:** no code changes.

This task is the single runnable check for the frontend (the project has no JS test runner — frontend changes are verified end-to-end against the live plugin per `CLAUDE.md` and `feedback_mediadash_real_world_tests.md`).

- [ ] **Step 1: Deploy the current build to the local Jellyfin dev server**

Dev server is `localhost:8099` (test/test) per `project_mediadash.md`. Copy the published plugin:

```powershell
dotnet publish Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj -c Debug -o C:\dev\mediadash\bin\Debug\net9.0\publish
```

Then copy `bin/Debug/net9.0/publish/*` into the local Jellyfin plugin folder (per `CLAUDE.md` instructions; the exact `MediaDash_X.Y.Z.0` folder name depends on the current plugin version). Restart the local Jellyfin service.

- [ ] **Step 2: Exercise each manual-add path from the browser**

Open `http://localhost:8099` → log in → MediaDash plugin config → Files tab. For each of the following, right-click a file, pick the action, confirm the toast and then switch to the Issues tab to verify the row appears:

1. **Quality** on a video file → Issue row with Type=Quality, Status=Detected, empty details.
2. **HeavyTranscode** on a video file → row with Type=HeavyTranscode.
3. **Misplaced** on a video file → folder picker opens → pick a target → row with Type=Misplaced and `targetPath` populated.
4. **FormatCompatibility** on a converter-supported file (e.g. a `.mobi` ebook) → row created.
5. **FormatCompatibility** on an unsupported extension (e.g. `.mkv`) → toast / alert with "no converter" message, no row created.
6. **Protect this path** on any file → dialog → Protect → the path appears in Settings → Protection rules.
7. **Right-click on a folder** → only "Protect this path" appears (no Issues item).
8. **Right-click at the library roots pseudo-root** → menu does not open.
9. **Right-click in the recycle bin shortcut** → menu does not open.

Also quickly test on a touch device or Chrome DevTools touch emulation: long-press (500ms) opens the menu; move-while-pressing cancels.

- [ ] **Step 3: Exercise the dry-run fixer path**

For the Quality row created in step 2.1:
- Switch to the Issues tab → Approve → run the fixer in dry-run.
- Confirm the fixer picks up the manually-added row and reports its planned action.

(Full live-fix run is covered by the pre-push sweep per `feedback_mediadash_prepush_sweep.md`, not this plan.)

- [ ] **Step 4: Commit (if any ad-hoc fixups were needed above)**

```powershell
git status
```
If anything changed, commit with a message describing the fixup.

---

## Task 9: CHANGELOG + close out

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add the bullet**

Per `feedback_mediadash_changelog_bullets.md` and `feedback_changelog_on_completion.md`: one bullet per user-visible change. At the top of `CHANGELOG.md` under the current unreleased section (or add an unreleased section if none exists — follow the file's existing shape), insert:

```markdown
- Added right-click / long-press "Add to issues" and "Protect this path" to the Files tab, so a user can push a specific file into the issue queue (Quality / HeavyTranscode / Misplaced / FormatCompatibility) without widening scan settings.
```

- [ ] **Step 2: Commit**

```powershell
git add CHANGELOG.md
git commit -m "Changelog: manual add-to-issues from Files tab"
```

- [ ] **Step 3: Full test suite one more time**

```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj
```
Expected: all green.

- [ ] **Step 4: Stop here**

Do **not** push, do **not** run the release tool, do **not** bump versions. Per `feedback_mediadash_no_live_without_explicit.md` and `project_mediadash.md`: every publishing step wants explicit per-step approval from the user.

Report back with: tasks completed, test results, commits made (hashes), and ask whether to proceed to the pre-push sweep.
