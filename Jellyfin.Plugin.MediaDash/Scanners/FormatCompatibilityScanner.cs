using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Emits FormatCompatibility issues for files whose extension Jellyfin's browser client can't
/// render (or that Jellyfin's server doesn't even index). See design spec §3 for the data flow.
/// Phase 1 is scan-only; the fixer lands in Phase 2+.
/// </summary>
public sealed class FormatCompatibilityScanner : IScanner
{
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly IFormatConverterRegistry _registry;
    private readonly ILibraryCollectionResolver _resolver;
    private readonly Func<IEnumerable<VirtualFolderInfo>> _getFolders;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILogger<FormatCompatibilityScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatCompatibilityScanner"/> class.</summary>
    /// <param name="registry">Converter registry.</param>
    /// <param name="resolver">Library-collection-type resolver.</param>
    /// <param name="libraryManager">Jellyfin library manager (for filesystem sweep of unindexed formats).</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="logger">Logger.</param>
    public FormatCompatibilityScanner(
        IFormatConverterRegistry registry,
        ILibraryCollectionResolver resolver,
        ILibraryManager libraryManager,
        IPluginConfigurationProvider configProvider,
        ILogger<FormatCompatibilityScanner> logger)
        : this(registry, resolver, libraryManager.GetVirtualFolders, configProvider, logger)
    {
    }

    // ponytail: internal seam so tests inject folder data without stubbing all of ILibraryManager.
    internal FormatCompatibilityScanner(
        IFormatConverterRegistry registry,
        ILibraryCollectionResolver resolver,
        Func<IEnumerable<VirtualFolderInfo>> getFolders,
        IPluginConfigurationProvider configProvider,
        ILogger<FormatCompatibilityScanner> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _getFolders = getFolders;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.FormatCompatibility;

    /// <inheritdoc />
    // Unindexed-file issues use filesystem paths that never match ScanTask's media-only
    // scannedPaths list — same reason as SuspiciousFileScanner. The full scan IS the
    // authoritative snapshot, so stale rows from prior runs are replaced in bulk.
    public bool AlwaysUnscoped => true;

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

        ScanUnindexedFiles(issues, now, cancellationToken);
        ScanAudiobookFolders(issues, now, cancellationToken);

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    private void ScanUnindexedFiles(List<Issue> issues, DateTime now, CancellationToken cancellationToken)
    {
        // Filesystem sweep for extensions the matrix marks ServerIndexes=false. Jellyfin never
        // creates BaseItems for these, so the BaseItem loop above misses them entirely.
        var unindexedRows = FormatMatrix.All
            .Where(r => !r.ServerIndexes && r.TargetExtension is not null)
            .ToList();
        if (unindexedRows.Count == 0)
        {
            return;
        }

        var unindexedExts = new HashSet<string>(unindexedRows.Select(r => r.Extension), StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _getFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryType = ToCollectionType(folder.CollectionType);
            var isMixed = libraryType is null;

            foreach (var location in folder.Locations ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(location) || !Directory.Exists(location))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(location, "*", WalkOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (string.IsNullOrEmpty(ext) || !unindexedExts.Contains(ext))
                    {
                        continue;
                    }

                    var row = FormatMatrix.Classify(file);
                    if (row is null || row.ClientRenders || row.TargetExtension is null)
                    {
                        continue;
                    }

                    var converter = _registry.Get(row.ConverterKey);
                    if (converter is null || !converter.CanConvert(row.Extension))
                    {
                        continue;
                    }

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
                        unindexed = true,
                        warnings = isMixed
                            ? new[] { new { blocking = true, code = "mixed_library", message = "Mixed-content library — approve per file." } }
                            : Array.Empty<object>()
                    };

                    issues.Add(new Issue
                    {
                        Type = IssueType.FormatCompatibility,
                        // Unindexed files have no Jellyfin item; Guid.Empty signals this to the fixer.
                        ItemId = Guid.Empty,
                        Path = file,
                        DetailsJson = JsonSerializer.Serialize(details),
                        SuggestedFix = row.UserAdvice,
                        SizeSavings = 0,
                        Status = IssueStatus.Detected,
                        DetectedAtUtc = now
                    });
                }
            }
        }
    }

    private void ScanAudiobookFolders(List<Issue> issues, DateTime now, CancellationToken cancellationToken)
    {
        var cfg = _configProvider.GetSnapshot();
        if (!cfg.AudiobookMergeMultiFile)
        {
            return;
        }

        var converter = _registry.Get("audiobook");
        if (converter is null)
        {
            return;
        }

        foreach (var folder in _getFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryType = ToCollectionType(folder.CollectionType);
            // Only Books libraries. Mixed-library merge doesn't make sense; user should move first.
            if (libraryType != CollectionType.books)
            {
                continue;
            }

            foreach (var location in folder.Locations ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(location) || !Directory.Exists(location))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(location, "*", WalkOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Quick rejection: already has an m4b — merge target exists.
                    if (Directory.EnumerateFiles(dir, "*.m4b", SearchOption.TopDirectoryOnly).Any())
                    {
                        continue;
                    }

                    var candidate = ClassifyFolderAsAudiobookMerge(dir);
                    if (candidate is null)
                    {
                        continue;
                    }

                    var details = new
                    {
                        kind = "audiobook-folder-merge",
                        files = candidate.Files,
                        converter = "audiobook",
                        targetExt = ".m4b",
                        libraryKind = "books",
                        mixedLibrary = false
                    };

                    issues.Add(new Issue
                    {
                        Type = IssueType.FormatCompatibility,
                        ItemId = Guid.Empty,
                        Path = dir,
                        DetailsJson = JsonSerializer.Serialize(details),
                        SuggestedFix = "Merge " + candidate.Files.Count + " chapter files into a single M4B for better Jellyfin browsing.",
                        SizeSavings = 0,
                        Status = IssueStatus.Detected,
                        DetectedAtUtc = now
                    });
                }
            }
        }
    }

    private static AudiobookMergeCandidate? ClassifyFolderAsAudiobookMerge(string dir)
    {
        var mp3s = Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly);
        var m4as = Directory.GetFiles(dir, "*.m4a", SearchOption.TopDirectoryOnly);

        // Prefer whichever is majority; refuse mixed extensions.
        string[] files;
        string ext;
        if (mp3s.Length >= 3 && m4as.Length == 0)
        {
            files = mp3s;
            ext = ".mp3";
        }
        else if (m4as.Length >= 3 && mp3s.Length == 0)
        {
            files = m4as;
            ext = ".m4a";
        }
        else
        {
            return null;
        }

        var numbered = new List<(int Num, string Path)>();
        foreach (var f in files)
        {
            if (TryReadTrackNumber(Path.GetFileNameWithoutExtension(f), out var n))
            {
                numbered.Add((n, f));
            }
            else
            {
                return null; // one un-numbered file → whole folder disqualified
            }
        }

        // Strictly ascending, no duplicates.
        numbered.Sort((a, b) => a.Num.CompareTo(b.Num));
        for (int i = 1; i < numbered.Count; i++)
        {
            if (numbered[i].Num == numbered[i - 1].Num)
            {
                return null; // duplicate number
            }
        }

        return new AudiobookMergeCandidate(
            numbered.Select(x => x.Path).ToList(),
            ext);
    }

    /// <summary>
    /// Extracts the last digit run from a filename stem (e.g. "01 - Chapter" → 1, "Chapter 03" → 3).
    /// Returns false when no digit run is found.
    /// </summary>
    /// <param name="filenameStem">Filename without extension.</param>
    /// <param name="number">The extracted track number, or 0 on failure.</param>
    /// <returns><see langword="true"/> if a digit run was found; otherwise <see langword="false"/>.</returns>
    internal static bool TryReadTrackNumber(string filenameStem, out int number)
    {
        number = 0;
        int i = filenameStem.Length - 1;
        // Skip trailing non-digits.
        while (i >= 0 && !char.IsDigit(filenameStem[i]))
        {
            i--;
        }

        if (i < 0)
        {
            return false;
        }

        int end = i;
        while (i >= 0 && char.IsDigit(filenameStem[i]))
        {
            i--;
        }

        int start = i + 1;
        return int.TryParse(filenameStem.AsSpan(start, end - start + 1), out number);
    }

    // VirtualFolderInfo uses CollectionTypeOptions (MediaBrowser.Model), while the scanner
    // contract uses CollectionType (Jellyfin.Data.Enums). Same lowercase names, different types.
    // CollectionTypeOptions.mixed means no specific type → return null.
    private static CollectionType? ToCollectionType(CollectionTypeOptions? raw)
    {
        if (raw is null || raw == CollectionTypeOptions.mixed)
        {
            return null;
        }

        return Enum.TryParse<CollectionType>(raw.ToString(), ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private sealed record AudiobookMergeCandidate(IReadOnlyList<string> Files, string Extension);
}
