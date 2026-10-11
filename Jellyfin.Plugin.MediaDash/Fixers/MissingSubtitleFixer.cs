using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Downloads a subtitle track for a video that has none in the wanted languages, via Jellyfin's configured
/// subtitle providers (OpenSubtitles etc.). Depends on the admin having a provider set up in Jellyfin;
/// without one, every fix reports "no providers configured" and nothing is downloaded.
/// </summary>
public sealed class MissingSubtitleFixer : IFixer
{
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MissingSubtitleFixer> _logger;
    private readonly FfprobeService? _ffprobe;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingSubtitleFixer"/> class.
    /// </summary>
    /// <param name="subtitleManager">Jellyfin's subtitle download service.</param>
    /// <param name="libraryManager">The Jellyfin library manager, used to resolve the video from the issue's item id.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="ffprobe">Resolves flagged original audio on hosts without original-language metadata.</param>
    public MissingSubtitleFixer(ISubtitleManager subtitleManager, ILibraryManager libraryManager, ILogger<MissingSubtitleFixer> logger, FfprobeService? ffprobe = null)
    {
        _subtitleManager = subtitleManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _ffprobe = ffprobe;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.MissingSubtitles;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (_libraryManager.GetItemById(issue.ItemId) is not Video video)
        {
            return FixResult.Fail(FixReason.SourceMissing, "The library item is no longer available — re-scan to refresh the list.");
        }

        var probe = LanguageHelper.WantsOriginal(config.AllowedSubtitleLanguages) && _ffprobe is not null
            ? await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false) : null;
        var wanted = LanguageHelper.ResolveLanguages(config.AllowedSubtitleLanguages, video, probe);
        if (wanted is null)
        {
            return FixResult.Refused(FixReason.ConsentChanged, "The original language could not be identified. No subtitle search was made; select a language explicitly or refresh the item's original-language metadata.");
        }

        if (wanted.Length == 0)
        {
            return FixResult.Refused(FixReason.ConsentChanged, "No wanted subtitle languages configured. Set some in Settings → Languages, then re-scan.");
        }

        // Skip the "already added since scan" pre-check — a re-scan clears the issue in that case, and
        // refreshing metadata inline here would need a Task path that's more code than the rare race avoids.

        // Dry-run: describe the intent without hitting the subtitle provider. Previously we called
        // SearchSubtitles first so the preview could name the exact hit (e.g. "would download fr subtitle
        // from OpenSubtitles"), but that consumed provider quota and leaked which files exist in the
        // library on every dry-run. The generic preview is worth the isolation — dry-run stays a pure
        // read of *local* state.
        if (config.DryRun)
        {
            var languages = string.Join(", ", wanted.Select(LanguageHelper.Normalize));
            var fileLabel = video.Name ?? System.IO.Path.GetFileNameWithoutExtension(issue.Path);
            return FixResult.DryRun(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "try to download {0} subtitle(s) for {1} from your configured providers",
                    languages,
                    fileLabel),
                0);
        }

        var attempts = new List<string>();
        var providerQuotaExhausted = false;
        foreach (var lang in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = LanguageHelper.Normalize(lang);
            RemoteSubtitleInfo[] hits;
            try
            {
                // isAutomated=false matches Jellyfin's manual "Search subtitles" UI: OpenSubtitles.com
                // returns full episode results instead of the trimmed set its automated path emits to
                // preserve free-tier quota. Users who want to respect that throttling can flip the
                // "Ignore rate limiting" setting off; otherwise the ~⅔ episode miss rate returns.
                hits = await _subtitleManager.SearchSubtitles(video, normalized, isPerfectMatch: null, isAutomated: !config.SubtitleIgnoreRateLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                attempts.Add($"{normalized}: {ex.Message}");
                if (LooksLikeProviderQuota(ex.Message))
                {
                    providerQuotaExhausted = true;
                }

                continue;
            }
            catch (HttpRequestException ex)
            {
                attempts.Add($"{normalized}: provider unreachable ({ex.Message})");
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // OpenSubtitles' HttpClient throws TaskCanceledException on network timeout even when
                // our outer cancellation token hasn't fired. Treat that as an unreachable provider —
                // don't let one stalled DNS lookup abort the whole fix run.
                attempts.Add($"{normalized}: provider timed out ({ex.Message})");
                continue;
            }

            if (hits is null || hits.Length == 0)
            {
                attempts.Add($"{normalized}: no matches from any provider");
                continue;
            }

            // Hearing-impaired mode: prefer an SDH/CC hit. Fall back to the first non-HI result if no
            // provider surfaced an HI variant, so the fix still lands a subtitle rather than skipping.
            var pick = config.SubtitleHearingImpairedMode
                ? (hits.FirstOrDefault(h => h.HearingImpaired == true) ?? hits[0])
                : hits[0];
            var actionText = string.Format(
                CultureInfo.InvariantCulture,
                "downloaded {0} subtitle for {1} from {2}",
                normalized,
                video.Name ?? System.IO.Path.GetFileNameWithoutExtension(issue.Path),
                pick.ProviderName ?? "provider");

            try
            {
                await _subtitleManager.DownloadSubtitles(video, pick.Id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Subtitle download: {Action}", actionText);
                return new FixResult { Success = true, Outcome = FixOutcome.Applied, Message = actionText, BytesFreed = 0 };
            }
            catch (InvalidOperationException ex)
            {
                attempts.Add($"{normalized}: download failed ({ex.Message})");
                if (LooksLikeProviderQuota(ex.Message))
                {
                    providerQuotaExhausted = true;
                }
            }
            catch (HttpRequestException ex)
            {
                attempts.Add($"{normalized}: download failed ({ex.Message})");
            }
        }

        var reason = attempts.Count == 0
            ? "No subtitle providers are configured in Jellyfin's Dashboard → Metadata."
            : string.Join("; ", attempts);
        // Provider quota is the one "please try again later" outcome the scheduler needs to
        // recognise — one exhausted provider means every remaining MissingSubtitles issue in
        // this run will hit the same wall. Return a typed Deferred outcome so FixTask can
        // short-circuit the rest of the run without parsing the message string. Boundary
        // parsing lives ONLY here (per the master plan's "third-party stderr/provider strings
        // may be parsed once at that integration boundary" rule).
        if (providerQuotaExhausted)
        {
            return FixResult.Deferred(FixReason.ProviderQuota, reason);
        }

        return FixResult.Fail(FixReason.ProcessFailed, reason);
    }

    /// <summary>Boundary parser for OpenSubtitles' quota-exhausted signal. The provider surfaces
    /// two phrasings in <c>InvalidOperationException.Message</c>; both flip the fixer to
    /// <see cref="FixReason.ProviderQuota"/> so <c>FixTask</c> can short-circuit the rest of the
    /// subtitle queue in this run without message classifiers of its own. Public for direct
    /// coverage from the batch-3 tests.</summary>
    /// <param name="message">The provider exception message.</param>
    /// <returns>True when the phrase indicates an exhausted download quota.</returns>
    internal static bool LooksLikeProviderQuota(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("download limit reached", StringComparison.OrdinalIgnoreCase)
            || message.Contains("download quota", StringComparison.OrdinalIgnoreCase);
    }
}
