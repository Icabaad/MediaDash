using System;
using System.Diagnostics;
using System.IO;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FileBrowserCrossDeviceMoveTests
{
    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void CrossDeviceMove_LinkInsideHiddenOrSystemFolder_RefusesBeforeCopy(FileAttributes attributes)
    {
        var root = NewSandbox();
        var source = Path.Combine(root, "src");
        var destination = Path.Combine(root, "dst");
        var parent = Path.Combine(source, "hidden");
        var link = Path.Combine(parent, "link");
        var target = Path.Combine(root, "link-target");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "source");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "target");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
                File.SetAttributes(parent, File.GetAttributes(parent) | attributes);
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }

            Assert.Throws<IOException>(() => FileBrowserController.CrossDeviceMove(source, destination, true));
            Assert.True(Directory.Exists(link));
            Assert.Equal("source", File.ReadAllText(Path.Combine(source, "keep.txt")));
            Assert.Equal("target", File.ReadAllText(Path.Combine(target, "keep.txt")));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(parent)) File.SetAttributes(parent, FileAttributes.Directory);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewSandbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-xdev-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CrossDeviceMove_File_MovesContentAndDeletesSource()
    {
        var root = NewSandbox();
        try
        {
            var source = Path.Combine(root, "src.bin");
            var target = Path.Combine(root, "dst.bin");
            File.WriteAllBytes(source, [1, 2, 3, 4, 5]);
            var expectedMtime = File.GetLastWriteTimeUtc(source);

            FileBrowserController.CrossDeviceMove(source, target, sourceIsDir: false);

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(target));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(target));
            Assert.Equal(expectedMtime, File.GetLastWriteTimeUtc(target));
            // Staging file must not linger.
            Assert.False(File.Exists(target + ".mediadash-moving"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CrossDeviceMove_Directory_MovesTreeAndDeletesSource()
    {
        var root = NewSandbox();
        try
        {
            var source = Path.Combine(root, "src");
            var target = Path.Combine(root, "dst");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "a.txt"), "one");
            File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "two");

            FileBrowserController.CrossDeviceMove(source, target, sourceIsDir: true);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(target));
            Assert.Equal("one", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
            Assert.False(Directory.Exists(target + ".mediadash-moving"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CrossDeviceMove_TargetAlreadyExists_LeavesSourceIntactAndCleansStaging()
    {
        var root = NewSandbox();
        try
        {
            var source = Path.Combine(root, "src.bin");
            var target = Path.Combine(root, "dst.bin");
            File.WriteAllBytes(source, [9, 9, 9]);
            File.WriteAllText(target, "already here");

            Assert.ThrowsAny<IOException>(() => FileBrowserController.CrossDeviceMove(source, target, sourceIsDir: false));

            // Invariant: on any failure the caller's source must survive; the staging file gets swept.
            Assert.True(File.Exists(source));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(source));
            Assert.Equal("already here", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".mediadash-moving"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
