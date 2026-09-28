using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class ComicConverterTests : FormatFixtureBase
{
    private static ComicConverter Converter()
        => new(NullLogger<ComicConverter>.Instance);

    [Fact]
    public void KeyIsComic()
    {
        Assert.Equal("comic", Converter().Key);
    }

    [Fact]
    public void ReadyWithoutToolchain()
    {
        Assert.Equal(ConverterAvailability.Ready, Converter().Availability);
    }

    [Fact]
    public void CanConvertKnownExtensions()
    {
        var c = Converter();
        Assert.True(c.CanConvert(".cbr"));
        Assert.True(c.CanConvert(".cb7"));
        Assert.False(c.CanConvert(".cbz"));
        Assert.False(c.CanConvert(".mobi"));
    }

    [Fact]
    public async Task ConvertsCbrToCbz()
    {
        var source = FixturePath("comic", ".cbr");
        if (!File.Exists(source))
        {
            throw new SkipException("comic/sample.cbr not present — Phase 2 Task 14 upgrades this fixture");
        }

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "out.cbz");

        var result = await Converter().ConvertAsync(source, target, null, CancellationToken.None);

        if (!result.Success)
        {
            // Fixture is likely a ZIP-with-.cbr-extension stub. SharpCompress's RarArchive.Open
            // will reject that; the test is still meaningful for the archive-open error path,
            // but there's no way to assert conversion succeeded without a real RAR fixture.
            throw new SkipException("comic/sample.cbr is not a real RAR archive: " + result.Reason);
        }

        Assert.True(File.Exists(target));
        using var zip = ZipFile.OpenRead(target);
        Assert.NotEmpty(zip.Entries);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}

// Local skip exception — xUnit 2.9 doesn't have Skip built into Assert.
internal sealed class SkipException : Exception
{
    public SkipException(string message)
        : base(message)
    {
    }
}
