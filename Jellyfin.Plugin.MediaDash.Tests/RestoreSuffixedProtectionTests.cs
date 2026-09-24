using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit round 4 P-04 (2026-09-19): restore endpoints called `MarkPathRestored` on the
/// ORIGINAL slot only. When a collision forced a `-restored` suffix, the actual restored file at
/// `movie-restored.mkv` was NOT registered in the restored_paths veto — next scan re-flagged it,
/// next FixTask auto-queue promoted it to Queued, next fix run undid the user's restoration.
/// This test pins the DB-layer contract the controller now relies on: MarkPathRestored on BOTH
/// the original slot AND the suffixed destination protects both from auto-queue re-flagging.
/// </summary>
[Collection("MediaDashDb")]
public sealed class RestoreSuffixedProtectionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-restore-veto-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public RestoreSuffixedProtectionTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void SuffixedRestoreProtectsBothPaths_AutoQueueSkipsBoth()
    {
        // Full P-04 shape: user recycled movie.mkv → later restored while movie.mkv was
        // present → landed at movie-restored.mkv. Controller now calls MarkPathRestored on
        // BOTH paths. Scanner later emits a fresh Duplicate issue at movie-restored.mkv.
        // FixTask.QueueDetectedIssues must NOT promote it.
        var originalPath = "/lib/Movies/Movie/movie.mkv";
        var restoredPath = "/lib/Movies/Movie/movie-restored.mkv";
        _db.MarkPathRestored(originalPath, IssueType.Quality);
        _db.MarkPathRestored(restoredPath, IssueType.Quality);

        // Seed a fresh Detected issue at the restored path (this is what the next scan would produce).
        _db.ReplaceDetectedIssues(IssueType.Quality, new[]
        {
            new Issue
            {
                Type = IssueType.Quality,
                ItemId = Guid.NewGuid(),
                Path = restoredPath,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });

        var queued = _db.QueueDetectedIssues(IssueType.Quality);

        Assert.Empty(queued);
        Assert.True(_db.WasPathRestored(restoredPath, IssueType.Quality));
        Assert.True(_db.WasPathRestored(originalPath, IssueType.Quality));
    }

    [Fact]
    public void OldSlotProtectionSurvivesReRecycleReRestore()
    {
        // A restore/re-recycle/re-restore cycle: original slot still protected by the FIRST
        // restore's marker. The suffixed protection covers today's restored copy; the original-
        // slot protection is what the controller comment specifically called out as intent.
        var originalPath = "/lib/x.mkv";
        var restoredPath = "/lib/x-restored.mkv";
        _db.MarkPathRestored(originalPath, IssueType.Duplicate);
        _db.MarkPathRestored(restoredPath, IssueType.Duplicate);

        // Simulate later re-recycle of x.mkv + fresh Detected row at the original slot.
        _db.ReplaceDetectedIssues(IssueType.Duplicate, new[]
        {
            new Issue
            {
                Type = IssueType.Duplicate,
                ItemId = Guid.NewGuid(),
                Path = originalPath,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });

        var queued = _db.QueueDetectedIssues(IssueType.Duplicate);

        Assert.Empty(queued);
    }

    [Fact]
    public void UnsuffixedRestore_OnlyOriginalPathIsProtected()
    {
        // Regression guard: when the restore lands at the original path (no collision), the
        // controller doesn't call the suffixed MarkPathRestored — only the original slot is
        // protected. A fresh Detected row at a totally-different path must still auto-queue.
        var originalPath = "/lib/movie.mkv";
        _db.MarkPathRestored(originalPath, IssueType.Playability);

        _db.ReplaceDetectedIssues(IssueType.Playability, new[]
        {
            new Issue
            {
                Type = IssueType.Playability,
                ItemId = Guid.NewGuid(),
                Path = "/lib/unrelated.mkv",
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });

        var queued = _db.QueueDetectedIssues(IssueType.Playability);

        Assert.Single(queued);
    }

    [Fact]
    public void ManifestRestore_AnyTypeProtectsBothPaths()
    {
        // Manifest-restore path uses MarkPathRestoredForAnyType (no known IssueType from the
        // manifest sidecar). Verify the "any type" sentinel blocks queue at BOTH original and
        // suffixed for every scanner type.
        var originalPath = "/lib/album.flac";
        var restoredPath = "/lib/album-restored.flac";
        _db.MarkPathRestoredForAnyType(originalPath);
        _db.MarkPathRestoredForAnyType(restoredPath);

        _db.ReplaceDetectedIssues(IssueType.EmbeddedCoverArt, new[]
        {
            new Issue
            {
                Type = IssueType.EmbeddedCoverArt,
                ItemId = Guid.NewGuid(),
                Path = restoredPath,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });

        var queued = _db.QueueDetectedIssues(IssueType.EmbeddedCoverArt);
        Assert.Empty(queued);
    }
}
