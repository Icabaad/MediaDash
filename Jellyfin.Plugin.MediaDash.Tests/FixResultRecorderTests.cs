using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Contract tests for <see cref="FixResultRecorder"/>. Every scenario the plan checklist for §04-B
/// calls out gets a targeted assertion here: success primary + companions, dry-run, failed pair,
/// CompleteLegacy failure, dismissed-mid-run conflict, sidecar fan-out, and the SQLite rollback
/// guarantee via the underlying <see cref="MediaDashDb.RecordFixOutcome"/> transaction.
/// </summary>
[Collection("MediaDashDb")]
public sealed class FixResultRecorderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-recorder-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly FixResultRecorder _recorder;

    public FixResultRecorderTests()
    {
        _db = new MediaDashDb(_dbPath);
        _recorder = new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private long SeedQueuedIssue(IssueType type, string path)
    {
        _db.ReplaceDetectedIssues(type, new[]
        {
            new Issue
            {
                Type = type,
                Path = path,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
            },
        });
        var id = _db.GetIssues(type)[0].Id;
        var applied = _db.TryTransitionIssueStatus(id, new[] { IssueStatus.Detected }, IssueStatus.Queued);
        Assert.Equal(StatusTransitionResult.Applied, applied);
        return id;
    }

    private Issue LoadIssue(long id)
    {
        return _db.GetIssues().Single(i => i.Id == id);
    }

    [Fact]
    public void Record_SinglePrimarySuccess_WritesOneHistoryRowAndMarksFixed()
    {
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/movie.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = new FixResult
        {
            Success = true,
            Message = "Repaired via remux",
            BytesFreed = 500,
            RecyclePath = "/bin/movie.mkv",
        };

        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var history = _db.GetHistory();
        var row = Assert.Single(history);
        Assert.Equal(id, row.IssueId);
        Assert.True(row.Success);
        Assert.False(row.WasDryRun);
        Assert.Equal(500, row.BytesFreed);
        Assert.Equal("/bin/movie.mkv", row.RecyclePath);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_TranscodeWithCompanions_WritesThreeRowsAndMarksAllFixedWithSingleBytesAttribution()
    {
        var transcodeId = SeedQueuedIssue(IssueType.Quality, "/lib/tv/E.mkv");
        var audioId = SeedQueuedIssue(IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subId = SeedQueuedIssue(IssueType.SubtitleLanguage, "/lib/tv/E.mkv");
        var primary = LoadIssue(transcodeId);
        var audio = LoadIssue(audioId);
        var subtitle = LoadIssue(subId);
        var plan = new PlannedFix(PlannedFixKind.TranscodeWithTracks, primary, new[] { audio, subtitle });
        var result = new FixResult
        {
            Success = true,
            Message = "Re-encoded to 1080p HEVC",
            BytesFreed = 1_000_000_000,
            RecyclePath = "/bin/E.mkv",
        };

        _recorder.Record(
            plan,
            new FixExecutionResult(result, transcodeId, new[] { transcodeId, audioId, subId }),
            DateTime.UtcNow);

        var history = _db.GetHistory();
        Assert.Equal(3, history.Count);
        Assert.Equal(1_000_000_000, history.Sum(r => r.BytesFreed)); // single attribution
        var primaryRow = history.Single(r => r.IssueId == transcodeId);
        Assert.Equal(1_000_000_000, primaryRow.BytesFreed);
        Assert.Equal("Re-encoded to 1080p HEVC", primaryRow.Action);
        var audioRow = history.Single(r => r.IssueId == audioId);
        Assert.Equal(0, audioRow.BytesFreed);
        Assert.StartsWith("Resolved by transcode pass: ", audioRow.Action);
        Assert.Equal("/bin/E.mkv", audioRow.RecyclePath);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(transcodeId).Status);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(audioId).Status);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(subId).Status);
    }

    [Fact]
    public void Record_TrackPairSuccess_UsesVerbatimMessageOnPartner()
    {
        // Combined-pass partner: history row action = primary's message verbatim (no
        // "Resolved by transcode pass:" prefix). Same bin path so both entries link back to the
        // one recycled file.
        var audioId = SeedQueuedIssue(IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subId = SeedQueuedIssue(IssueType.SubtitleLanguage, "/lib/tv/E.mkv");
        var audio = LoadIssue(audioId);
        var subtitle = LoadIssue(subId);
        var plan = new PlannedFix(PlannedFixKind.TrackPair, audio, new[] { subtitle });
        var result = new FixResult
        {
            Success = true,
            Message = "Removed 2 audio + 3 subtitle tracks in one pass",
            RecyclePath = "/bin/E.mkv",
        };

        _recorder.Record(
            plan,
            new FixExecutionResult(result, audioId, new[] { audioId, subId }),
            DateTime.UtcNow);

        var partnerRow = _db.GetHistory().Single(r => r.IssueId == subId);
        Assert.Equal("Removed 2 audio + 3 subtitle tracks in one pass", partnerRow.Action);
        Assert.DoesNotContain("Resolved by transcode pass:", partnerRow.Action);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(subId).Status);
    }

    [Fact]
    public void Record_SidecarFanout_WritesOneRowPerSidecarWithZeroBytesFreed()
    {
        var id = SeedQueuedIssue(IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = new FixResult
        {
            Success = true,
            Message = "Removed 2 unwanted audio tracks",
            BytesFreed = 800,
            RecyclePath = "/bin/E.mkv",
            AdditionalRecycled = new[]
            {
                new RecycledSidecar { OriginalPath = "/lib/tv/E.en.srt", RecyclePath = "/bin/E.en.srt", Action = "Recycled external subtitle sidecar." },
                new RecycledSidecar { OriginalPath = "/lib/tv/E.fr.srt", RecyclePath = "/bin/E.fr.srt", Action = "Recycled external subtitle sidecar." },
            },
        };

        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var history = _db.GetHistory();
        Assert.Equal(3, history.Count);
        Assert.Equal(3, history.Select(r => r.RecyclePath).Distinct().Count());
        Assert.All(history.Where(r => r.Path.EndsWith(".srt", StringComparison.Ordinal)), r =>
        {
            Assert.Equal(0, r.BytesFreed); // sidecars carry zero savings
            Assert.True(r.Success);
            Assert.False(r.WasDryRun);
        });
        Assert.Equal(800, history.Sum(r => r.BytesFreed));
    }

    [Fact]
    public void Record_DryRunSuccess_WritesHistoryButLeavesQueuedStateUntouched()
    {
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/movie.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = new FixResult
        {
            Success = true,
            WasDryRun = true,
            Message = "Preview only — would have re-encoded",
            BytesFreed = 500,
        };

        // Caller passes { id } in ResolvedIssueIds; recorder must swallow the transition slot in
        // dry-run and leave status Queued. F-206.
        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var row = Assert.Single(_db.GetHistory());
        Assert.True(row.WasDryRun);
        Assert.Equal(0, row.BytesFreed); // BytesFreed suppressed in dry-run per recorder invariant
        Assert.Equal(IssueStatus.Queued, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_FailedTrackPair_WritesPrimaryFailureOnlyAndPartnerStaysQueued()
    {
        // Failed combined-pass: executor returns ResolvedIssueIds=[]. Partner must NOT get a
        // success history row and must stay Queued for the next retry.
        var audioId = SeedQueuedIssue(IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subId = SeedQueuedIssue(IssueType.SubtitleLanguage, "/lib/tv/E.mkv");
        var audio = LoadIssue(audioId);
        var subtitle = LoadIssue(subId);
        var plan = new PlannedFix(PlannedFixKind.TrackPair, audio, new[] { subtitle });
        var result = FixResult.Fail("ffmpeg reported: Invalid data found when processing input");

        _recorder.Record(plan, new FixExecutionResult(result, audioId, Array.Empty<long>()), DateTime.UtcNow);

        var history = _db.GetHistory();
        var row = Assert.Single(history);
        Assert.Equal(audioId, row.IssueId);
        Assert.False(row.Success);
        Assert.Equal(IssueStatus.Queued, LoadIssue(audioId).Status);
        Assert.Equal(IssueStatus.Queued, LoadIssue(subId).Status);
    }

    [Fact]
    public void Record_CompleteLegacyFailure_KeepsFailureHistoryAndMarksPrimaryFixed()
    {
        // Typed terminal refusal: primary carries a Failure history row (Success=false, message
        // preserved) AND its status advances out of Queued so the retry loop stops. Companions
        // are never in ResolvedIssueIds on failure — this test uses a Single-shape plan for
        // clarity.
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/gone.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = FixResult.Fail(FixReason.SourceMissing, "File went missing between scan and fix");

        // FixTask computes { id } for CompleteLegacy failures — the recorder just consumes.
        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var row = Assert.Single(_db.GetHistory());
        Assert.False(row.Success);
        Assert.Contains("went missing", row.Action);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_ConflictingDismissalStaysDismissed()
    {
        // User dismissed the issue mid-fix. Recorder still writes history (reality) but the
        // transition attempt returns false and the row keeps its Dismissed status.
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/movie.mkv");
        _db.TryTransitionIssueStatus(id, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
        var primary = LoadIssue(id);
        Assert.Equal(IssueStatus.Dismissed, primary.Status);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = new FixResult
        {
            Success = true,
            Message = "Repaired via remux",
            BytesFreed = 100,
            RecyclePath = "/bin/movie.mkv",
        };

        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var row = Assert.Single(_db.GetHistory());
        Assert.True(row.Success);
        Assert.Equal(IssueStatus.Dismissed, LoadIssue(id).Status);
    }

    [Fact]
    public void RecordFixOutcome_ForcedInsertFailure_RollsBackHistoryAndStatus()
    {
        // Real SQLite rollback: force a NOT NULL constraint violation on the second row insert
        // and assert the transaction leaves ZERO history rows AND leaves the issue's status
        // exactly where it started. Plan §04-B: "verify original file-operation result is still
        // observable to caller" — the caller's FixResult stays intact because it never entered
        // the DB layer; only the recorder's DB write reverts.
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/movie.mkv");
        var goodRow = new HistoryEntry
        {
            IssueId = id,
            Type = IssueType.Playability,
            Path = "/lib/movie.mkv",
            Action = "first row would have committed",
            FixedAtUtc = DateTime.UtcNow,
            Success = true,
        };
        var badRow = new HistoryEntry
        {
            IssueId = id,
            Type = IssueType.Playability,
            Path = null!, // history.path is NOT NULL — this will raise a SqliteException
            Action = "second row triggers rollback",
            FixedAtUtc = DateTime.UtcNow,
            Success = true,
        };

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            _db.RecordFixOutcome(
                new[] { goodRow, badRow },
                new[] { id },
                new[] { IssueStatus.Queued },
                IssueStatus.Fixed));

        Assert.Empty(_db.GetHistory());
        Assert.Equal(IssueStatus.Queued, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_FailedInsufficientSavings_PromotesFixedToDismissedToBreakRescanLoop()
    {
        // External audit finding P2-F4: pre-fix a Failed+InsufficientSavings outcome flipped the
        // primary Queued→Fixed but the scanner's re-insert NOT EXISTS clause only suppresses
        // Queued/Dismissed rows, so the next scan re-detected the same file, auto-queued, and
        // re-encoded. Post-fix the recorder promotes Fixed→Dismissed on this specific outcome so
        // the existing suppression path closes the loop.
        var id = SeedQueuedIssue(IssueType.Quality, "/lib/movie.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = FixResult.Fail(FixReason.InsufficientSavings, "Saved only 12% — under the configured minimum. Original kept.");

        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        var row = Assert.Single(_db.GetHistory());
        Assert.False(row.Success);
        Assert.Equal(IssueStatus.Dismissed, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_DryRunInsufficientSavings_LeavesQueuedUntouched()
    {
        // A dry-run must never mutate persistent state (F-206). Even for InsufficientSavings the
        // auto-dismiss must skip when WasDryRun is true.
        var id = SeedQueuedIssue(IssueType.Quality, "/lib/movie.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = FixResult.Fail(FixReason.InsufficientSavings, "Saved only 12% — under the configured minimum. Original kept.");
        result.WasDryRun = true;

        // Dry-run branch in FixExecutor.ComputeResolvedIds returns an empty ResolvedIssueIds set,
        // which means no Queued→Fixed transition happens above, and therefore no promotion.
        _recorder.Record(plan, new FixExecutionResult(result, id, Array.Empty<long>()), DateTime.UtcNow);

        Assert.Equal(IssueStatus.Queued, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_FailedSourceMissing_StaysFixedNoAutoDismiss()
    {
        // Control: other CompleteLegacy failure reasons must NOT dismiss — the "same input →
        // same failure" invariant is specific to InsufficientSavings. SourceMissing might resolve
        // if the file reappears, so Fixed is the correct terminal state (matches existing
        // FixResultPolicy contract).
        var id = SeedQueuedIssue(IssueType.Playability, "/lib/gone.mkv");
        var primary = LoadIssue(id);
        var plan = new PlannedFix(PlannedFixKind.Single, primary, Array.Empty<Issue>());
        var result = FixResult.Fail(FixReason.SourceMissing, "File went missing between scan and fix");

        _recorder.Record(plan, new FixExecutionResult(result, id, new[] { id }), DateTime.UtcNow);

        Assert.Equal(IssueStatus.Fixed, LoadIssue(id).Status);
    }

    [Fact]
    public void Record_RevokedCompanionAbsentFromResolvedIds_WritesPrimaryOnly()
    {
        // Executor detected the subtitle partner was Dismissed at pre-execution check and dropped
        // to audio-only. plan.Companions still names it (the plan is frozen at build time), but
        // ResolvedIssueIds carries only the primary. Recorder must honor the executor's judgment.
        var audioId = SeedQueuedIssue(IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subId = SeedQueuedIssue(IssueType.SubtitleLanguage, "/lib/tv/E.mkv");
        _db.TryTransitionIssueStatus(subId, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
        var audio = LoadIssue(audioId);
        var subtitle = LoadIssue(subId);
        var plan = new PlannedFix(PlannedFixKind.TrackPair, audio, new[] { subtitle });
        var result = new FixResult
        {
            Success = true,
            Message = "Removed 2 audio tracks",
            RecyclePath = "/bin/E.mkv",
        };

        _recorder.Record(plan, new FixExecutionResult(result, audioId, new[] { audioId }), DateTime.UtcNow);

        var history = _db.GetHistory();
        var row = Assert.Single(history);
        Assert.Equal(audioId, row.IssueId);
        Assert.Equal(IssueStatus.Fixed, LoadIssue(audioId).Status);
        Assert.Equal(IssueStatus.Dismissed, LoadIssue(subId).Status);
    }
}
