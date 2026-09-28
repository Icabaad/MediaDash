using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
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
        var item = _getItemById(issue.ItemId);
        var libraryType = item is null ? (CollectionType?)null : _resolver.Resolve(item);
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

        // 11. Dry-run — return preview without touching files.
        if (config.DryRun)
        {
            return FixResult.DryRun("convert " + Path.GetFileName(issue.Path) + " to " + details.TargetExt, 0);
        }

        // 12. Conversion.
        var stagedPath = issue.Path + InflightTail + details.TargetExt;
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
        var sourceInfo = new FileInfo(issue.Path);
        var destinationPath = Path.ChangeExtension(issue.Path, details.TargetExt);
        var request = new ReplacementRequest(
            SourcePath: issue.Path,
            OutputPath: conversion.StagedPath,
            DestinationPath: destinationPath,
            ExpectedSourceLength: sourceInfo.Length,
            ExpectedSourceMtimeUtcTicks: sourceInfo.LastWriteTimeUtc.Ticks,
            RecycleOriginal: config.FormatCompatibilityDisposal == DisposalMethod.RecycleBin);

        var vrs = _replacement ?? throw new InvalidOperationException("FormatCompatibilityFixer: VerifiedReplacementService is required for the live path but was not injected.");

        var stagedForVerify = conversion.StagedPath;
        var replaceResult = await vrs.ReplaceAsync(
            request,
            ct => VerifyOutputAsync(stagedForVerify, ct),
            () => PolicyDecision.Allow,
            cancellationToken).ConfigureAwait(false);

        if (!replaceResult.Applied)
        {
            SafeDelete(stagedPath);
            return FixResult.Fail(replaceResult.Reason, replaceResult.TechnicalDetail ?? "Replacement failed.");
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
            return new Details(converterKey, targetExt, mixed, approvedInMixed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<string?> VerifyOutputAsync(string stagedPath, CancellationToken ct)
    {
        // Lightweight post-conversion check: file must exist and be non-empty.
        // ponytail: deeper format-integrity probe (EPUB structure, CBZ validity) is Phase 2.5.
        if (!File.Exists(stagedPath))
        {
            return Task.FromResult<string?>("Converted output is missing after conversion.");
        }

        if (new FileInfo(stagedPath).Length == 0)
        {
            return Task.FromResult<string?>("Converted output is empty.");
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

    private sealed record Details(string ConverterKey, string TargetExt, bool MixedLibrary, bool ApprovedInMixedLibrary);
}
