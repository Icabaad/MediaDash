using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Encoder-quirks audit Q14: the video-stream picker in QualityScanner and TranscodeFixer
// filters attached_pic streams via FfprobeStreamInfo.IsAttachedPicture. If this helper
// silently changes shape (Disposition typo, wrong int comparison, etc.) the fixers will
// re-encode yt-dlp thumbnails as movies. Pin the semantics here.
public sealed class FfprobeStreamDispositionTests
{
    [Fact]
    public void AttachedPictureSetReadsTrue()
    {
        var stream = new FfprobeStreamInfo
        {
            Disposition = new Dictionary<string, int> { ["attached_pic"] = 1 }
        };

        Assert.True(stream.IsAttachedPicture);
    }

    [Fact]
    public void AttachedPictureClearedReadsFalse()
    {
        var stream = new FfprobeStreamInfo
        {
            Disposition = new Dictionary<string, int> { ["attached_pic"] = 0 }
        };

        Assert.False(stream.IsAttachedPicture);
    }

    [Fact]
    public void NoDispositionReadsFalse()
    {
        Assert.False(new FfprobeStreamInfo().IsAttachedPicture);
    }

    [Fact]
    public void DispositionWithoutAttachedPicKeyReadsFalse()
    {
        var stream = new FfprobeStreamInfo
        {
            Disposition = new Dictionary<string, int> { ["default"] = 1, ["forced"] = 0 }
        };

        Assert.False(stream.IsAttachedPicture);
    }

    [Fact]
    public void HearingImpairedAndAttachedPictureAreIndependent()
    {
        var stream = new FfprobeStreamInfo
        {
            Disposition = new Dictionary<string, int>
            {
                ["hearing_impaired"] = 1,
                ["attached_pic"] = 0
            }
        };

        Assert.True(stream.IsHearingImpaired);
        Assert.False(stream.IsAttachedPicture);
    }
}
