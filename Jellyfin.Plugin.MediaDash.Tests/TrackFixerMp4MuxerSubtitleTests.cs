using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Issue #56: HandBrake-authored .m4v with a VobSub subtitle track was failing every TrackFixer
// remux with "Tag text incompatible with output codec id '98314'" — the ipod muxer refuses
// bitmap subs under -c copy. ComputeMuxerIncompatibleSubtitleIndexes decides which subtitle
// stream indexes to fold into the remux's negative-map list so the fix succeeds instead of
// leaving a raw ffmpeg error on the Errors tab.
public class TrackFixerMp4MuxerSubtitleTests
{
    [Fact]
    public void VobSubInM4vIsFlaggedForDrop()
    {
        var probe = Probe(
            Sub(0, "dvd_subtitle"),
            Aud(1, "aac"));

        var dropped = TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "m4v");

        Assert.Equal(new[] { 0 }, dropped);
    }

    [Fact]
    public void PgsAndDvbSubsAreFlaggedForDropInMp4()
    {
        var probe = Probe(
            Sub(0, "hdmv_pgs_subtitle"),
            Sub(1, "dvb_subtitle"),
            Aud(2, "aac"));

        var dropped = TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mp4");

        Assert.Equal(new[] { 0, 1 }, dropped);
    }

    [Fact]
    public void MovTextInMp4IsKept()
    {
        var probe = Probe(
            Sub(0, "mov_text"),
            Aud(1, "aac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mp4"));
    }

    [Fact]
    public void MkvOutputNeverDropsSubs()
    {
        var probe = Probe(
            Sub(0, "dvd_subtitle"),
            Sub(1, "hdmv_pgs_subtitle"),
            Aud(2, "flac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mkv"));
    }

    [Fact]
    public void LeadingDotOnExtensionIsAccepted()
    {
        var probe = Probe(Sub(0, "dvd_subtitle"), Aud(1, "aac"));
        Assert.Equal(new[] { 0 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, ".m4v"));
    }

    [Fact]
    public void EmptyOrMissingStreamsReturnsEmpty()
    {
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(new FfprobeData(), "m4v"));
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(Probe(), "m4v"));
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(Probe(Aud(0, "aac")), string.Empty));
    }

    // GitHub #56 (2026-09-15 retry): the exact-string blacklist missed ffmpeg's older aliases —
    // HandBrake .m4v outputs sometimes report VobSub as `dvdsub` (not `dvd_subtitle`) or with
    // an empty codec_tag_string, both of which slipped past the 1.0.7.6 fold-in. Flipped to a
    // whitelist of known-mp4-compatible subtitle codecs — every other codec_name (including
    // aliases and empty) is now dropped so the ipod muxer never sees anything it can't tag.
    [Theory]
    [InlineData("dvdsub")]          // older ffmpeg alias for dvd_subtitle
    [InlineData("dvd_subtitle")]    // canonical name
    [InlineData("pgssub")]          // older alias for hdmv_pgs_subtitle
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvbsub")]          // older alias for dvb_subtitle
    [InlineData("dvb_subtitle")]
    [InlineData("xsub")]            // DivX bitmap sub
    [InlineData("subrip")]          // text but not MP4-native under -c copy (needs -c:s mov_text)
    [InlineData("srt")]             // subrip alias emitted by some ffprobe builds
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("")]                // fadern's file: empty codec_name (bogus ffprobe output)
    public void MuxerHostileSubIsDroppedForMp4(string codecName)
    {
        var probe = Probe(Sub(0, codecName), Aud(1, "aac"));

        Assert.Equal(new[] { 0 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "m4v"));
    }

    [Fact]
    public void NullCodecNameSubIsDroppedForMp4()
    {
        var probe = Probe(Sub(0, codec: null!), Aud(1, "aac"));

        Assert.Equal(new[] { 0 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "m4v"));
    }

    [Theory]
    [InlineData("mov_text")]  // native MP4 timed text
    [InlineData("tx3g")]      // 3GPP timed text
    [InlineData("webvtt")]    // valid with movflags on many ffmpeg builds
    public void Mp4NativeSubsAreKept(string codecName)
    {
        var probe = Probe(Sub(0, codecName), Aud(1, "aac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "m4v"));
    }

    [Fact]
    public void MkvOutputKeepsEverySubtitleCodec()
    {
        var probe = Probe(
            Sub(0, "dvdsub"),
            Sub(1, "hdmv_pgs_subtitle"),
            Sub(2, "subrip"),
            Sub(3, "ass"),
            Sub(4, "xsub"),
            Sub(5, codec: null!),
            Aud(6, "flac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mkv"));
    }

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams)
        => new() { Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Sub(int index, string codec)
        => new() { Index = index, CodecType = "subtitle", CodecName = codec };

    private static FfprobeStreamInfo Aud(int index, string codec)
        => new() { Index = index, CodecType = "audio", CodecName = codec };
}
