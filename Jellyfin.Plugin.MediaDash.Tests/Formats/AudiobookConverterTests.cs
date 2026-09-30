using System;
using System.IO;
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
}
