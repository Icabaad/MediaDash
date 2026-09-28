using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class CalibreDownloaderTests
{
    [Fact]
    public void CanDownloadReflectsPlatform()
    {
        var http = new StubHttpClientFactory();
        var d = new CalibreDownloader(http, NullLogger<CalibreDownloader>.Instance);
        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), d.CanDownload);
    }

    [Fact]
    public async Task DeclinesWhenNotOnWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var http = new StubHttpClientFactory();
        var d = new CalibreDownloader(http, NullLogger<CalibreDownloader>.Instance);
        using var dest = new MemoryStream();
        var result = await d.DownloadAsync(dest, null, CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, dest.Length);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }
}
