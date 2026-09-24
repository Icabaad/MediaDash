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

    // Bug-pattern audit round 3 (2026-09-19): MKV target used to fall through as "no
    // incompatible subs" and mkvmuxer aborted mid-remux on tx3g / mov_text — both are MP4-
    // native timed-text codecs with no MKV mapping. Rung 4 of the Playability repair ladder
    // hits this every time it re-encodes an MP4 source with a tx3g track.
    [Theory]
    [InlineData("tx3g")]
    [InlineData("mov_text")]
    public void MkvOutputDropsMp4NativeTimedText(string codec)
    {
        var probe = Probe(Vid(0, "h264"), Sub(1, codec), Aud(2, "aac"));

        Assert.Equal(new[] { 1 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mkv"));
    }

    [Theory]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    public void MkvOutputKeepsUniversalSubtitleCodecs(string codec)
    {
        // Regression: MKV is permissive by design. Adding the tx3g/mov_text blacklist must not
        // accidentally reject the codecs Matroska handles cleanly under -c copy.
        var probe = Probe(Vid(0, "h264"), Sub(1, codec), Aud(2, "aac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mkv"));
    }

    [Fact]
    public void WebmOutputAlsoDropsMp4NativeTimedText()
    {
        // WebM uses the same matroska muxer family with the same tx3g/mov_text limitation.
        var probe = Probe(Vid(0, "vp9"), Sub(1, "tx3g"), Aud(2, "opus"));

        Assert.Equal(new[] { 1 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "webm"));
    }

    // Bug-pattern audit round 3 (2026-09-19): the mpegts family (.ts / .m2ts / .mts) refuses
    // most subtitle codecs under -c copy — only dvb_subtitle and dvb_teletext survive. The
    // PlayabilityFixer rung 1 same-container remux hits this every time a Blu-ray .ts rip
    // carries an hdmv_pgs_subtitle track. Fold-in drops the offending sub so rung 1 keeps
    // the container instead of falling through to rung 3's MKV coerce (which resets Jellyfin's
    // watch history).
    [Theory]
    [InlineData("hdmv_pgs_subtitle")]  // Blu-ray PGS — most common
    [InlineData("pgssub")]             // older alias
    [InlineData("dvd_subtitle")]       // DVD bitmap
    [InlineData("dvdsub")]             // older alias
    [InlineData("subrip")]             // needs transcode to dvb_subtitle for mpegts
    [InlineData("ass")]
    [InlineData("mov_text")]           // MP4-native, mpegts refuses
    [InlineData("")]                   // empty codec_name — drop rather than risk mux failure
    public void MpegTsOutputDropsHostileSubs(string codec)
    {
        var probe = Probe(Vid(0, "h264"), Sub(1, codec), Aud(2, "ac3"));

        Assert.Equal(new[] { 1 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "ts"));
    }

    [Theory]
    [InlineData("dvb_subtitle")]
    [InlineData("dvb_teletext")]
    public void MpegTsOutputKeepsNativeDvbSubs(string codec)
    {
        // Sanity: mpegts natively supports these. Adding the hostile blacklist must not
        // accidentally drop the codecs mpegts DOES handle under -c copy.
        var probe = Probe(Vid(0, "h264"), Sub(1, codec), Aud(2, "ac3"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "ts"));
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("m2ts")]
    [InlineData("mts")]
    [InlineData(".ts")]
    public void EveryMpegTsFamilyExtensionFoldsInHostileSubs(string ext)
    {
        // Every mpegts-family container routes through the same muxer with the same limitations.
        var probe = Probe(Vid(0, "h264"), Sub(1, "hdmv_pgs_subtitle"), Aud(2, "ac3"));

        Assert.Equal(new[] { 1 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, ext));
    }

    [Fact]
    public void UnknownContainerReturnsEmpty()
    {
        // .flv, .avi, .ogg — no fold-in defined. Same-container remuxes for these fall through
        // to rung 3 today; the fold-in helper returning empty is intentional (no coverage yet).
        var probe = Probe(Vid(0, "h264"), Sub(1, "hdmv_pgs_subtitle"), Aud(2, "ac3"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "flv"));
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "avi"));
    }

    private static FfprobeStreamInfo Vid(int index, string codec)
        => new() { Index = index, CodecType = "video", CodecName = codec };

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams)
        => new() { Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Sub(int index, string codec)
        => new() { Index = index, CodecType = "subtitle", CodecName = codec };

    private static FfprobeStreamInfo Aud(int index, string codec)
        => new() { Index = index, CodecType = "audio", CodecName = codec };
}
