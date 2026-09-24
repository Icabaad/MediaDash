using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// 03-C plan-mandated fixture for the TrackFixer migration. Emulates the exact wiring
/// TrackFixer produces around <see cref="VerifiedReplacementService"/>: same-path swap
/// (destination == source), verified remux output, policy recheck at commit time. The
/// external subtitle sidecar loop is a separate effect kept in TrackFixer (produces
/// `AdditionalRecycled` rows independent of the primary source replacement) — that
/// loop is exercised by existing `TrackFixerMp4MuxerSubtitleTests` / `IssueRevertTests`
/// / `RestoreResolveNonCollidingPathTests` and is not re-tested here.
/// </summary>
[Collection("Diagnostics")]
public sealed class TrackReplacementIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>Initializes a new instance.</summary>
    public TrackReplacementIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "trkrit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Cleans up the temp directory.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DryRunFlippedOnMidRemux_RefusesCommit_SourceUntouched()
    {
        var source = Path.Combine(_tempDir, "episode.mkv");
        File.WriteAllBytes(source, new byte[] { 0x11, 0x22, 0x33 });
        var srcBytes = File.ReadAllBytes(source);
        var srcInfo = new FileInfo(source);

        var tempPath = Path.Combine(_tempDir, "episode.mkv.tmp");
        File.WriteAllBytes(tempPath, new byte[] { 0x99, 0x88, 0x77 });

        var recycled = false;
        Func<string, string> failIfRecycled = _ =>
        {
            recycled = true;
            throw new InvalidOperationException("recycle must NOT run when policy revokes at commit");
        };
        var service = new VerifiedReplacementService(failIfRecycled, new SystemIOReplacementFileOperations(), logger: null);

        var request = new ReplacementRequest(
            source,
            tempPath,
            source, // TrackFixer is always same-path
            srcInfo.Length,
            srcInfo.LastWriteTimeUtc.Ticks,
            RecycleOriginal: true);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => PolicyDecision.Refuse(FixReason.TypeDisabled),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.TypeDisabled, result.Reason);
        Assert.Equal(srcBytes, File.ReadAllBytes(source));
        Assert.True(File.Exists(tempPath), "TrackFixer's finally cleans up tempPath");
        Assert.False(recycled);
    }
}
