using System.IO;
using System.Text;
using Jellyfin.Plugin.MediaDash.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Contract tests for the 06-D.1 asset controller. Runtime browser verification lives in
/// the phases/07-ui.ps1 matrix and requires a live localhost:8099 Jellyfin — these tests
/// exercise the routing/whitelist/MIME/embed contract in isolation so failures surface at
/// build time instead of during a live QA sweep.
/// </summary>
public sealed class PluginAssetControllerTests
{
    [Fact]
    public void WhitelistedAssetReturnsFileStreamWithDeclaredMime()
    {
        var controller = new PluginAssetController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.GetAsset("page-lifetime.js");

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/javascript; charset=utf-8", file.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        var body = reader.ReadToEnd();
        Assert.Contains("createPageLifetime", body);
        Assert.Contains("ns.MediaDash.createPageLifetime", body);
    }

    [Fact]
    public void UnknownAssetNameReturnsNotFound()
    {
        var controller = new PluginAssetController();

        var result = controller.GetAsset("nope.js");

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("../scripts/page-lifetime.js")]
    [InlineData("..\\page-lifetime.js")]
    [InlineData("/etc/passwd")]
    [InlineData("page-lifetime")] // extension stripped
    [InlineData("Page-Lifetime.js")] // case-sensitive whitelist — no accidental Windows-style fold-in
    public void PathTraversalOrMalformedNameReturnsNotFound(string name)
    {
        var controller = new PluginAssetController();

        var result = controller.GetAsset(name);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void EmptyOrWhitespaceNameReturnsNotFound()
    {
        var controller = new PluginAssetController();

        Assert.IsType<NotFoundResult>(controller.GetAsset(string.Empty));
        Assert.IsType<NotFoundResult>(controller.GetAsset(" "));
    }
}
