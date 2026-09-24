using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// 03-C plan-mandated fixture for the EmbeddedCoverArtFixer migration. Emulates the exact
/// wiring the fixer produces around <see cref="VerifiedReplacementService"/>: per-audio-file
/// same-path swap, verify checks live inside the caller (probe the rewrite: no remaining
/// cover stream AND audio-stream count preserved). This fixture proves the service refuses
/// commit when the post-strip verify surfaces "audio streams lost" via the verify callback
/// error — the pre-migration bug where the caller's verify was inline but no service
/// existed to isolate it.
/// </summary>
[Collection("Diagnostics")]
public sealed class CoverArtReplacementIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>Initializes a new instance.</summary>
    public CoverArtReplacementIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cart-" + Guid.NewGuid().ToString("N"));
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
    public async Task VerifyReturnsAudioLostError_RefusesAndSourceUntouched()
    {
        var source = Path.Combine(_tempDir, "song.flac");
        File.WriteAllBytes(source, new byte[] { 0xF1, 0xAC });
        var srcInfo = new FileInfo(source);
        var srcBytes = File.ReadAllBytes(source);

        var tempPath = Path.Combine(_tempDir, "song.flac.strip");
        File.WriteAllBytes(tempPath, new byte[] { 0xDE, 0xAD });

        var recycled = false;
        Func<string, string> failIfRecycled = _ =>
        {
            recycled = true;
            throw new InvalidOperationException("recycle must NOT run on verify refusal");
        };
        var service = new VerifiedReplacementService(failIfRecycled, new SystemIOReplacementFileOperations(), logger: null);

        var request = new ReplacementRequest(
            source,
            tempPath,
            source,
            srcInfo.Length,
            srcInfo.LastWriteTimeUtc.Ticks,
            RecycleOriginal: true);

        // EmbeddedCoverArtFixer's real caller runs verify inline BEFORE calling ReplaceAsync
        // and passes a null-returning callback here; but for the fault matrix we exercise the
        // "verify surfaces caller's specific error" shape to prove the service's refusal is
        // faithful to what a caller-driven strip would refuse on.
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>("rewritten file lost audio streams (2 → 0)"),
            () => PolicyDecision.Allow,
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.VerificationFailed, result.Reason);
        Assert.Contains("audio streams", result.TechnicalDetail);
        Assert.Equal(srcBytes, File.ReadAllBytes(source));
        Assert.False(recycled);
    }

    [Fact]
    public async Task HappyPath_RecyclesOriginalAndCommitsStrippedBytes()
    {
        var source = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllBytes(source, new byte[] { 0x11, 0x22, 0x33 });
        var srcInfo = new FileInfo(source);

        var tempPath = Path.Combine(_tempDir, "song.mp3.strip");
        File.WriteAllBytes(tempPath, new byte[] { 0xAA, 0xBB });

        string? recycled = null;
        Func<string, string> recycleTo = path =>
        {
            var bin = Path.Combine(_tempDir, "bin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bin);
            var target = Path.Combine(bin, Path.GetFileName(path));
            File.Move(path, target);
            recycled = target;
            return target;
        };
        var service = new VerifiedReplacementService(recycleTo, new SystemIOReplacementFileOperations(), logger: null);

        var request = new ReplacementRequest(
            source,
            tempPath,
            source,
            srcInfo.Length,
            srcInfo.LastWriteTimeUtc.Ticks,
            RecycleOriginal: true);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => PolicyDecision.Allow,
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.NotNull(recycled);
        Assert.Equal(recycled, result.RecyclePath);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, File.ReadAllBytes(source));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, File.ReadAllBytes(recycled));
    }
}
