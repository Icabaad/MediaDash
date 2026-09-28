using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// The CI safety net for Phase 1: adding a matrix row without also committing its fixture will
/// fail this test. Phase 2+ will also add "every unrenderable row has a converter registered"
/// once real converters exist; for now the registry is empty so only fixture coverage is checked.
/// </summary>
public class FormatMatrixCoverageTests : FormatFixtureBase
{
    [Fact]
    public void EveryUnrenderableRowWithTargetHasAFixture()
    {
        var missing = new List<string>();
        foreach (var row in FormatMatrix.All.Where(r => !r.ClientRenders && r.TargetExtension is not null))
        {
            var kindDir = row.Kind.ToString().ToLowerInvariant();
            var path = Path.Combine(FixturesRoot, kindDir, "sample" + row.Extension);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        Assert.True(missing.Count == 0,
            "Missing fixtures for rows in FormatMatrix. Add each file under Tests/fixtures/formats/<kind>/:\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void DrmAdvisoryRowsHaveFixtureToo()
    {
        // AAX / AA are unrenderable with a null TargetExtension (DRM advisory). Still want a
        // fixture so Phase 3's scanner test can assert the DRM advisory message is emitted.
        var drmRows = FormatMatrix.All.Where(r =>
            !r.ClientRenders && r.TargetExtension is null && r.Kind == MediaKind.Audiobook);
        var missing = new List<string>();
        foreach (var row in drmRows)
        {
            var path = Path.Combine(FixturesRoot, "audiobook", "sample" + row.Extension);
            if (!File.Exists(path)) missing.Add(path);
        }

        Assert.True(missing.Count == 0,
            "Missing DRM fixture files:\n" + string.Join("\n", missing));
    }
}
