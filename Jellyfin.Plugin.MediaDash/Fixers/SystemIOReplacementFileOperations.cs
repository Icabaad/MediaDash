using System;
using System.IO;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Default <see cref="IReplacementFileOperations"/> — thin wrapper over <see cref="System.IO"/>.
/// Production uses this. Tests substitute a wrapper that decorates this implementation with
/// fault injection.
/// </summary>
internal sealed class SystemIOReplacementFileOperations : IReplacementFileOperations
{
    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc/>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc/>
    public long GetLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc/>
    public long GetLastWriteTicksUtc(string path) => new FileInfo(path).LastWriteTimeUtc.Ticks;

    /// <inheritdoc/>
    public void Move(string source, string destination) => File.Move(source, destination);

    /// <inheritdoc/>
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc/>
    public void TrySetTimestampsUtc(string path, DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
    {
        try
        {
            File.SetCreationTimeUtc(path, creationTimeUtc);
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        }
        catch (IOException)
        {
            // Non-fatal — the commit already succeeded. Log-only in the caller.
        }
        catch (UnauthorizedAccessException)
        {
            // Linux `utimensat(2)` returns EPERM as UnauthorizedAccessException, not IOException
            // (GitHub #59). Network shares / restricted-perms mounts hit this. Non-fatal.
        }
    }
}
