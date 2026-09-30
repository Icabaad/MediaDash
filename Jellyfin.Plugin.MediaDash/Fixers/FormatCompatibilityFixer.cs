using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Fixer for <see cref="IssueType.FormatCompatibility"/> issues. Dispatches to the
/// registered <see cref="IFormatConverter"/> for the row's converter key, then hands the
/// staged output to <see cref="VerifiedReplacementService"/> for the audited
/// swap-and-recycle protocol.
/// </summary>
public sealed class FormatCompatibilityFixer : IFixer
{
    private const string InflightTail = ".mediadash.tmp";

    private readonly IFormatConverterRegistry _converters;
    private readonly ILibraryCollectionResolver _resolver;

    // ponytail: nullable — tests never exercise the VRS path (all 4 test cases exit early
    // via refusal prechecks). Null guard below throws loudly if production code somehow
    // reaches ReplaceAsync without DI wiring it.
    private readonly VerifiedReplacementService? _replacement;

    // ponytail: nullable guard/protection — null = skip check in tests; production DI always supplies them.
    private readonly LibraryGuard? _guard;
    private readonly ProtectionService? _protection;

    private readonly IPluginConfigurationProvider _configProvider;

    // ponytail: delegate instead of full ILibraryManager — tests supply a lambda returning
    // a stub item; DI wires `id => libraryManager.GetItemById(id)`. The success path
    // (DeleteItem + monitor) is never reached by the 4 unit tests, so those two remain optional.
    private readonly Func<Guid, BaseItem?> _getItemById;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILibraryMonitor? _libraryMonitor;

    private readonly ILogger<FormatCompatibilityFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatCompatibilityFixer"/> class for
    /// production DI. All parameters are mandatory.
    /// </summary>
    /// <param name="converters">Converter registry.</param>
    /// <param name="resolver">Library collection-type resolver.</param>
    /// <param name="replacement">Verified replacement service.</param>
    /// <param name="guard">Library guard.</param>
    /// <param name="protection">Protection service.</param>
    /// <param name="configProvider">Config snapshot provider.</param>
    /// <param name="libraryManager">Jellyfin library manager for post-fix item purge.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor for FS-change notifications.</param>
    /// <param name="logger">The logger.</param>
    public FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService replacement,
        LibraryGuard guard,
        ProtectionService protection,
        IPluginConfigurationProvider configProvider,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<FormatCompatibilityFixer> logger)
        : this(
            converters,
            resolver,
            replacement,
            guard,
            protection,
            configProvider,
            id => libraryManager.GetItemById(id),
            libraryManager,
            libraryMonitor,
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatCompatibilityFixer"/> class with
    /// injectable seams for unit tests. Guards and VRS may be null; the unit tests exit via
    /// refusal prechecks before any of those are invoked.
    /// </summary>
    /// <param name="converters">Converter registry.</param>
    /// <param name="resolver">Library collection-type resolver.</param>
    /// <param name="replacement">Verified replacement service. Null is accepted — throws if reached.</param>
    /// <param name="guard">Library guard. Null = skip IsInsideLibrary check.</param>
    /// <param name="protection">Protection service. Null = skip IsFixerBlocked check.</param>
    /// <param name="configProvider">Config snapshot provider.</param>
    /// <param name="getItemById">Item-lookup delegate. Return null to treat item as not found.</param>
    /// <param name="libraryManager">Optional — used only on the success path for DeleteItem.</param>
    /// <param name="libraryMonitor">Optional — used only on the success path for FS change notification.</param>
    /// <param name="logger">The logger.</param>
    internal FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService? replacement,
        LibraryGuard? guard,
        ProtectionService? protection,
        IPluginConfigurationProvider configProvider,
        Func<Guid, BaseItem?> getItemById,
        ILibraryManager? libraryManager,
        ILibraryMonitor? libraryMonitor,
        ILogger<FormatCompatibilityFixer> logger)
    {
        _converters = converters;
        _resolver = resolver;
        _replacement = replacement;
        _guard = guard;
        _protection = protection;
        _configProvider = configProvider;
        _getItemById = getItemById;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.FormatCompatibility;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = _configProvider.GetSnapshot();

        // 1. Source must exist.
        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The source file is missing; re-scan to refresh the issue list.");
        }

        // 2. Details must parse.
        var details = ParseDetails(issue.DetailsJson);
        if (details is null || string.IsNullOrEmpty(details.ConverterKey) || string.IsNullOrEmpty(details.TargetExt))
        {
            return FixResult.Refused(FixReason.SourceExtensionNotConvertible, "Issue details are missing the conversion target.");
        }

        // 3. Converter must be registered.
        var converter = _converters.Get(details.ConverterKey);
        if (converter is null)
        {
            return FixResult.Refused(FixReason.ConverterUnavailable, "Converter '" + details.ConverterKey + "' is not registered.");
        }

        // 4. Per-kind toggle.
        if (!IsKindEnabled(config, converter.Kind))
        {
            return FixResult.Refused(FixReason.TypeDisabled, "The per-kind converter toggle is disabled in Settings.");
        }

        // 5. Resolve library type via item lookup + resolver.
        // For unindexed files (ItemId == Guid.Empty) the item will be null; fall back to the
        // libraryKind the scanner froze into DetailsJson at detection time so the mixed-library gate
        // below uses the actual library type rather than treating every unindexed file as mixed.
        var item = _getItemById(issue.ItemId);
        CollectionType? libraryType;
        if (item is not null)
        {
            libraryType = _resolver.Resolve(item);
        }
        else if (details.MixedLibrary)
        {
            libraryType = null; // genuinely mixed
        }
        else
        {
            libraryType = Enum.TryParse<CollectionType>(details.LibraryKind, ignoreCase: true, out var parsed) ? parsed : (CollectionType?)null;
        }

        var isMixed = libraryType is null;

        // 6. Mixed-library gate: require per-row approval.
        if (isMixed && !details.ApprovedInMixedLibrary)
        {
            return FixResult.Refused(
                FixReason.MixedLibraryUserReviewOnly,
                "This file is in a mixed-content library. Approve the specific row on the Issues tab to convert it.");
        }

        // 7. Library-type mismatch: converter not valid for this library kind.
        if (!isMixed && !converter.AllowedLibraryTypes.Contains(libraryType))
        {
            return FixResult.Refused(
                FixReason.LibraryTypeMismatch,
                "This converter does not run inside " + libraryType + " libraries.");
        }

        // 8. Library guard.
        if (_guard is not null && !_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "File is outside every configured library folder.");
        }

        // 9. Protection check.
        if (_protection is not null && _protection.IsFixerBlocked(issue.Path, IssueType.FormatCompatibility))
        {
            return FixResult.Refused(FixReason.Protected, "This file is on the never-modify list.");
        }

        // 10. Ensure toolchain.
        var readiness = await converter.EnsureToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (readiness != ConverterAvailability.Ready)
        {
            return FixResult.Refused(
                FixReason.ConverterUnavailable,
                "Converter toolchain is not ready. Install Calibre or wait for the download to complete.");
        }

        // P2-7: user override — only for the ebook converter, and only when config picks one of
        // the supported alternatives. Other converters (Comic/Image/Music/Video) ignore their
        // preferred-target settings until Phase 4 wires them. Do NOT mutate details.
        var targetExt = details.TargetExt;
        if (converter.Kind == MediaKind.Ebook && !string.IsNullOrWhiteSpace(config.EbookPreferredTarget))
        {
            var pref = config.EbookPreferredTarget.Trim().ToLowerInvariant();
            if (pref == "epub" || pref == "pdf")
            {
                targetExt = "." + pref;
            }
        }

        // 11. Dry-run — return preview without touching files.
        if (config.DryRun)
        {
            return FixResult.DryRun("convert " + Path.GetFileName(issue.Path) + " to " + targetExt, 0);
        }

        // 12. Conversion.
        // P1-2: capture source identity BEFORE conversion so a mid-conversion source mutation
        // is detected by VRS's identity recheck and the stale encode is refused.
        var sourceInfoBeforeConvert = new FileInfo(issue.Path);
        var expectedSourceLength = sourceInfoBeforeConvert.Length;
        var expectedSourceMtimeTicks = sourceInfoBeforeConvert.LastWriteTimeUtc.Ticks;

        var stagedPath = issue.Path + InflightTail + targetExt;

        // Keep converter.Kind in a local so the P1-1 policy callback below can capture it
        // without capturing the converter object itself (avoids converter keeping alive longer
        // than needed and makes the closure dependency explicit).
        var converterKind = converter.Kind;

        ConversionResult conversion;
        try
        {
            conversion = await converter.ConvertAsync(issue.Path, stagedPath, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedPath);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(stagedPath);
            return FixResult.Fail(FixReason.ConverterFailed, "Converter threw unexpectedly.", ex.Message);
        }

        if (!conversion.Success || conversion.StagedPath is null || !File.Exists(conversion.StagedPath))
        {
            SafeDelete(stagedPath);
            return FixResult.Fail(FixReason.ConverterFailed, conversion.Reason, conversion.TechnicalDetail);
        }

        // 13. Verified replacement (swap-and-recycle protocol).
        var destinationPath = Path.ChangeExtension(issue.Path, targetExt);
        var request = new ReplacementRequest(
            SourcePath: issue.Path,
            OutputPath: conversion.StagedPath,
            DestinationPath: destinationPath,
            ExpectedSourceLength: expectedSourceLength,
            ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
            RecycleOriginal: config.FormatCompatibilityDisposal == DisposalMethod.RecycleBin);

        var vrs = _replacement ?? throw new InvalidOperationException("FormatCompatibilityFixer: VerifiedReplacementService is required for the live path but was not injected.");

        var stagedForVerify = conversion.StagedPath;

        // P1-1: re-consult live config + protection immediately before the commit so a mid-
        // conversion dry-run flip, fixer disable, per-kind toggle off, or protection addition
        // aborts before any file is touched. Pattern from TranscodeFixer.EvaluateCommitPolicy
        // (external audit P1-F3, 1.0.8.0).
        PolicyDecision CheckCurrentPolicy()
        {
            var freshCfg = _configProvider.GetSnapshot();
            if (freshCfg.DryRun)
            {
                return PolicyDecision.Refuse(FixReason.TypeDisabled);
            }

            var mode = freshCfg.GetFixMode(IssueType.FormatCompatibility);
            if (mode == FixMode.Off || mode == FixMode.DetectOnly)
            {
                return PolicyDecision.Refuse(FixReason.TypeDisabled);
            }

            if (!IsKindEnabled(freshCfg, converterKind))
            {
                return PolicyDecision.Refuse(FixReason.TypeDisabled);
            }

            if (_protection is not null && _protection.IsFixerBlocked(issue.Path, issue.Type))
            {
                return PolicyDecision.Refuse(FixReason.Protected);
            }

            return PolicyDecision.Allow;
        }

        var replaceResult = await vrs.ReplaceAsync(
            request,
            ct => VerifyOutputAsync(stagedForVerify, ct),
            CheckCurrentPolicy,
            cancellationToken).ConfigureAwait(false);

        if (!replaceResult.Applied)
        {
            // P2-4: only delete the staged output when NEITHER a recycle nor a recovery path was
            // recorded. If VRS moved the staged output to a recovery path, it's an artefact the
            // user may need; deleting stagedPath is either a no-op or wrong in that case.
            if (string.IsNullOrEmpty(replaceResult.RecyclePath) && string.IsNullOrEmpty(replaceResult.RecoveryPath))
            {
                SafeDelete(stagedPath);
            }

            var fail = FixResult.Fail(replaceResult.Reason, replaceResult.TechnicalDetail ?? "Replacement failed.");
            fail.RecyclePath = replaceResult.RecyclePath;
            fail.RecoveryPath = replaceResult.RecoveryPath;
            return fail;
        }

        // 14. Post-fix library notifications.
        _libraryMonitor?.ReportFileSystemChanged(destinationPath);
        if (_libraryManager is not null && item is not null && !string.Equals(item.Path, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions
            {
                DeleteFileLocation = false,
                DeleteFromExternalProvider = false
            });
        }

        _logger.LogInformation("FormatCompatibilityFixer: converted {Source} → {Dest}", issue.Path, destinationPath);
        return new FixResult
        {
            Success = true,
            Message = "Converted " + Path.GetFileName(issue.Path) + " → " + Path.GetFileName(destinationPath),
            RecyclePath = replaceResult.RecyclePath
        };
    }

    private static bool IsKindEnabled(PluginConfiguration config, MediaKind kind) => kind switch
    {
        MediaKind.Ebook => config.ConvertEbooks,
        MediaKind.Comic => config.ConvertComics,
        MediaKind.Audiobook => config.ConvertAudiobooks,
        MediaKind.Image => config.ConvertImages,
        MediaKind.Music => config.ConvertMusic,
        MediaKind.Video => config.ConvertVideos,
        _ => false
    };

    private static Details? ParseDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var converterKey = root.TryGetProperty("converter", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            var targetExt = root.TryGetProperty("targetExt", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
            var mixed = root.TryGetProperty("mixedLibrary", out var m) && m.ValueKind == JsonValueKind.True;
            var approvedInMixed = root.TryGetProperty("approvedInMixedLibrary", out var a) && a.ValueKind == JsonValueKind.True;
            var libraryKind = root.TryGetProperty("libraryKind", out var lk) && lk.ValueKind == JsonValueKind.String ? lk.GetString() ?? string.Empty : string.Empty;
            return new Details(converterKey, targetExt, mixed, approvedInMixed, libraryKind);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // P1-3: dispatch to the existing BookProbeService/ComicProbeService structural probes so
    // an unreadable or truncated converted output is refused before the original is disposed.
    // VRS verifyOutput signature is Func<CancellationToken, Task<string?>> — stagedPath
    // is captured by the caller's closure, ct is passed by VRS.
    // internal for direct unit testing (InternalsVisibleTo tests project).
    internal static Task<string?> VerifyOutputAsync(string stagedPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(stagedPath))
        {
            return Task.FromResult<string?>("Converted output is missing after conversion.");
        }

        if (new FileInfo(stagedPath).Length == 0)
        {
            return Task.FromResult<string?>("Converted output is empty.");
        }

        var ext = Path.GetExtension(stagedPath).ToLowerInvariant();
        switch (ext)
        {
            case ".epub":
            case ".pdf":
            case ".mobi":
            case ".azw":
            case ".azw3":
            case ".djvu":
            {
                var probe = BookProbeService.Probe(stagedPath);
                if (!probe.Ok)
                {
                    return Task.FromResult<string?>("Converted output failed integrity probe: " + probe.Reason);
                }

                break;
            }

            case ".cbz":
            case ".cbr":
            case ".cb7":
            {
                var probe = ComicProbeService.Probe(stagedPath);
                if (!probe.Ok)
                {
                    return Task.FromResult<string?>("Converted output failed integrity probe: " + probe.Reason);
                }

                break;
            }

            // ponytail: image/music/video converters are Phase 4+; add ffprobe check when those land.
            default:
                break;
        }

        return Task.FromResult<string?>(null);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record Details(string ConverterKey, string TargetExt, bool MixedLibrary, bool ApprovedInMixedLibrary, string LibraryKind = "");
}
