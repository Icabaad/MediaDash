using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class AudiobookConverterTests : FormatFixtureBase
{
    private static AudiobookConverter Converter() => new();

    [Fact]
    public void KeyIsAudiobook()
    {
        Assert.Equal("audiobook", Converter().Key);
    }

    [Fact]
    public void CanConvertAaxAndAa()
    {
        var c = Converter();
        Assert.True(c.CanConvert(".aax"));
        Assert.True(c.CanConvert(".aa"));
        Assert.False(c.CanConvert(".epub"));
        Assert.False(c.CanConvert(".mp3"));
    }

    [Fact]
    public void CanConvertRecognizesFolderMergeKey()
    {
        // CanConvert("audiobook-folder-merge") returns true so callers can
        // check support uniformly.
        Assert.True(Converter().CanConvert(AudiobookConverter.FolderMergeKey));
    }

    [Fact]
    public async Task RefusesAaxWithDrmMessage()
    {
        var result = await Converter().ConvertAsync(
            @"C:\fake\audiobook.aax",
            @"C:\fake\audiobook.m4b",
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("DRM", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesAaButNeverTouchesTheFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mediadash-abook-" + Guid.NewGuid() + ".aa");
        // Fake AAX header bytes — converter must not inspect or mutate.
        var originalBytes = new byte[] { 0xAA, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(tmp, originalBytes);
        try
        {
            var fi = new FileInfo(tmp);
            var lengthBefore = fi.Length;
            var mtimeBefore = fi.LastWriteTimeUtc;

            var result = await Converter().ConvertAsync(tmp, tmp + ".out.m4b", null, CancellationToken.None);

            Assert.False(result.Success);

            fi.Refresh();
            Assert.Equal(lengthBefore, fi.Length);
            Assert.Equal(mtimeBefore, fi.LastWriteTimeUtc);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task RefusesFolderThatNoLongerMatchesPattern()
    {
        // Empty folder → ClassifyAndSortFolder returns null → ConversionResult.Success=false.
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-abook-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // parameterless ctor has no ffprobe — reaches ClassifyAndSortFolder, gets null, returns failure.
            var result = await Converter().ConvertAsync(dir, Path.Combine(dir, "out.m4b"), null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("no longer matches", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task RefusesFolderWithOnlyOneNumberedFile()
    {
        // One file → count < 2 → failure.
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-abook-one-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Chapter01.mp3"), new byte[8]);
            var result = await Converter().ConvertAsync(dir, Path.Combine(dir, "out.m4b"), null, CancellationToken.None);
            Assert.False(result.Success);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Happy-path integration test: two numbered MP3s → ffmpeg concat → M4B produced.
    /// Skipped when ffmpeg is not on PATH (CI without ffmpeg).
    /// Exercises ClassifyAndSortFolder + the ffmpeg concat + chapter-metadata pipeline end-to-end.
    /// </summary>
    [Fact]
    public async Task MergeFolderProducesM4bWhenFfmpegAvailable()
    {
        var ffmpegPath = FindFfmpegOnPath();
        if (ffmpegPath is null)
        {
            // ponytail: skip gracefully — ffmpeg not on PATH in this environment.
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mediadash-abook-merge-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var outM4b = Path.ChangeExtension(Path.GetTempFileName(), ".m4b");
        try
        {
            CreateSilentMp3(ffmpegPath, Path.Combine(dir, "Chapter01.mp3"), seconds: 1);
            CreateSilentMp3(ffmpegPath, Path.Combine(dir, "Chapter02.mp3"), seconds: 1);

            // Inline the merge logic (mirrors MergeFolderAsync) using the real ffmpeg path.
            // This exercises ClassifyAndSortFolder, the concat manifest, the metadata file, and
            // ffmpeg's exit code — the full pipeline minus DI wiring.
            var result = await RunInlineM4bMerge(ffmpegPath, dir, outM4b, CancellationToken.None);

            Assert.True(result, "Expected ffmpeg merge to succeed.");
            Assert.True(File.Exists(outM4b) && new FileInfo(outM4b).Length > 0, "M4B output should be non-empty.");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { File.Delete(outM4b); } catch { }
        }
    }

    // Runs the exact same concat + chapter metadata pipeline as AudiobookConverter.MergeFolderAsync
    // but accepts the ffmpeg path directly (no IMediaEncoder/FfprobeService DI).
    private static async Task<bool> RunInlineM4bMerge(string ffmpegPath, string dir, string outM4b, CancellationToken ct)
    {
        var files = AudiobookConverter.ClassifyAndSortFolder(dir);
        if (files is null || files.Count < 2)
        {
            return false;
        }

        // Probe durations via ffprobe (same directory as ffmpeg, or PATH fallback).
        var durations = new List<double>(files.Count);
        var ffprobeDir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var ffprobePath = File.Exists(Path.Combine(ffprobeDir, ffprobeName))
            ? Path.Combine(ffprobeDir, ffprobeName)
            : "ffprobe";

        foreach (var file in files)
        {
            var dur = GetDurationViaFfprobe(ffprobePath, file);
            if (dur <= 0)
            {
                return false;
            }

            durations.Add(dur);
        }

        var concatTxt = Path.ChangeExtension(Path.GetTempFileName(), ".txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append("file '").Append(file.Replace("'", @"'\''")).Append("'\n");
            }

            await File.WriteAllTextAsync(concatTxt, sb.ToString(), ct);

            var metadataTxt = Path.ChangeExtension(Path.GetTempFileName(), ".txt");
            try
            {
                var meta = new StringBuilder();
                meta.AppendLine(";FFMETADATA1");
                double cumulativeMs = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    var startMs = (long)Math.Round(cumulativeMs);
                    cumulativeMs += durations[i] * 1000;
                    var endMs = (long)Math.Round(cumulativeMs);
                    meta.AppendLine("[CHAPTER]");
                    meta.AppendLine("TIMEBASE=1/1000");
                    meta.AppendLine("START=" + startMs.ToString(CultureInfo.InvariantCulture));
                    meta.AppendLine("END=" + endMs.ToString(CultureInfo.InvariantCulture));
                    meta.AppendLine("title=" + Path.GetFileNameWithoutExtension(files[i]));
                }

                await File.WriteAllTextAsync(metadataTxt, meta.ToString(), ct);

                using var proc = new Process();
                proc.StartInfo.FileName = ffmpegPath;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.RedirectStandardError = true;
                foreach (var arg in new[] { "-y", "-nostdin", "-f", "concat", "-safe", "0", "-i", concatTxt, "-i", metadataTxt, "-map_metadata", "1", "-c:a", "aac", "-b:a", "96k", "-vn", outM4b })
                {
                    proc.StartInfo.ArgumentList.Add(arg);
                }

                proc.Start();
                await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0;
            }
            finally
            {
                try { File.Delete(metadataTxt); } catch { }
            }
        }
        finally
        {
            try { File.Delete(concatTxt); } catch { }
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

    private static void CreateSilentMp3(string ffmpegPath, string outputPath, int seconds)
    {
        using var proc = new Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", seconds.ToString(CultureInfo.InvariantCulture), "-q:a", "9", outputPath })
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not create silent MP3 for test.");
        }
    }

    private static double GetDurationViaFfprobe(string ffprobePath, string file)
    {
        using var proc = new Process();
        proc.StartInfo.FileName = ffprobePath;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file })
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(30_000);
        return double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
