using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Root-cause audit 2026-09-19 R01: "Audio and subtitle cleanup are Off and explicitly disabled;
/// BuildArgs still maps only English tracks from a five-stream source." Existing
/// <see cref="TranscodeFixerAuthorizationTests"/> already pins the 3-stream case; this reproduces
/// the audit's exact 5-stream fixture (1 video + 3 audio + 1 subtitle) so we can trust or reject
/// the audit's fresh-DLL claim on its own terms.
/// </summary>
public class R01AuditReproTests
{
    [Fact]
    public void BuildArgs_FiveStreamSource_UnauthorizedAudioAndSubtitleCleanup_KeepsEveryTrack()
    {
        var cfg = new PluginConfiguration
        {
            TargetContainer = "mkv",
            PreferredCodec = "hevc",
            MaxResolutionHeight = 1080,
            MaxBitrateMbpsAt1080p = 8,
            AllowedAudioLanguages = new[] { "eng" },
            AllowedSubtitleLanguages = new[] { "eng" },
            AudioFixMode = FixMode.Off,
            SubtitleFixMode = FixMode.Off,
            FixerDisabled = new[] { "AudioLanguage", "SubtitleLanguage" }
        };

        var probe = new FfprobeData
        {
            Format = new FfprobeFormat { FormatName = "matroska,webm" },
            Streams = new List<FfprobeStreamInfo>
            {
                new() { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080 },
                new() { Index = 1, CodecType = "audio", CodecName = "aac", Tags = new Dictionary<string, string> { ["language"] = "eng" } },
                new() { Index = 2, CodecType = "audio", CodecName = "aac", Tags = new Dictionary<string, string> { ["language"] = "fre" } },
                new() { Index = 3, CodecType = "audio", CodecName = "aac", Tags = new Dictionary<string, string> { ["language"] = "ger" } },
                new() { Index = 4, CodecType = "subtitle", CodecName = "subrip", Tags = new Dictionary<string, string> { ["language"] = "fre" } }
            }
        };

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null);

        var maps = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                maps.Add(args[i + 1]);
            }
        }

        // Every audio + subtitle stream must survive the unauthorized-cleanup gate. If R01 reproduces,
        // this test fails with maps missing "0:2", "0:3", "0:4".
        Assert.Contains("0:0", maps); // video
        Assert.Contains("0:1", maps); // eng audio
        Assert.Contains("0:2", maps); // fre audio — DROPS if the gate fails
        Assert.Contains("0:3", maps); // ger audio — DROPS if the gate fails
        Assert.Contains("0:4", maps); // fre subtitle — DROPS if the gate fails
    }
}
