using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Coverage for the 01-C boundary parser. Every malformed shape must be refused so a
/// downstream fixer can't be steered by a garbled row. Parsing success is a schema check
/// only — downstream code still has to route paths through LibraryGuard and reject
/// self-referential entries.
/// </summary>
public class IssueDetailsReaderTests
{
    // ---- TryReadPlayability negative cases -------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    [InlineData("{\"reason\":42}")]                 // wrong type on reason
    [InlineData("{\"rename_to_ext\":42}")]          // wrong type on rename_to_ext
    [InlineData("{\"reason\":null}")]               // JSON-null (distinct from absent) is refused
    [InlineData("{\"rename_to_ext\":null}")]        // same
    [InlineData("{\"reason\":true}")]               // wrong type: bool
    [InlineData("{\"reason\":[\"drm-protected\"]}")] // wrong type: array
    public void TryReadPlayability_RejectsMalformedInput(string? json)
    {
        Assert.False(IssueDetailsReader.TryReadPlayability(json, out var details));
        Assert.Null(details);
    }

    [Theory]
    [InlineData("{\"reason\":\"drm-protected\"}", "drm-protected", null)]
    [InlineData("{\"reason\":\"container-extension-mismatch\",\"rename_to_ext\":\"mkv\"}", "container-extension-mismatch", "mkv")]
    [InlineData("{\"reason\":\"no-audio\",\"detail\":\"the file has no audio stream\"}", "no-audio", null)]
    [InlineData("{}", null, null)]
    public void TryReadPlayability_AcceptsWellFormedInput(string json, string? expectedReason, string? expectedRenameToExt)
    {
        var ok = IssueDetailsReader.TryReadPlayability(json, out var details);
        Assert.True(ok);
        Assert.NotNull(details);
        Assert.Equal(expectedReason, details!.Reason);
        Assert.Equal(expectedRenameToExt, details.RenameToExtension);
    }

    // ---- TryReadSubtitleCleanup negative cases ----------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]                                // externalFiles missing
    [InlineData("{\"externalFiles\":42}")]            // externalFiles wrong type
    [InlineData("{\"externalFiles\":\"a string\"}")]  // wrong-typed array
    [InlineData("{\"externalFiles\":[\"/a\", 3, \"/b\"]}")] // one non-string entry rejects the whole array
    [InlineData("{\"externalFiles\":[null]}")]        // JSON null in the array is not a string
    public void TryReadSubtitleCleanup_RejectsMalformedInput(string? json)
    {
        Assert.False(IssueDetailsReader.TryReadSubtitleCleanup(json, out var details));
        Assert.Null(details);
    }

    [Fact]
    public void TryReadSubtitleCleanup_AcceptsEmptyArray()
    {
        var ok = IssueDetailsReader.TryReadSubtitleCleanup("{\"externalFiles\":[]}", out var details);
        Assert.True(ok);
        Assert.NotNull(details);
        Assert.Empty(details!.ExternalFiles);
    }

    [Fact]
    public void TryReadSubtitleCleanup_AcceptsWellFormedArray()
    {
        var ok = IssueDetailsReader.TryReadSubtitleCleanup(
            "{\"externalFiles\":[\"/mnt/media/en.srt\",\"/mnt/media/fr.srt\"]}",
            out var details);
        Assert.True(ok);
        Assert.NotNull(details);
        Assert.Equal(
            new[] { "/mnt/media/en.srt", "/mnt/media/fr.srt" },
            details!.ExternalFiles.ToArray());
    }

    [Fact]
    public void TryReadSubtitleCleanup_TolerantOfUnknownRootFields()
    {
        // Additive fields the scanner adds later (warnings, languages, etc.) must not break
        // the reader — the persisted payload survives forward-compatible extension.
        var ok = IssueDetailsReader.TryReadSubtitleCleanup(
            "{\"externalFiles\":[\"/mnt/media/en.srt\"],\"languages\":[\"en\"],\"warnings\":[]}",
            out var details);
        Assert.True(ok);
        Assert.NotNull(details);
        Assert.Single(details!.ExternalFiles);
    }

    [Fact]
    public void TryReadSubtitleCleanup_DropsBlankPathEntries()
    {
        // Empty-string entries in the array would resolve to "" downstream and blow through
        // the guard; drop them at the boundary so the fixer never sees an empty string as a
        // "path".
        var ok = IssueDetailsReader.TryReadSubtitleCleanup(
            "{\"externalFiles\":[\"\",\"/mnt/media/fr.srt\",\"\"]}",
            out var details);
        Assert.True(ok);
        Assert.NotNull(details);
        Assert.Single(details!.ExternalFiles);
        Assert.Equal("/mnt/media/fr.srt", details.ExternalFiles[0]);
    }

    // ---- Downstream safety spot-check -------------------------------------------------------

    [Fact]
    public void SelfReferentialSubtitle_StillRefusedAfterBoundaryParse()
    {
        // Cross-check: parsing accepts the payload, but the sidecar-path guard the fixer
        // enforces on every external file still refuses a self-referential path (an
        // "external subtitle" that IS the video file). Boundary parsing is a schema check;
        // authorisation lives elsewhere.
        var ok = IssueDetailsReader.TryReadSubtitleCleanup(
            "{\"externalFiles\":[\"/mnt/media/Movie.mkv\"]}",
            out var details);
        Assert.True(ok);
        Assert.NotNull(details);

        // TrackFixer.IsSelfReferentialSubtitle is the guard that runs on every entry before
        // recycle/delete — matches the video path being fixed.
        Assert.True(
            Jellyfin.Plugin.MediaDash.Fixers.TrackFixer.IsSelfReferentialSubtitle(
                details!.ExternalFiles[0],
                "/mnt/media/Movie.mkv"));
    }
}
