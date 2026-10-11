using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Detects files whose audio is stored far away from the video it belongs to. Such a file plays
/// from the start (the player reads it front to back) but after a seek the reader lands in a
/// stretch that holds video only, so on-the-fly transcodes produce no sound and clients stall.
/// </summary>
public static class InterleaveAnalyzer
{
    /// <summary>
    /// The <c>reason</c> value stored in an issue's details when a file is flagged by this check.
    /// </summary>
    public const string IssueReason = "audio-not-interleaved";

    /// <summary>
    /// Audio stored more than this many seconds away from its video counts as badly interleaved.
    /// ffmpeg's own muxer never lets streams drift further apart than 10 seconds.
    /// </summary>
    public const double MaxOffsetSeconds = 10;

    /// <summary>
    /// Files shorter than this are not checked: the sample windows would overlap.
    /// </summary>
    public const double MinDurationSeconds = 600;

    private const int SampleCount = 9;
    private const double SampleLengthSeconds = 5;

    // A seek lands on the keyframe before the requested time, so packets can start this early.
    private const double SampleLeadSeconds = 20;

    /// <summary>
    /// Picks the positions at which the file is sampled, evenly spread over its runtime.
    /// </summary>
    /// <param name="durationSeconds">The file's duration in seconds.</param>
    /// <returns>The sample start times in seconds; empty when the file is too short to sample.</returns>
    public static IReadOnlyList<double> GetSampleStarts(double durationSeconds)
    {
        if (durationSeconds < MinDurationSeconds)
        {
            return Array.Empty<double>();
        }

        var starts = new double[SampleCount];
        for (var i = 0; i < SampleCount; i++)
        {
            starts[i] = Math.Floor(durationSeconds * (i + 1) / (SampleCount + 1));
        }

        return starts;
    }

    /// <summary>
    /// Formats sample positions as an ffprobe <c>-read_intervals</c> argument.
    /// </summary>
    /// <param name="sampleStarts">The sample start times in seconds.</param>
    /// <returns>The argument value, e.g. <c>540%+5,1080%+5</c>.</returns>
    public static string ToReadIntervals(IReadOnlyList<double> sampleStarts)
    {
        ArgumentNullException.ThrowIfNull(sampleStarts);
        return string.Join(
            ',',
            sampleStarts.Select(s => string.Format(CultureInfo.InvariantCulture, "{0:F0}%+{1:F0}", s, SampleLengthSeconds)));
    }

    /// <summary>
    /// Parses one line of <c>ffprobe -show_entries packet=stream_index,pts_time -of csv=p=0</c>.
    /// </summary>
    /// <param name="line">The output line.</param>
    /// <param name="streamIndex">The packet's stream index.</param>
    /// <param name="ptsSeconds">The packet's presentation time in seconds.</param>
    /// <returns>False for blank lines and packets without a timestamp.</returns>
    public static bool TryParsePacketLine(string? line, out int streamIndex, out double ptsSeconds)
    {
        streamIndex = 0;
        ptsSeconds = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var fields = line.Split(',');
        return fields.Length >= 2
            && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out streamIndex)
            && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ptsSeconds)
            && double.IsFinite(ptsSeconds);
    }

    /// <summary>Checks that every requested window contains usable video evidence.</summary>
    /// <param name="packets">The returned packets.</param>
    /// <param name="sampleStarts">The requested windows.</param>
    /// <param name="videoIndex">The main video stream.</param>
    /// <returns>True when all windows contain video.</returns>
    public static bool HasVideoInEverySample(IReadOnlyList<(int StreamIndex, double PtsSeconds)> packets, IReadOnlyList<double> sampleStarts, int videoIndex)
        => sampleStarts.Count > 0 && sampleStarts.All(start => packets.Any(p => p.StreamIndex == videoIndex
            && double.IsFinite(p.PtsSeconds) && p.PtsSeconds >= start - SampleLeadSeconds
            && p.PtsSeconds <= start + SampleLengthSeconds + SampleLeadSeconds));

    /// <summary>
    /// Evaluates the packets read at the sample positions. A sample is suspicious when the reader
    /// found video there but no packet at all from one of the audio streams.
    /// </summary>
    /// <param name="packets">The packets ffprobe returned, in file order.</param>
    /// <param name="sampleStarts">The sample start times that were requested.</param>
    /// <param name="videoIndex">The stream index of the main video stream.</param>
    /// <param name="audioIndexes">The stream indexes of the audio streams.</param>
    /// <returns>True when at least one sample held video without matching audio.</returns>
    public static bool HasSampleWithoutAudio(
        IEnumerable<(int StreamIndex, double PtsSeconds)> packets,
        IReadOnlyList<double> sampleStarts,
        int videoIndex,
        IReadOnlyCollection<int> audioIndexes)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(sampleStarts);
        ArgumentNullException.ThrowIfNull(audioIndexes);
        if (audioIndexes.Count == 0)
        {
            return false;
        }

        var videoSeen = new bool[sampleStarts.Count];
        var audioSeen = new HashSet<int>[sampleStarts.Count];
        for (var i = 0; i < audioSeen.Length; i++)
        {
            audioSeen[i] = [];
        }

        foreach (var (streamIndex, pts) in packets)
        {
            for (var i = 0; i < sampleStarts.Count; i++)
            {
                if (pts < sampleStarts[i] - SampleLeadSeconds || pts > sampleStarts[i] + SampleLengthSeconds + SampleLeadSeconds)
                {
                    continue;
                }

                if (streamIndex == videoIndex)
                {
                    videoSeen[i] = true;
                }
                else if (audioIndexes.Contains(streamIndex))
                {
                    audioSeen[i].Add(streamIndex);
                }

                break;
            }
        }

        for (var i = 0; i < sampleStarts.Count; i++)
        {
            if (videoSeen[i] && audioSeen[i].Count < audioIndexes.Count)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks every packet in file order and measures how far each audio packet's timestamp is from
    /// the video packet stored just before it. In a healthy file that distance stays well under a second.
    /// </summary>
    /// <param name="packets">All packets of the file, in file order.</param>
    /// <param name="videoIndex">The stream index of the main video stream.</param>
    /// <param name="audioIndexes">The stream indexes of the audio streams.</param>
    /// <returns>The largest distance found, in seconds.</returns>
    public static double MeasureMaxOffsetSeconds(
        IEnumerable<(int StreamIndex, double PtsSeconds)> packets,
        int videoIndex,
        IReadOnlyCollection<int> audioIndexes)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(audioIndexes);

        var walk = new PacketWalk(videoIndex, audioIndexes);
        foreach (var (streamIndex, pts) in packets)
        {
            walk.Add(streamIndex, pts);
        }

        return walk.MaximumOffsetSeconds;
    }

    internal sealed class PacketWalk(int videoIndex, IReadOnlyCollection<int> audioIndexes)
    {
        private readonly HashSet<int> _audioIndexes = new(audioIndexes);
        private readonly HashSet<int> _audioSeen = [];
        private double? _lastVideo;

        internal double MaximumOffsetSeconds { get; private set; }

        internal bool HasAllStreams => _lastVideo.HasValue && _audioSeen.SetEquals(_audioIndexes);

        internal void Add(int streamIndex, double pts)
        {
            if (streamIndex == videoIndex)
            {
                _lastVideo = pts;
            }
            else if (_audioIndexes.Contains(streamIndex))
            {
                _audioSeen.Add(streamIndex);
                if (_lastVideo is double videoPts)
                {
                    MaximumOffsetSeconds = Math.Max(MaximumOffsetSeconds, Math.Abs(pts - videoPts));
                }
            }
        }
    }
}
