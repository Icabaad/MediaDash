using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class BookProbeServiceTests
{
    [Fact]
    public void Epub_Valid_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write("application/epub+zip");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Epub_MissingMimetype_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("content.opf");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Epub_TruncatedZip_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"trunc-{Guid.NewGuid():N}.epub");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_Valid_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.pdf");
        try
        {
            var content = "%PDF-1.4\n%âãÏÓ\n1 0 obj<</Pages 2 0 R/Type/Catalog>>endobj\n2 0 obj<</Kids[]/Count 0/Type/Pages>>endobj\nxref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000063 00000 n \ntrailer<</Root 1 0 R/Size 3>>\nstartxref\n107\n%%EOF\n";
            File.WriteAllText(tmp, content);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_MissingEofMarker_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(tmp, "%PDF-1.4\nnot a real pdf");
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_MissingHeader_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(tmp, "hello");
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Mobi_BadMagic_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.mobi");
        try
        {
            File.WriteAllBytes(tmp, new byte[100]);
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Mobi_ValidMagic_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.mobi");
        try
        {
            var bytes = new byte[128];
            Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(bytes, 60);
            File.WriteAllBytes(tmp, bytes);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ebook-comic-quirks-audit Q7: pre-KF8 .azw files carry TEXtREAd magic instead of BOOKMOBI.
    [Fact]
    public void Mobi_PreKF8TEXtREAdMagic_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.azw");
        try
        {
            var bytes = new byte[128];
            Encoding.ASCII.GetBytes("TEXtREAd").CopyTo(bytes, 60);
            File.WriteAllBytes(tmp, bytes);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ebook-comic-quirks-audit Q5: encrypted PDFs carry /Encrypt in the trailer dict.
    [Fact]
    public void Pdf_WithEncryptToken_FlaggedAsDrm()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"enc-{Guid.NewGuid():N}.pdf");
        try
        {
            var content = "%PDF-1.4\n%âãÏÓ\n1 0 obj<</Pages 2 0 R/Type/Catalog>>endobj\nxref\n0 2\ntrailer<</Root 1 0 R/Encrypt 3 0 R/Size 2>>\nstartxref\n70\n%%EOF\n";
            File.WriteAllText(tmp, content);
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
            Assert.StartsWith("drm:", result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ebook-comic-quirks-audit Q4: Adobe DRM in EPUB — encryption.xml with a CipherReference
    // targeting a non-font resource. Must be distinguished from Calibre's font-obfuscation
    // (encryption.xml where every URI is a font).
    [Fact]
    public void Epub_AdobeDrmEncryptionXml_FlaggedAsDrm()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"drm-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var m = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var w = new StreamWriter(m.Open(), new UTF8Encoding(false)))
                {
                    w.Write("application/epub+zip");
                }

                var enc = zip.CreateEntry("META-INF/encryption.xml", CompressionLevel.Optimal);
                using var e = new StreamWriter(enc.Open(), new UTF8Encoding(false));
                e.Write("<?xml version=\"1.0\"?><encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><EncryptedData xmlns=\"http://www.w3.org/2001/04/xmlenc#\"><CipherData><CipherReference URI=\"OEBPS/chapter01.xhtml\"/></CipherData></EncryptedData></encryption>");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
            Assert.StartsWith("drm:", result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Epub_CalibreFontObfuscation_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"calibre-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var m = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var w = new StreamWriter(m.Open(), new UTF8Encoding(false)))
                {
                    w.Write("application/epub+zip");
                }

                var enc = zip.CreateEntry("META-INF/encryption.xml", CompressionLevel.Optimal);
                using var e = new StreamWriter(enc.Open(), new UTF8Encoding(false));
                e.Write("<?xml version=\"1.0\"?><encryption><EncryptedData><CipherData><CipherReference URI=\"OEBPS/fonts/CustomFont.otf\"/></CipherData></EncryptedData><EncryptedData><CipherData><CipherReference URI=\"OEBPS/fonts/AnotherFont.ttf\"/></CipherData></EncryptedData></encryption>");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ebook-comic-quirks-audit Q37: .djvu was routed to "unsupported extension" — now it has a probe.
    [Fact]
    public void Djvu_ValidMagic_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.djvu");
        try
        {
            // AT&TFORM + 4-byte size (any) + DJVU
            var bytes = new byte[32];
            Encoding.ASCII.GetBytes("AT&TFORM").CopyTo(bytes, 0);
            bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0x14;
            Encoding.ASCII.GetBytes("DJVU").CopyTo(bytes, 12);
            File.WriteAllBytes(tmp, bytes);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Djvu_MultiPageDJVM_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"multi-{Guid.NewGuid():N}.djvu");
        try
        {
            var bytes = new byte[32];
            Encoding.ASCII.GetBytes("AT&TFORM").CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes("DJVM").CopyTo(bytes, 12);
            File.WriteAllBytes(tmp, bytes);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Djvu_BadMagic_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.djvu");
        try
        {
            File.WriteAllBytes(tmp, new byte[32]);
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
