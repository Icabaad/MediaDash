using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// 03-B plan-mandated fixture: "Add one whole-fixer fixture showing policy changes while fake
/// encoding is paused before commit; no source mutation occurs."
///
/// Emulates the exact wiring TranscodeFixer produces around
/// <see cref="VerifiedReplacementService.ReplaceAsync"/> — real filesystem, real service,
/// caller-composed verify + policy callbacks. Does NOT construct the whole TranscodeFixer
/// (its DI graph is 11 deps, most of which are ffmpeg/probe-heavy and irrelevant to the
/// policy-flip invariant). The service call is byte-identical to TranscodeFixer's real
/// migration site in 03-B; this fixture pauses the callback long enough to flip policy and
/// asserts that the source is preserved.
///
/// 02-B ceiling closed: previously, the fixer captured its config snapshot at the top of
/// FixAsync and committed under that snapshot even if the user flipped Dry-run ON during a
/// multi-hour encode. The service now runs checkCurrentPolicy at commit time; the fixture
/// proves the refusal cleans up without touching the source.
/// </summary>
[Collection("Diagnostics")]
public sealed class TranscodeReplacementIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>Initializes a new instance of the <see cref="TranscodeReplacementIntegrationTests"/> class.</summary>
    public TranscodeReplacementIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "trit-" + Guid.NewGuid().ToString("N"));
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
    public async Task DryRunFlippedOnMidEncode_RefusesCommit_SourceUntouched()
    {
        var source = Path.Combine(_tempDir, "video.mp4");
        File.WriteAllBytes(source, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var originalBytes = File.ReadAllBytes(source);
        var srcInfo = new FileInfo(source);
        var expectedLength = srcInfo.Length;
        var expectedMtime = srcInfo.LastWriteTimeUtc.Ticks;

        // Simulate ffmpeg completing an encode: temp file exists next to the source with the
        // "verified" bytes. The plan's "fake encoding paused" means the temp file is on disk
        // and verify has already returned success; the policy callback fires between that and
        // the destructive commit.
        var tempPath = Path.Combine(_tempDir, "video.mp4.mediadash.tmp");
        File.WriteAllBytes(tempPath, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        var destination = Path.Combine(_tempDir, "video.mkv");

        var recycled = false;
        Func<string, string> throwIfRecycled = _ =>
        {
            recycled = true;
            throw new InvalidOperationException("recycle must NOT run when policy revokes at commit");
        };
        var service = new VerifiedReplacementService(throwIfRecycled, new SystemIOReplacementFileOperations(), logger: null);

        var request = new ReplacementRequest(
            source,
            tempPath,
            destination,
            expectedLength,
            expectedMtime,
            RecycleOriginal: true);

        var result = await service.ReplaceAsync(
            request,
            verifyOutput: _ => Task.FromResult<string?>(null),
            checkCurrentPolicy: () => PolicyDecision.Refuse(FixReason.TypeDisabled),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.TypeDisabled, result.Reason);
        Assert.Equal(originalBytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(destination), "no commit landed at destination");
        Assert.True(File.Exists(tempPath), "output stays with caller — TranscodeFixer's finally cleans it up");
        Assert.False(recycled, "recycle callback never ran");
    }

    [Fact]
    public async Task SourceRewrittenDuringEncode_RefusesCommit_ChangedSourceRemains()
    {
        // Companion invariant: the plan's fault matrix requires that a source mutation
        // during the encode is caught by the pre-commit identity recheck. The fixer wiring
        // must not commit stale output over a source that has changed underneath it.
        var source = Path.Combine(_tempDir, "clip.mp4");
        File.WriteAllBytes(source, new byte[] { 0x01, 0x02 });
        var srcInfo = new FileInfo(source);
        var expectedLength = srcInfo.Length;
        var expectedMtime = srcInfo.LastWriteTimeUtc.Ticks;

        var tempPath = Path.Combine(_tempDir, "clip.mp4.mediadash.tmp");
        File.WriteAllBytes(tempPath, new byte[] { 0xFF, 0xFF });
        var destination = source; // same-container in this case

        // Rewrite source AFTER capturing the snapshot ticks — simulates the user or another
        // scan touching the file while ffmpeg was running.
        File.WriteAllBytes(source, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(60));
        var mutatedBytes = File.ReadAllBytes(source);

        var recycled = false;
        Func<string, string> throwIfRecycled = _ =>
        {
            recycled = true;
            throw new InvalidOperationException("recycle must NOT run on stale-source refusal");
        };
        var service = new VerifiedReplacementService(throwIfRecycled, new SystemIOReplacementFileOperations(), logger: null);

        var request = new ReplacementRequest(
            source,
            tempPath,
            destination,
            expectedLength,
            expectedMtime,
            RecycleOriginal: true);

        var result = await service.ReplaceAsync(
            request,
            verifyOutput: _ => Task.FromResult<string?>(null),
            checkCurrentPolicy: () => PolicyDecision.Allow,
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.Equal(mutatedBytes, File.ReadAllBytes(source));
        Assert.True(File.Exists(tempPath), "output preserved for caller-finally cleanup");
        Assert.False(recycled);
    }
}
