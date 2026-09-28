using System.IO;
using System.Reflection;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// Base class for format-compatibility tests. Owns the fixtures-directory resolver.
/// Fixtures land at `<test-output>/fixtures/formats/<kind>/sample<ext>` via the .csproj
/// glob copy set up in Task 9.
/// </summary>
public abstract class FormatFixtureBase
{
    /// <summary>Gets the absolute path to the fixtures root next to the test assembly.</summary>
    protected static string FixturesRoot { get; } = ResolveFixturesRoot();

    /// <summary>Returns the absolute path to `sample&lt;ext&gt;` for the given kind (lowercase, e.g. "ebook").</summary>
    /// <param name="kind">Kind subdirectory name (e.g. "ebook", "comic", "image").</param>
    /// <param name="extensionWithDot">Extension including the leading dot (e.g. ".mobi").</param>
    /// <returns>The absolute fixture path — may or may not exist on disk.</returns>
    protected static string FixturePath(string kind, string extensionWithDot)
        => Path.Combine(FixturesRoot, kind, "sample" + extensionWithDot);

    private static string ResolveFixturesRoot()
    {
        var asm = Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(asm) ?? Directory.GetCurrentDirectory();
        return Path.Combine(dir, "fixtures", "formats");
    }
}
