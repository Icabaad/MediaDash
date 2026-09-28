using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Emits FormatCompatibility issues for files whose extension Jellyfin's browser client can't
/// render (or that Jellyfin's server doesn't even index). See design spec §3 for the data flow.
/// Phase 1 is scan-only; the fixer lands in Phase 2+.
/// </summary>
public sealed class FormatCompatibilityScanner : IScanner
{
    private readonly IFormatConverterRegistry _registry;
    private readonly ILibraryCollectionResolver _resolver;
    private readonly ILogger<FormatCompatibilityScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatCompatibilityScanner"/> class.</summary>
    /// <param name="registry">Converter registry.</param>
    /// <param name="resolver">Library-collection-type resolver.</param>
    /// <param name="logger">Logger.</param>
    public FormatCompatibilityScanner(
        IFormatConverterRegistry registry,
        ILibraryCollectionResolver resolver,
        ILogger<FormatCompatibilityScanner> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.FormatCompatibility;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(
        IReadOnlyList<BaseItem> items,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        var now = DateTime.UtcNow;
        var total = items.Count;
        var index = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            if (total > 0)
            {
                progress.Report(100.0 * index / total);
            }

            var path = item.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var row = FormatMatrix.Classify(path);
            if (row is null || row.ClientRenders || row.TargetExtension is null)
            {
                continue;
            }

            var converter = _registry.Get(row.ConverterKey);
            if (converter is null || !converter.CanConvert(row.Extension))
            {
                continue;
            }

            var libraryType = _resolver.Resolve(item);
            var isMixed = libraryType is null;
            if (!isMixed && !converter.AllowedLibraryTypes.Contains(libraryType))
            {
                continue;
            }

            var details = new
            {
                sourceExt = row.Extension,
                targetExt = row.TargetExtension,
                converter = row.ConverterKey,
                libraryKind = libraryType?.ToString() ?? "unknown",
                mixedLibrary = isMixed,
                warnings = isMixed
                    ? new[] { new { blocking = true, code = "mixed_library", message = "Mixed-content library — approve per file." } }
                    : Array.Empty<object>()
            };

            issues.Add(new Issue
            {
                Type = IssueType.FormatCompatibility,
                ItemId = item.Id,
                Path = path,
                DetailsJson = JsonSerializer.Serialize(details),
                SuggestedFix = row.UserAdvice,
                SizeSavings = 0,
                Status = IssueStatus.Detected,
                DetectedAtUtc = now,
            });
        }

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
