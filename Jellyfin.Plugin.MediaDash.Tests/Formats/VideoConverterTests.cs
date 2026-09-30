using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class VideoConverterTests : FormatFixtureBase
{
    private static VideoConverter Converter() => new();

    [Fact]
    public void KeyIsVideo()
    {
        Assert.Equal("video", Converter().Key);
    }

    [Fact]
    public void CanConvertRmvb()
    {
        Assert.True(Converter().CanConvert(".rmvb"));
    }

    [Fact]
    public void CanConvertRejectsUnhandledExtension()
    {
        var c = Converter();
        Assert.False(c.CanConvert(".mkv"));
        Assert.False(c.CanConvert(".mp4"));
        Assert.False(c.CanConvert(".avi"));
    }

    [Fact]
    public void AllowedLibraryTypesContainsMoviesAndTvShows()
    {
        var types = Converter().AllowedLibraryTypes;
        Assert.Contains(CollectionType.movies, types);
        Assert.Contains(CollectionType.tvshows, types);
    }

    /// <summary>
    /// Live integration test: ffmpeg remux a minimal MKV "as-if-rmvb" to another MKV via stream-copy.
    /// Uses a synthetic MKV input (ffmpeg can produce one) so we don't need a real RMVB file.
    /// Exercises the stream-copy code path. Skipped (early return) when ffmpeg is not on PATH.
    /// </summary>
    [Fact]
    public async Task RemuxesToMkvWhenFfmpegAvailable()
    {
        var ffmpegPath = FindFfmpegOnPath();
        if (ffmpegPath is null)
        {
            // ponytail: skip gracefully — ffmpeg absent from PATH.
            return;
        }

        // Create a tiny 1-second MKV (lavfi source) to act as a remux source.
        var sourceFile = Path.ChangeExtension(Path.GetTempFileName(), ".mkv");
        var outMkv = Path.ChangeExtension(Path.GetTempFileName(), ".mkv");
        try
        {
            CreateTinyMkv(ffmpegPath, sourceFile, seconds: 1);

            var converter = new VideoConverter(ffmpegPath);
            var result = await converter.ConvertAsync(sourceFile, outMkv, null, CancellationToken.None);

            Assert.True(result.Success, "Expected stream-copy to succeed. Reason: " + result.Reason);
            Assert.True(File.Exists(outMkv) && new FileInfo(outMkv).Length > 0, "Output MKV should be non-empty.");
        }
        finally
        {
            try { File.Delete(sourceFile); } catch { }
            try { File.Delete(outMkv); } catch { }
        }
    }

    private static string? FindFfmpegOnPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CreateTinyMkv(string ffmpegPath, string outputPath, int seconds)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "color=c=black:size=32x32:rate=1", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-an", outputPath })
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not create test MKV.");
        }
    }
}
