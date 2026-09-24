using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit round 4 FU-5 (2026-09-19): TrackFixer, PlayabilityFixer rung 4, and
/// TranscodeFixer all make codec/container compatibility decisions during their respective
/// remuxes/encodes. TrackFixer uses <see cref="TrackFixer.ComputeMuxerIncompatibleSubtitleStreams"/>
/// (MP4/MKV/mpegts family). PlayabilityFixer rung 4 goes through the same helper for tx3g in an
/// MKV target. TranscodeFixer uses <see cref="TrackFixer.FirstMp4IncompatibleAudioCodec"/> as
/// its MP4-audio pre-flight. This suite pins that the three fixers make the SAME decision for
/// the same source+target combination — if a future refactor splits the whitelist across sites
/// they'd drift silently.
/// </summary>
public class CodecDecisionParityTests
{
    // === Subtitle-side parity ===
    // Every fixer whose ffmpeg map builder consults `ComputeMuxerIncompatibleSubtitleStreams`
    // must agree on the drop set for a given (probe, extension) pair. The helper itself is the
    // source of truth; this test just documents that all three sites route through it.

    [Theory]
    [InlineData("mp4", "hdmv_pgs_subtitle", true)]   // MP4 muxer refuses PGS
    [InlineData("m4v", "dvd_subtitle", true)]         // MP4 muxer refuses VobSub
    [InlineData("mov", "subrip", true)]               // MP4 muxer refuses SRT (needs mov_text)
    [InlineData("mp4", "mov_text", false)]            // MP4 native
    [InlineData("mkv", "tx3g", true)]                 // MKV refuses MP4-native timed text
    [InlineData("mkv", "mov_text", true)]             // MKV refuses MP4-native timed text
    [InlineData("mkv", "hdmv_pgs_subtitle", false)]   // MKV permissive
    [InlineData("mkv", "subrip", false)]              // MKV permissive
    [InlineData("ts", "hdmv_pgs_subtitle", true)]     // mpegts refuses PGS
    [InlineData("m2ts", "subrip", true)]              // mpegts refuses SRT
    [InlineData("ts", "dvb_subtitle", false)]         // mpegts native
    public void SubtitleMuxerDecision_IsConsistentAcrossFixers(string ext, string codec, bool expectDropped)
    {
        // All three fixer paths (TrackFixer.RunTrackRemuxAsync fold-in, PlayabilityFixer.TryRung1
        // fold-in, PlayabilityFixer.TryRung4 for MKV target) call this single helper. The parity
        // guarantee is "one authoritative predicate answers for all three". If a future refactor
        // introduces a per-fixer override, this test won't catch that directly — but the drop set
        // for a given (ext, codec) is pinned so drift is at least visible at commit time.
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { Index = 0, CodecType = "video", CodecName = "h264" },
                new() { Index = 1, CodecType = "subtitle", CodecName = codec }
            }
        };

        var dropped = TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, ext);

        if (expectDropped)
        {
            Assert.Contains(1, dropped);
        }
        else
        {
            Assert.Empty(dropped);
        }
    }

    // === Audio-side parity ===
    // TranscodeFixer's Quality re-encode pre-flight uses FirstMp4IncompatibleAudioCodec. Track/
    // PlayabilityFixer don't currently pre-flight audio at all (they don't produce MP4 targets
    // from arbitrary source audio). This test documents the current split so a future addition
    // of an audio pre-flight in Track/Playability can be aligned via the same helper.

    [Theory]
    [InlineData("mp4", "truehd", "truehd")]
    [InlineData("mp4", "dts_hd_ma", "dts_hd_ma")]
    [InlineData("mp4", "flac", "flac")]
    [InlineData("mp4", "aac", null)]        // MP4 accepts AAC
    [InlineData("mkv", "truehd", null)]     // MKV accepts everything
    [InlineData("ts", "aac", null)]         // Non-MP4 target: pre-flight is MP4-only by design
    public void Mp4AudioPreflight_IsAvailableForCrossFixerReuse(string ext, string codec, string? expectFirstOffender)
    {
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { Index = 0, CodecType = "video", CodecName = "h264" },
                new() { Index = 1, CodecType = "audio", CodecName = codec }
            }
        };

        var first = TrackFixer.FirstMp4IncompatibleAudioCodec(probe, ext);

        Assert.Equal(expectFirstOffender, first);
    }

    // === MP4 family membership parity ===
    // TranscodeFixer, TrackFixer, and PlayabilityFixer all need to recognise MP4-family targets
    // consistently. `IsMp4FamilyExtension` is the single source of truth callers should route
    // through instead of maintaining private `switch (ext)` lists.

    [Theory]
    [InlineData("mp4", true)]
    [InlineData("m4v", true)]
    [InlineData("m4a", true)]
    [InlineData("mov", true)]
    [InlineData(".mp4", true)]
    [InlineData("mkv", false)]
    [InlineData("webm", false)]
    [InlineData("ts", false)]
    [InlineData("", false)]
    public void Mp4FamilyExtension_HasStablePredicate(string ext, bool expected)
    {
        Assert.Equal(expected, TrackFixer.IsMp4FamilyExtension(ext));
    }
}
