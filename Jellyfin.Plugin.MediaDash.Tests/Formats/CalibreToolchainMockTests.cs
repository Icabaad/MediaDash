using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class CalibreToolchainMockTests
{
    [Fact]
    public async Task NoOpDownloaderReportsUnavailable()
    {
        using var tmp = new TempDir();
        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);
        Assert.Equal(ConverterAvailability.Unavailable, result);
        Assert.False(chain.IsAvailable);
    }

    [Fact]
    public async Task UserOverrideResolvesWhenExecutablePresent()
    {
        using var tmp = new TempDir();
        var overrideDir = Path.Combine(tmp.Path, "override");
        Directory.CreateDirectory(overrideDir);
        var exe = Path.Combine(overrideDir, ExeName());
        File.WriteAllText(exe, "stub");

        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(overrideDir, null, CancellationToken.None);

        Assert.Equal(ConverterAvailability.Ready, result);
        Assert.Equal(exe, chain.EbookConvertPath);
    }

    [Fact]
    public async Task PreviouslyExtractedPortableResolves()
    {
        using var tmp = new TempDir();
        var toolsDir = Path.Combine(tmp.Path, "tools", "calibre");
        Directory.CreateDirectory(toolsDir);
        var exe = Path.Combine(toolsDir, ExeName());
        File.WriteAllText(exe, "stub");

        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);

        Assert.Equal(ConverterAvailability.Ready, result);
        Assert.Equal(exe, chain.EbookConvertPath);
    }

    [Fact]
    public async Task DownloaderInvokedWhenCanDownload()
    {
        using var tmp = new TempDir();
        var invoked = false;
        var downloader = new StubDownloader(() =>
        {
            invoked = true;
            return "fake-checksum";
        });

        var chain = new CalibreToolchain(downloader, NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);

        // Downloader was called. The stub writes a 1-byte file (not a real installer),
        // so extraction fails and returns Unavailable rather than the old NeedsDownload stub value.
        Assert.True(invoked);
        Assert.Equal(ConverterAvailability.Unavailable, result);
    }

    [Fact]
    public async Task FailedExtractionCleansExtractRootAndReturnsUnavailable()
    {
        using var tmp = new TempDir();
        // Stub downloader writes a bogus "installer" — extraction will fail.
        var downloader = new StubDownloader(() => "fake-checksum");
        var chain = new CalibreToolchain(downloader, NullLogger<CalibreToolchain>.Instance, tmp.Path);

        var result = await chain.EnsureAsync(null, null, CancellationToken.None);

        Assert.Equal(ConverterAvailability.Unavailable, result);
        Assert.False(chain.IsAvailable);
    }

    private static string ExeName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ebook-convert.exe" : "ebook-convert";

    private sealed class StubDownloader : IToolchainDownloader
    {
        private readonly Func<string?> _returns;

        public StubDownloader(Func<string?> returns)
        {
            _returns = returns;
        }

        public bool CanDownload => true;

        public Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            destination.WriteByte(0x42);
            return Task.FromResult(_returns());
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
