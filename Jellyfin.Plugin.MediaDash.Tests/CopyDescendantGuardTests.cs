using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Review P1-3 regression: copying/moving a directory into itself or a descendant must be
/// rejected before any filesystem operation. Without this guard, CopyDirectory recurses through
/// the target it just created, building an infinite chain until the disk or path length fails.
/// </summary>
public sealed class CopyDescendantGuardTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediadash-copyguard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RejectsExactSelfCopy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        try
        {
            Assert.True(FileBrowserController.IsSelfOrDescendant(
                Path.Combine(_root, "A"),
                Path.Combine(_root, "A")));
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RejectsDirectDescendant()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        try
        {
            Assert.True(FileBrowserController.IsSelfOrDescendant(
                Path.Combine(_root, "A", "copy"),
                Path.Combine(_root, "A")));
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RejectsDeepDescendant()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        try
        {
            Assert.True(FileBrowserController.IsSelfOrDescendant(
                Path.Combine(_root, "A", "subdir", "deeper", "copy"),
                Path.Combine(_root, "A")));
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AllowsSiblingCopy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        Directory.CreateDirectory(Path.Combine(_root, "B"));
        try
        {
            Assert.False(FileBrowserController.IsSelfOrDescendant(
                Path.Combine(_root, "B"),
                Path.Combine(_root, "A")));
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AllowsSiblingPrefixName()
    {
        // "/A" and "/Alpha" share a leading substring but Alpha is NOT under A — the guard must
        // use a real path boundary check, not a naive StartsWith. This is exactly the sort of
        // bug the reviewer called out ("sibling-prefix paths").
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        try
        {
            Assert.False(FileBrowserController.IsSelfOrDescendant(
                Path.Combine(_root, "Alpha"),
                Path.Combine(_root, "A")));
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
