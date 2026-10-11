using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Covers MediaDashDb.ParseRepairRungLabel (pure) and GetRepairSummary (SQL aggregation over the
// history table). The parser is the sole owner of the action-string → rung-key contract used by
// the Overview "Broken files repaired" panel; any drift between PlayabilityFixer's action
// strings and this parser silently buckets rungs into "other".
public sealed class RepairSummaryTests
{
    [Theory]
    [InlineData("repaired movie.mkv (quick remux)", "remux")]
    [InlineData("repaired movie.mkv (dropped 1 broken stream)", "drop")]
    [InlineData("repaired movie.mkv (dropped broken streams)", "drop")]
    [InlineData("repaired movie.mkv (container changed to .mkv (Jellyfin watch history reset))", "coerce")]
    [InlineData("repaired movie.mkv (audio re-encoded)", "audio-reencode")]
    [InlineData("repaired movie.mkv (video re-encoded)", "reencode")]
    [InlineData("partially repaired movie.mkv (quick remux) — 2.5s of unrecoverable tail damage was trimmed (declared 14.0s, playable 11.5s)", "remux")]
    [InlineData("partially repaired movie.mkv (video re-encoded) — 0.5s of unrecoverable tail damage was trimmed (declared 90.0s, playable 89.5s)", "reencode")]
    [InlineData("partially repaired movie.mkv (dropped broken streams) — playable duration is 12.0s shorter than originally declared (declared 24.0s, playable 12.0s)", "drop")]
    public void ParseRepairRungLabelExtractsKnownRungs(string action, string expected)
    {
        Assert.Equal(expected, MediaDashDb.ParseRepairRungLabel(action));
    }

    [Theory]
    [InlineData("", "other")]
    [InlineData("removed unplayable file movie.mkv", "other")]
    [InlineData("repaired movie.mkv (some future rung nobody added yet)", "other")]
    [InlineData("repaired movie.mkv without any parens", "other")]
    public void ParseRepairRungLabelBucketsUnknownAsOther(string action, string expected)
    {
        Assert.Equal(expected, MediaDashDb.ParseRepairRungLabel(action));
    }

    [Collection("MediaDashDb")]
    public sealed class GetRepairSummaryTests : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-repair-test-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly MediaDashDb _db;

        public GetRepairSummaryTests()
        {
            _db = new MediaDashDb(_dbPath);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        [Fact]
        public void EmptyHistoryReturnsAllZero()
        {
            var summary = _db.GetRepairSummary(DateTime.UtcNow.AddDays(-30).Ticks);
            Assert.Equal(0, summary.CountLifetime);
            Assert.Equal(0, summary.Count30d);
            Assert.Equal(0, summary.SavedBytesLifetime);
            Assert.Equal(0, summary.SavedBytes30d);
            Assert.Empty(summary.Rungs30d);
        }

        [Fact]
        public void PartialRepairsCountInLifetimeWindowAndRungs()
        {
            var now = DateTime.UtcNow;
            _db.AddHistory(Make("recent.mkv", "partially repaired recent.mkv (quick remux)", 100, now));
            _db.AddHistory(Make("old.mkv", "partially repaired old.mkv (video re-encoded)", 200, now.AddDays(-45)));
            _db.AddHistory(Make("dry.mkv", "partially repaired dry.mkv (quick remux)", 300, now, dryRun: true));
            _db.AddHistory(Make("fail.mkv", "partially repaired fail.mkv (quick remux)", 400, now, success: false));
            var summary = _db.GetRepairSummary(now.AddDays(-30).Ticks);
            Assert.Equal(2, summary.CountLifetime);
            Assert.Equal(300, summary.SavedBytesLifetime);
            Assert.Equal(1, summary.Count30d);
            Assert.Equal(100, summary.SavedBytes30d);
            var rung = Assert.Single(summary.Rungs30d);
            Assert.Equal("remux", rung.Rung);
            Assert.Equal(1, rung.Count);
            Assert.Equal(100, rung.SavedBytes);
        }

        [Fact]
        public void SumsPerRungWithinWindowAndAllTime()
        {
            var now = DateTime.UtcNow;
            _db.AddHistory(Make("a.mkv", "repaired a.mkv (quick remux)", savedBytes: 100, atUtc: now.AddDays(-1)));
            _db.AddHistory(Make("b.mkv", "repaired b.mkv (quick remux)", savedBytes: 200, atUtc: now.AddDays(-5)));
            _db.AddHistory(Make("c.mkv", "repaired c.mkv (video re-encoded)", savedBytes: 900, atUtc: now.AddDays(-10)));
            _db.AddHistory(Make("d.mkv", "repaired d.mkv (audio re-encoded)", savedBytes: 50, atUtc: now.AddDays(-45))); // outside 30d window
            _db.AddHistory(Make("e.mkv", "removed unplayable file e.mkv", savedBytes: 0, atUtc: now.AddDays(-2))); // not a repair row — must not count

            var summary = _db.GetRepairSummary(now.AddDays(-30).Ticks);

            Assert.Equal(4, summary.CountLifetime);
            Assert.Equal(3, summary.Count30d);
            Assert.Equal(1250, summary.SavedBytesLifetime);
            Assert.Equal(1200, summary.SavedBytes30d);

            // 30d rungs ordered biggest-count-first.
            var remux = summary.Rungs30d.Single(r => r.Rung == "remux");
            Assert.Equal(2, remux.Count);
            Assert.Equal(300, remux.SavedBytes);
            var reencode = summary.Rungs30d.Single(r => r.Rung == "reencode");
            Assert.Equal(1, reencode.Count);
            Assert.Equal(900, reencode.SavedBytes);
            Assert.Equal("remux", summary.Rungs30d[0].Rung); // sorted biggest first
            Assert.DoesNotContain(summary.Rungs30d, r => r.Rung == "audio-reencode"); // outside window
        }

        [Fact]
        public void DryRunAndFailureRowsAreExcluded()
        {
            var now = DateTime.UtcNow;
            _db.AddHistory(Make("dry.mkv", "repaired dry.mkv (quick remux)", savedBytes: 100, atUtc: now, dryRun: true));
            _db.AddHistory(Make("fail.mkv", "repaired fail.mkv (quick remux)", savedBytes: 100, atUtc: now, success: false));
            _db.AddHistory(Make("real.mkv", "repaired real.mkv (quick remux)", savedBytes: 100, atUtc: now));

            var summary = _db.GetRepairSummary(now.AddDays(-30).Ticks);

            Assert.Equal(1, summary.CountLifetime);
            Assert.Equal(100, summary.SavedBytesLifetime);
        }

        private static HistoryEntry Make(string path, string action, long savedBytes, DateTime atUtc, bool dryRun = false, bool success = true) => new()
        {
            IssueId = Random.Shared.NextInt64(1, long.MaxValue),
            Type = IssueType.Playability,
            Path = path,
            Action = action,
            BytesFreed = 0,
            SavedBytes = savedBytes,
            RecyclePath = "/bin/" + path,
            FixedAtUtc = atUtc,
            WasDryRun = dryRun,
            Success = success,
        };
    }
}
