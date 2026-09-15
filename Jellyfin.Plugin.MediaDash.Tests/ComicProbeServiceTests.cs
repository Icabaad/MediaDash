using System;
using System.IO;
using System.IO.Compression;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ComicProbeServiceTests
{
    [Fact]
    public void Cbz_Valid_WithOneImage_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("001.jpg");
            }

            var result = ComicProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbz_NoImages_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("readme.txt");
            }

            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbz_NotAZip_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbz");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00, 0x01, 0x02 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbr_NotARar_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbr");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cb7_NotA7z_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cb7");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ebook-comic-quirks-audit Q22: a .cbz that's actually RAR (common in scanlation dumps)
    // now routes through the extension-mismatch rename flow instead of "corrupt".
    [Fact]
    public void Cbz_ButActuallyRar_FlaggedAsMismatch()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lie-{Guid.NewGuid():N}.cbz");
        try
        {
            // RAR magic: "Rar!\x1A\x07\x00"
            File.WriteAllBytes(tmp, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
            Assert.StartsWith("mismatch:cbr:", result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbr_ButActually7z_FlaggedAsMismatch()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lie-{Guid.NewGuid():N}.cbr");
        try
        {
            // 7z magic: "7z\xBC\xAF\x27\x1C"
            File.WriteAllBytes(tmp, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
            Assert.StartsWith("mismatch:cb7:", result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cb7_ButActuallyZip_FlaggedAsMismatch()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lie-{Guid.NewGuid():N}.cb7");
        try
        {
            // ZIP magic: "PK\x03\x04"
            File.WriteAllBytes(tmp, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
            Assert.StartsWith("mismatch:cbz:", result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
