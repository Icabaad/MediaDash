using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// One runnable check for the roadmap i-110-protected-media decision logic. Covers the branches
/// that would silently allow a fixer to touch a protected file if they regress: path prefix,
/// case-insensitive match, mask-parsing ('*' vs comma list vs empty), non-match paths.
/// </summary>
public sealed class ProtectionServiceTests : IDisposable
{
    private static string P(string path) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "protection-tests-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly ProtectionService _service;

    public ProtectionServiceTests()
    {
        _db = new MediaDashDb(_dbPath);
        _service = new ProtectionService(_db.Protection, libraryManager: null);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void EmptyStore_BlocksNothing()
    {
        Assert.False(_service.IsScannerBlocked(P("media/movies/x.mkv"), IssueType.Quality));
        Assert.False(_service.IsFixerBlocked(P("media/movies/x.mkv"), IssueType.Quality));
    }

    [Fact]
    public void ScanInput_ExcludesProtectedPrimaryAndAlternateMediaForItsMaskOnly()
    {
        var protectedPath = Path.Combine(Path.GetTempPath(), "protected.mkv");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = protectedPath, ScannersMask = "Quality" });
        var safe = new Video { Path = Path.Combine(Path.GetTempPath(), "safe.mkv") };
        BaseItem[] items = { safe, new Video { Path = protectedPath }, new Video { Path = safe.Path, LocalAlternateVersions = new[] { protectedPath } } };
        Assert.Same(safe, Assert.Single(_service.FilterScannable(items, IssueType.Quality)));
        Assert.Equal(3, _service.FilterScannable(items, IssueType.AudioLanguage).Count);
    }

    [Fact]
    public void EmptyFolderScan_DoesNotReadOrProposeAProtectedTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "protected-scan-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "empty");
        Directory.CreateDirectory(child);
        try
        {
            var issues = new List<Issue>();
            OrphanCleanupScanner.DetectEmptyFolders(new[] { root }, issues, CancellationToken.None);
            Assert.NotEmpty(issues);
            issues.Clear();
            _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = child, ScannersMask = "*" });
            _service.Invalidate();
            OrphanCleanupScanner.DetectEmptyFolders(new[] { root }, issues, CancellationToken.None, _service);
            Assert.Empty(issues);
            Assert.True(_service.IsAnyDescendantProtected(root, IssueType.OrphanedDebris, scanners: true));
            Assert.False(_service.IsAnyDescendantProtected(root, IssueType.OrphanedDebris));
            var other = Path.Combine(root, "unprotected");
            Directory.CreateDirectory(other);
            OrphanCleanupScanner.DetectEmptyFolders(new[] { root }, issues, CancellationToken.None, _service);
            Assert.Equal(other, Assert.Single(issues).Path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FolderRule_StarMask_BlocksEveryTypeUnderneath()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), ScannersMask = "*", FixersMask = "*" });
        _service.Invalidate();

        Assert.True(_service.IsScannerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
        Assert.True(_service.IsScannerBlocked(P("media/movies/sub/Alien.mkv"), IssueType.Duplicate));
        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Playability));
        Assert.False(_service.IsScannerBlocked(P("media/tv/Alien.mkv"), IssueType.Quality)); // different tree
    }

    [Fact]
    public void FolderRule_CommaMask_BlocksOnlyListedTypes()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), FixersMask = "Quality,Duplicate" });
        _service.Invalidate();

        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Duplicate));
        Assert.False(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Playability));
        // Scanner mask is empty for this rule, so scanning is never blocked.
        Assert.False(_service.IsScannerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void FolderRule_PathComparisonUsesFilesystemCaseRules()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("Media/Movies"), FixersMask = "*" });
        _service.Invalidate();

        Assert.Equal(OperatingSystem.IsWindows(), _service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
        Assert.Equal(OperatingSystem.IsWindows(), _service.IsFixerBlocked(P("MEDIA/MOVIES/Alien.mkv"), IssueType.Quality));
        Assert.True(_service.IsFixerBlocked(P("Media/Movies/Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void FolderRule_ExactPathBlockedButAdjacentPrefixIsNot()
    {
        // If the rule targets `movies`, `movies-archive` must NOT match (guard against naive StartsWith).
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), FixersMask = "*" });
        _service.Invalidate();

        Assert.True(_service.IsFixerBlocked(P("media/movies"), IssueType.Quality));
        Assert.False(_service.IsFixerBlocked(P("media/movies-archive/Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void RemovingRule_UnblocksImmediatelyAfterInvalidate()
    {
        var id = _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), FixersMask = "*" });
        _service.Invalidate();
        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));

        _db.Protection.Remove(id);
        _service.Invalidate();
        Assert.False(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void FolderRule_ForwardSlashRuleStillMatchesBackslashPath()
    {
        // External audit finding P1-F1: pre-fix a rule stored with forward slashes never
        // matched a path emitted with backslashes because PathIsUnder only handled alt-separator
        // on the *path* side. Post-fix both sides canonicalize through Path.GetFullPath so the
        // separator style is irrelevant.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies").Replace('\\', '/'), FixersMask = "*" });
        _service.Invalidate();

        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv").Replace('\\', '/'), IssueType.Quality));
    }

    [Fact]
    public void FolderRule_DotSegmentRuleStillMatchesCanonicalPath()
    {
        // External audit finding P1-F1: `\.\` in the stored target never matched because
        // Path.TrimEndingDirectorySeparator preserved dot segments. Post-fix Path.GetFullPath
        // resolves them at compare time.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/./movies"), FixersMask = "*" });
        _service.Invalidate();

        Assert.True(_service.IsFixerBlocked(P("media/movies/Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void FolderRule_ParentDotDotSegmentInPathStillMatches()
    {
        // A path emitted with a `..` segment that resolves inside the protected folder must be
        // treated as inside — pre-fix the prefix match failed because `..` wasn't resolved.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), FixersMask = "*" });
        _service.Invalidate();

        Assert.True(_service.IsFixerBlocked(P("media/movies/sub/../Alien.mkv"), IssueType.Quality));
    }

    [Fact]
    public void FilterEmittable_DropsProtectedRowsAndKeepsTheRest()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), ScannersMask = "Quality" });
        _service.Invalidate();

        var input = new[]
        {
            new Issue { Type = IssueType.Quality, Path = P("media/movies/Alien.mkv"), DetectedAtUtc = DateTime.UtcNow },
            new Issue { Type = IssueType.Quality, Path = P("media/tv/Alien.mkv"), DetectedAtUtc = DateTime.UtcNow },
        };

        var survivors = _service.FilterEmittable(input, IssueType.Quality);

        Assert.Single(survivors);
        Assert.Equal(P("media/tv/Alien.mkv"), survivors[0].Path);
    }
}
