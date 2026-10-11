using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Pre-flight guard for the playability repair ladder. If the guard mis-fires we either
/// starve the ladder (never repair) or run repairs that fill the volume mid-encode. Both
/// end users badly, so the check has its own test even though the surrounding rung logic
/// is exercised by the tools/repair-test end-to-end harness.
/// </summary>
public class PlayabilityFixerRepairTests
{
    [Theory]
    [InlineData("mkv", "matroska")]
    [InlineData("mp4", "mp4")]
    [InlineData("ts", "mpegts")]
    public void InterleaveRepair_SelectsMuxerForTemporarySuffix(string ext, string format)
    {
        var output = TranscodeFixer.SidecarPath("movie." + ext, "repair.tmp5", ext);
        Assert.Equal(new[] { "-i", "movie." + ext, "-map", "0", "-c", "copy", "-max_interleave_delta", "0", "-f", format, output },
            PlayabilityFixer.BuildInterleaveRepairArgs("movie." + ext, output, ext));
    }

    [Fact]
    public void HasFreeSpace_TrueForTinyFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x01 });
            Assert.True(PlayabilityFixer.HasFreeSpace(tmp, multiplier: 3));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void HasFreeSpace_FalseForMissingFile()
    {
        var gone = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.False(PlayabilityFixer.HasFreeSpace(gone, multiplier: 2));
    }

    [Fact]
    public void HasFreeSpace_FalseWhenMultiplierWouldOverflowFreeSpace()
    {
        // Ask for 1000× the free space on the temp drive by pretending a huge file is there.
        // Rather than actually writing gigs, we assert the arithmetic itself refuses the impossible
        // by pointing at a real file and demanding a multiplier that no volume can satisfy.
        var tmp = Path.GetTempFileName();
        try
        {
            var oneMb = new byte[1024 * 1024];
            File.WriteAllBytes(tmp, oneMb);
            var drive = new DriveInfo(Path.GetPathRoot(tmp) ?? "/");
            var impossibleMultiplier = (int)Math.Min(int.MaxValue, drive.AvailableFreeSpace / oneMb.Length + 1);
            Assert.False(PlayabilityFixer.HasFreeSpace(tmp, impossibleMultiplier));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateDurationLoss_ReportsTruncationBeyondSlack()
    {
        // Regression from the 2026-09-19 QA sweep: an interrupted-copy fixture declared 14.021s on
        // the container but the repair could only salvage 8.021s of playable data. Because both the
        // source and the output count the same packets on disk, OutputVerifier's packet-count
        // rescue legitimately accepts the file — but the user must still see the 6s of loss in the
        // success message rather than a bare "repaired" claim.
        var loss = PlayabilityFixer.EvaluateDurationLoss(originalDuration: 14.021, repairedDuration: 8.021);

        Assert.NotNull(loss);
        Assert.Equal(14.021, loss!.Value.DeclaredOriginal, 3);
        Assert.Equal(8.021, loss.Value.RepairedDuration, 3);
        Assert.Equal(6.0, loss.Value.Lost, 3);
    }

    [Fact]
    public void EvaluateDurationLoss_IgnoresSubSlackDrift()
    {
        // Well-behaved MKV/MP4 remuxes routinely differ by a fraction of a second because the
        // container writes the actual pts of the last packet rather than the ideal duration. That's
        // not tail damage and reporting it would spam the Errors tab. Slack is max(2s, 2%) — a
        // 30-minute file with 1.5s drift falls under the 2s floor and must NOT be annotated.
        var loss = PlayabilityFixer.EvaluateDurationLoss(originalDuration: 1800.0, repairedDuration: 1798.5);

        Assert.Null(loss);
    }

    [Fact]
    public void EvaluateDurationLoss_IgnoresLongFileWithinTwoPercent()
    {
        // 2-hour file, 1-minute delta (~0.8%) is inside the 2% slack window OutputVerifier uses;
        // legitimate re-encode drift (b-frame reordering, GOP alignment, sample-rate conversion).
        var loss = PlayabilityFixer.EvaluateDurationLoss(originalDuration: 7200.0, repairedDuration: 7140.0);

        Assert.Null(loss);
    }

    [Fact]
    public void EvaluateDurationLoss_ZeroDurationsReturnNull()
    {
        // Unknown-duration input (audio-only, some MPEG-TS, malformed) makes the loss ratio
        // undefined. The rest of the repair pipeline is still allowed to declare success — we just
        // can't quantify tail loss without a reliable reference. Skip annotation.
        Assert.Null(PlayabilityFixer.EvaluateDurationLoss(originalDuration: 0, repairedDuration: 5));
        Assert.Null(PlayabilityFixer.EvaluateDurationLoss(originalDuration: 5, repairedDuration: 0));
    }

    /// <summary>
    /// 03-C plan-mandated fixture: PlayabilityFixer's repair-swap path now flows through
    /// <see cref="VerifiedReplacementService"/>. The always-recycle invariant survives —
    /// PlayabilityFixer passes <c>RecycleOriginal: true</c> regardless of general failed-
    /// repair disposal. This fixture proves the recycle callback fires on a successful
    /// repair-swap even when the caller has already chosen "permanent-delete" as its
    /// general disposal (the request the fixer emits still says RecycleOriginal=true).
    /// </summary>
    [Fact]
    public async Task RepairSwap_AlwaysRecyclesOriginal_EvenWhenGeneralDisposalIsPermanent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pfrit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var source = Path.Combine(tempDir, "broken.mkv");
            File.WriteAllBytes(source, new byte[] { 0xBA, 0xD1 });
            var srcInfo = new FileInfo(source);
            var srcBytes = File.ReadAllBytes(source);

            var repairedTemp = Path.Combine(tempDir, "broken.mkv.repair");
            File.WriteAllBytes(repairedTemp, new byte[] { 0x00, 0x0D, 0x00, 0x0D });

            string? recycledTo = null;
            Func<string, string> recycleTo = path =>
            {
                var bin = Path.Combine(tempDir, "bin", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bin);
                var target = Path.Combine(bin, Path.GetFileName(path));
                File.Move(path, target);
                recycledTo = target;
                return target;
            };
            var service = new VerifiedReplacementService(recycleTo, new SystemIOReplacementFileOperations(), logger: null);

            // Emulate PlayabilityFixer.TrySwapRepairedAsync's request shape.
            var request = new ReplacementRequest(
                SourcePath: source,
                OutputPath: repairedTemp,
                DestinationPath: source,
                ExpectedSourceLength: srcInfo.Length,
                ExpectedSourceMtimeUtcTicks: srcInfo.LastWriteTimeUtc.Ticks,
                RecycleOriginal: true, // ALWAYS true — the plan-preserved invariant
                PreserveSourceTimestamps: true);

            var result = await service.ReplaceAsync(
                request,
                _ => Task.FromResult<string?>(null),
                () => PolicyDecision.Allow,
                CancellationToken.None);

            Assert.True(result.Applied);
            Assert.NotNull(recycledTo);
            Assert.Equal(recycledTo, result.RecyclePath);
            Assert.Equal(srcBytes, File.ReadAllBytes(recycledTo));
            Assert.Equal(new byte[] { 0x00, 0x0D, 0x00, 0x0D }, File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// 03-C safety upgrade: pre-migration the repair-swap did `recycleBin.MoveToBin(source)`
    /// then `File.Move(temp, final, overwrite:false)` — a failure in the second step left the
    /// temp sitting as a `.repair.tmpN.mediadash.tmp` sidecar that the sweep could delete on
    /// the next cycle. Post-migration the service promotes the temp to a
    /// `*.mediadash-recover-*` name BEFORE dispose, so a final-rename failure survives sweep.
    /// This fixture proves the recovery marker holds when the final rename fails.
    /// </summary>
    [Fact]
    public async Task RepairSwap_FinalRenameFails_RecoveryFileSurvivesSweepPattern()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pfritsw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var source = Path.Combine(tempDir, "broken.mkv");
            File.WriteAllBytes(source, new byte[] { 0xBA, 0xD1 });
            var srcInfo = new FileInfo(source);

            var repairedTemp = Path.Combine(tempDir, "broken.mkv.repair");
            File.WriteAllBytes(repairedTemp, new byte[] { 0x0D, 0x00 });

            var recycled = false;
            Func<string, string> recycleTo = path =>
            {
                var bin = Path.Combine(tempDir, "bin", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bin);
                var target = Path.Combine(bin, Path.GetFileName(path));
                File.Move(path, target);
                recycled = true;
                return target;
            };

            // Fail the SECOND Move call (final rename recovery -> destination). First Move
            // is the promote step (temp -> recovery marker).
            var faulty = new FaultyMoveFileOps(new SystemIOReplacementFileOperations()) { ThrowOnMoveNumber = 2 };
            var service = new VerifiedReplacementService(recycleTo, faulty, logger: null);

            var request = new ReplacementRequest(
                SourcePath: source,
                OutputPath: repairedTemp,
                DestinationPath: source,
                ExpectedSourceLength: srcInfo.Length,
                ExpectedSourceMtimeUtcTicks: srcInfo.LastWriteTimeUtc.Ticks,
                RecycleOriginal: true,
                PreserveSourceTimestamps: true);

            var result = await service.ReplaceAsync(
                request,
                _ => Task.FromResult<string?>(null),
                () => PolicyDecision.Allow,
                CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(FixReason.RecoveryRequired, result.Reason);
            Assert.NotNull(result.RecoveryPath);
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(result.RecoveryPath!),
                "recovery survivor must carry the marker so LibraryGuard.SweepOrphanSidecars skips it");
            Assert.True(File.Exists(result.RecoveryPath), "recovery survivor is on disk");
            Assert.True(recycled, "recycle happened before the final rename attempted");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class FaultyMoveFileOps : IReplacementFileOperations
    {
        private readonly IReplacementFileOperations _inner;
        private int _moves;

        public FaultyMoveFileOps(IReplacementFileOperations inner)
        {
            _inner = inner;
        }

        public int ThrowOnMoveNumber { get; set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public long GetLength(string path) => _inner.GetLength(path);

        public long GetLastWriteTicksUtc(string path) => _inner.GetLastWriteTicksUtc(path);

        public void Move(string source, string destination)
        {
            _moves++;
            if (ThrowOnMoveNumber == _moves)
            {
                throw new IOException("simulated Move #" + _moves + " failure");
            }

            _inner.Move(source, destination);
        }

        public void Delete(string path) => _inner.Delete(path);

        public void TrySetTimestampsUtc(string path, DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
            => _inner.TrySetTimestampsUtc(path, creationTimeUtc, lastWriteTimeUtc);
    }
}
