using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class LibraryGuardTests
{
    private static string P(params string[] parts) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.Combine(parts)));

    [Fact]
    public void FileInsideLibraryIsAllowed()
    {
        Assert.True(LibraryGuard.IsUnder(P("movies", "film", "film.mkv"), P("movies")));
    }

    [Fact]
    public void SiblingFolderWithSamePrefixIsRejected()
    {
        // "…\movies2\x" must not match library root "…\movies".
        Assert.False(LibraryGuard.IsUnder(P("movies2", "film.mkv"), P("movies")));
    }

    [Fact]
    public void CompletelyOutsidePathIsRejected()
    {
        Assert.False(LibraryGuard.IsUnder(P("other", "film.mkv"), P("movies")));
    }

    [Fact]
    public void TrailingSeparatorOnRootIsHandled()
    {
        Assert.True(LibraryGuard.IsUnder(P("movies", "film.mkv"), P("movies") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void WindowsDriveRootMatchesAnySubpath()
    {
        // 2026-08-23 regression: Path.TrimEndingDirectorySeparator("C:\\") preserves the trailing
        // slash on .NET 5+ (drive roots aren't stripped because "C:" alone isn't a valid rooted
        // path). The old boundary check then read fullPath[fullRoot.Length] and expected a
        // separator, but that position was already PAST the separator. SMART/library-drive
        // detection on Windows silently returned false for every subpath of C:\\.
        if (!System.OperatingSystem.IsWindows())
        {
            return; // Path semantics differ on Linux/macOS; the drive-root case is Windows-specific.
        }

        Assert.True(LibraryGuard.IsUnder(@"C:\Users\me\file.txt", @"C:\"));
        Assert.True(LibraryGuard.IsUnder(@"C:\", @"C:\"));
        Assert.False(LibraryGuard.IsUnder(@"D:\Users\me\file.txt", @"C:\"));
    }

    [Fact]
    public void PathTraversalAttemptIsRejected()
    {
        // File browser: user posts a path with ".." to escape the library. GetFullPath must be called first
        // so that '../etc/passwd' relative to a library folder resolves to somewhere outside the root before check.
        var libraryRoot = P("movies");
        var attackerPath = Path.GetFullPath(Path.Combine(libraryRoot, "..", "..", "etc", "passwd"));
        Assert.False(LibraryGuard.IsUnder(attackerPath, libraryRoot));
    }

    [Fact]
    public void IsInEnabledLocations_KeepsPathInsideEnabledRoot()
    {
        // Regression from 2026-09-19 QA sweep: the fix loop treated the queue as global scope and
        // ran approved fixes against files whose library had been un-ticked in Settings → Libraries.
        // This helper is the predicate FixTask now uses to scope its queue.
        var enabled = new[] { P("sweep-generated") + Path.DirectorySeparatorChar };
        Assert.True(LibraryGuard.IsInEnabledLocations(P("sweep-generated", "Healthy Control (2026).mkv"), enabled));
    }

    [Fact]
    public void IsInEnabledLocations_RejectsPathInDisabledRoot()
    {
        // The exact bug from the QA sweep: 'Sweep Disabled' library is not ticked, but a queued
        // installer.exe under it was being fixed anyway. The predicate must return false so
        // FixTask drops it before handing to the fixer.
        var enabled = new[] { P("sweep-generated") + Path.DirectorySeparatorChar };
        Assert.False(LibraryGuard.IsInEnabledLocations(P("sweep-disabled", "installer.exe"), enabled));
    }

    [Fact]
    public void IsInEnabledLocations_EmptyPathIsPassedThrough()
    {
        // Path-less issues get surfaced elsewhere (they typically fail File.Exists inside the
        // fixer); the scope filter must not silently swallow them by returning false.
        var enabled = new[] { P("sweep-generated") + Path.DirectorySeparatorChar };
        Assert.True(LibraryGuard.IsInEnabledLocations(string.Empty, enabled));
        Assert.True(LibraryGuard.IsInEnabledLocations(null, enabled));
    }
}
