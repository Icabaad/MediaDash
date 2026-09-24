using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Guards issue #64: WipeDetectedForTypes must clear only Detected rows for the given types
// and never touch Queued / Dismissed (user's approval / suppression outlives a config edit).
[Collection("MediaDashDb")]
public sealed class MediaDashDbWipeDetectedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-wipe-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public MediaDashDbWipeDetectedTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static Issue Make(IssueType type, string path) => new()
    {
        Type = type,
        ItemId = Guid.NewGuid(),
        Path = path,
        Status = IssueStatus.Detected,
        DetectedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void WipeDetectedForTypes_RemovesOnlyTheGivenTypes()
    {
        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, [Make(IssueType.AudioLanguage, "A")]);
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make(IssueType.Playability, "B")]);

        var removed = _db.WipeDetectedForTypes(new[] { IssueType.AudioLanguage });

        Assert.Equal(1, removed);
        Assert.Empty(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected));
        Assert.Single(_db.GetIssues(IssueType.Playability, IssueStatus.Detected));
    }

    [Fact]
    public void WipeDetectedForTypes_PreservesQueuedAndDismissed()
    {
        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, [Make(IssueType.AudioLanguage, "A"), Make(IssueType.AudioLanguage, "B"), Make(IssueType.AudioLanguage, "C")]);
        var rows = _db.GetIssues(IssueType.AudioLanguage);
        _db.TryTransitionIssueStatus(rows[0].Id, [IssueStatus.Detected], IssueStatus.Queued);
        _db.TryTransitionIssueStatus(rows[1].Id, [IssueStatus.Detected], IssueStatus.Dismissed);
        // rows[2] stays Detected

        var removed = _db.WipeDetectedForTypes(new[] { IssueType.AudioLanguage });

        Assert.Equal(1, removed);
        Assert.Single(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Queued));
        Assert.Single(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Dismissed));
        Assert.Empty(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected));
    }

    [Fact]
    public void WipeDetectedForTypes_EmptySetIsNoOp()
    {
        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, [Make(IssueType.AudioLanguage, "A")]);

        var removed = _db.WipeDetectedForTypes(Array.Empty<IssueType>());

        Assert.Equal(0, removed);
        Assert.Single(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected));
    }

    [Fact]
    public void WipeDetectedForTypes_MultipleTypesInOneCall()
    {
        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, [Make(IssueType.AudioLanguage, "A")]);
        _db.ReplaceDetectedIssues(IssueType.SubtitleLanguage, [Make(IssueType.SubtitleLanguage, "B")]);
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make(IssueType.Playability, "C")]);

        var removed = _db.WipeDetectedForTypes(new[] { IssueType.AudioLanguage, IssueType.SubtitleLanguage });

        Assert.Equal(2, removed);
        Assert.Empty(_db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected));
        Assert.Empty(_db.GetIssues(IssueType.SubtitleLanguage, IssueStatus.Detected));
        Assert.Single(_db.GetIssues(IssueType.Playability, IssueStatus.Detected));
    }
}
