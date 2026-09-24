using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit round 2 (2026-09-19): TranscodeFixer's Quality re-encode does `-c:a copy` and
/// silently failed at the mux boundary when the user's target container was MP4 and the source
/// had TrueHD / DTS-HD MA / lossless PCM audio (standard on 4K Blu-ray remuxes). Pre-flight the
/// audio codecs against the same MP4 muxer whitelist the subtitle pass uses and refuse cleanly
/// with an actionable message before ffmpeg is called.
/// </summary>
public class TrackFixerMp4MuxerAudioTests
{
    [Theory]
    // Codecs the mp4/mov/ipod muxer refuses under -c copy — every one is a definite silent-fail
    // before this pre-flight landed. Ranked by real-world prevalence on user rips.
    [InlineData("truehd")]         // Blu-ray lossless — most common offender on 4K remuxes
    [InlineData("dts")]            // most legacy Blu-ray audio (partially compatible in newer mp4, but flaky)
    [InlineData("dts_hd_ma")]      // lossless DTS master audio
    [InlineData("dts_hd")]         // hi-res DTS
    [InlineData("flac")]           // lossless — ffmpeg's flac-in-mp4 support keeps changing
    [InlineData("pcm_bluray")]     // uncompressed Blu-ray PCM
    [InlineData("vorbis")]         // rare in .mkv → wanted-mp4 conversion
    [InlineData("speex")]          // webm-only
    [InlineData("wmav2")]          // Windows Media audio
    [InlineData("")]               // empty codec_name — better to refuse than let mp4 fail cryptically
    public void MuxerHostileAudioIsFlaggedForMp4(string codec)
    {
        var probe = Probe(Vid(0, "hevc"), Aud(1, codec));

        var result = TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "mp4");

        Assert.NotNull(result);
    }

    [Theory]
    // MP4-family target with a compatible codec must pass — otherwise Quality re-encodes of the
    // common AAC-in-MP4 web-download case would refuse silently.
    [InlineData("aac")]      // universal — 90% of streaming rips
    [InlineData("mp3")]      // legacy but widespread
    [InlineData("ac3")]      // Dolby Digital
    [InlineData("eac3")]     // Dolby Digital Plus
    [InlineData("alac")]     // Apple lossless (native in mp4)
    [InlineData("opus")]     // Jellyfin 10.9+, ffmpeg 4.4+
    public void Mp4CompatibleAudioPassesPreflight(string codec)
    {
        var probe = Probe(Vid(0, "hevc"), Aud(1, codec));

        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "mp4"));
    }

    [Fact]
    public void NonMp4TargetSkipsPreflightEntirely()
    {
        // MKV / TS / etc. handle every audio codec (that's why MKV is the safe default target).
        // Pre-flight must not fire for those or every Quality re-encode targeting MKV would fail.
        var probe = Probe(Vid(0, "hevc"), Aud(1, "truehd"));

        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "mkv"));
        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "ts"));
        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "webm"));
    }

    [Theory]
    // Every MP4-family extension routes through the same mp4/ipod muxer family and refuses the
    // same audio codecs — check each so a future extension add doesn't silently escape the guard.
    [InlineData("mp4")]
    [InlineData("m4v")]
    [InlineData("m4a")]
    [InlineData("mov")]
    // Leading dot must be tolerated (Path.GetExtension returns ".mp4" but config may store "mp4").
    [InlineData(".mp4")]
    public void EveryMp4FamilyExtensionFlagsIncompatibleAudio(string ext)
    {
        var probe = Probe(Vid(0, "hevc"), Aud(1, "truehd"));

        Assert.Equal("truehd", TrackFixer.FirstMp4IncompatibleAudioCodec(probe, ext));
    }

    [Fact]
    public void FirstIncompatibleWinsWhenMultipleTracksAreOffenders()
    {
        // Real 4K remux: TrueHD primary + DTS-HD secondary + AAC commentary. Refusal message
        // names the first offender the user needs to think about; the others are implied.
        var probe = Probe(
            Vid(0, "hevc"),
            Aud(1, "truehd"),
            Aud(2, "dts_hd_ma"),
            Aud(3, "aac"));

        Assert.Equal("truehd", TrackFixer.FirstMp4IncompatibleAudioCodec(probe, "mp4"));
    }

    [Fact]
    public void EmptyProbeReturnsNull()
    {
        // Defence in depth: no audio streams present at all → nothing to flag. TranscodeFixer's
        // earlier "no video" guard would have already refused the fix.
        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(new FfprobeData(), "mp4"));
        Assert.Null(TrackFixer.FirstMp4IncompatibleAudioCodec(Probe(), "mp4"));
    }

    [Fact]
    public void IsMp4FamilyExtensionMatchesTheAudioCheckSurface()
    {
        // Sibling call surface — TranscodeFixer uses this to gate whether to run the audio
        // pre-flight at all. Keeping them symmetric prevents drift.
        Assert.True(TrackFixer.IsMp4FamilyExtension("mp4"));
        Assert.True(TrackFixer.IsMp4FamilyExtension("m4v"));
        Assert.True(TrackFixer.IsMp4FamilyExtension(".mov"));
        Assert.False(TrackFixer.IsMp4FamilyExtension("mkv"));
        Assert.False(TrackFixer.IsMp4FamilyExtension("webm"));
        Assert.False(TrackFixer.IsMp4FamilyExtension(string.Empty));
    }

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams)
        => new() { Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Vid(int index, string codec)
        => new() { Index = index, CodecType = "video", CodecName = codec };

    private static FfprobeStreamInfo Aud(int index, string codec)
        => new() { Index = index, CodecType = "audio", CodecName = codec };
}
