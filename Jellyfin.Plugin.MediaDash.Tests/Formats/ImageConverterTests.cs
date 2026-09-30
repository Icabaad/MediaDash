using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class ImageConverterTests : FormatFixtureBase
{
    // Parameterless ctor — Key / CanConvert / AllowedLibraryTypes don't touch ffmpeg.
    private static ImageConverter Converter() => new();

    [Fact]
    public void KeyIsImage()
    {
        Assert.Equal("image", Converter().Key);
    }

    [Fact]
    public void CanConvertHandledExtensions()
    {
        var c = Converter();
        Assert.True(c.CanConvert(".heic"));
        Assert.True(c.CanConvert(".heif"));
        Assert.True(c.CanConvert(".tif"));
        Assert.True(c.CanConvert(".tiff"));
        Assert.True(c.CanConvert(".bmp"));
        Assert.True(c.CanConvert(".raw"));
        Assert.True(c.CanConvert(".nef"));
        Assert.True(c.CanConvert(".cr2"));
        Assert.True(c.CanConvert(".arw"));
        Assert.True(c.CanConvert(".dng"));
    }

    [Fact]
    public void CanConvertRejectsUnhandledExtension()
    {
        var c = Converter();
        Assert.False(c.CanConvert(".jpg"));
        Assert.False(c.CanConvert(".png"));
        Assert.False(c.CanConvert(".mp4"));
    }

    [Fact]
    public void AllowedLibraryTypesContainsHomevideos()
    {
        Assert.Contains(CollectionType.homevideos, Converter().AllowedLibraryTypes);
    }

    /// <summary>
    /// Live integration test: create a 1x1 BMP via ffmpeg, convert it to JPG.
    /// Skipped (early return) when ffmpeg is not on PATH — mirrors MergeFolderProducesM4bWhenFfmpegAvailable.
    /// </summary>
    [Fact]
    public async Task ConvertsBmpToJpgWhenFfmpegAvailable()
    {
        var ffmpegPath = FindFfmpegOnPath();
        if (ffmpegPath is null)
        {
            // ponytail: skip gracefully — ffmpeg absent from PATH.
            return;
        }

        var sourceBmp = Path.ChangeExtension(Path.GetTempFileName(), ".bmp");
        var outJpg = Path.ChangeExtension(Path.GetTempFileName(), ".jpg");
        try
        {
            CreateSinglePixelBmp(ffmpegPath, sourceBmp);

            var converter = new ImageConverter(ffmpegPath);
            var result = await converter.ConvertAsync(sourceBmp, outJpg, null, CancellationToken.None);

            Assert.True(result.Success, "Expected conversion to succeed. Reason: " + result.Reason);
            Assert.True(File.Exists(outJpg) && new FileInfo(outJpg).Length > 0, "Output JPG should be non-empty.");
        }
        finally
        {
            try { File.Delete(sourceBmp); } catch { }
            try { File.Delete(outJpg); } catch { }
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

    private static void CreateSinglePixelBmp(string ffmpegPath, string outputPath)
    {
        using var proc = new Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "color=c=blue:size=1x1:rate=1", "-vframes", "1", outputPath })
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not create test BMP.");
        }
    }
}
