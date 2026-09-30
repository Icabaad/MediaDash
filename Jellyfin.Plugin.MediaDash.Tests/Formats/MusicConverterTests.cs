using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class MusicConverterTests : FormatFixtureBase
{
    private static MusicConverter Converter() => new();

    [Fact]
    public void KeyIsMusic()
    {
        Assert.Equal("music", Converter().Key);
    }

    [Fact]
    public void CanConvertHandledExtensions()
    {
        var c = Converter();
        // Trackers
        Assert.True(c.CanConvert(".669"));
        Assert.True(c.CanConvert(".it"));
        Assert.True(c.CanConvert(".mod"));
        Assert.True(c.CanConvert(".s3m"));
        Assert.True(c.CanConvert(".xm"));
        // Legacy lossless
        Assert.True(c.CanConvert(".ape"));
        Assert.True(c.CanConvert(".wv"));
        Assert.True(c.CanConvert(".tak"));
        Assert.True(c.CanConvert(".tta"));
        // WMA
        Assert.True(c.CanConvert(".wma"));
    }

    [Fact]
    public void CanConvertRejectsUnhandledExtension()
    {
        var c = Converter();
        Assert.False(c.CanConvert(".mp3"));
        Assert.False(c.CanConvert(".flac"));
        Assert.False(c.CanConvert(".m4a"));
    }

    [Fact]
    public void AllowedLibraryTypesContainsMusic()
    {
        Assert.Contains(CollectionType.music, Converter().AllowedLibraryTypes);
    }

    /// <summary>
    /// Live integration test: create a 1-second silent WAV, convert it to FLAC.
    /// Skipped (early return) when ffmpeg is not on PATH.
    /// </summary>
    [Fact]
    public async Task ConvertsWavToFlacWhenFfmpegAvailable()
    {
        var ffmpegPath = FindFfmpegOnPath();
        if (ffmpegPath is null)
        {
            // ponytail: skip gracefully — ffmpeg absent from PATH.
            return;
        }

        var sourceWav = Path.ChangeExtension(Path.GetTempFileName(), ".wav");
        var outFlac = Path.ChangeExtension(Path.GetTempFileName(), ".flac");
        try
        {
            CreateSilentWav(ffmpegPath, sourceWav, seconds: 1);

            // WAV isn't in MusicConverter.Handled, but the FLAC args path applies to any non-WMA.
            // We rename it to .ape in the call to exercise the default (FLAC) branch.
            var sourceApe = Path.ChangeExtension(sourceWav, ".ape");
            File.Move(sourceWav, sourceApe, overwrite: true);

            var converter = new MusicConverter(ffmpegPath);
            var result = await converter.ConvertAsync(sourceApe, outFlac, null, CancellationToken.None);

            Assert.True(result.Success, "Expected FLAC conversion to succeed. Reason: " + result.Reason);
            Assert.True(File.Exists(outFlac) && new FileInfo(outFlac).Length > 0, "Output FLAC should be non-empty.");
        }
        finally
        {
            try { File.Delete(sourceWav); } catch { }
            try { File.Delete(Path.ChangeExtension(sourceWav, ".ape")); } catch { }
            try { File.Delete(outFlac); } catch { }
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

    private static void CreateSilentWav(string ffmpegPath, string outputPath, int seconds)
    {
        using var proc = new Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", seconds.ToString(CultureInfo.InvariantCulture), outputPath })
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not create silent WAV for test.");
        }
    }
}
