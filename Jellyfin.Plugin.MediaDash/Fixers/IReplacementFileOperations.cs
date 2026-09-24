using System;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Narrow file-operation seam <see cref="VerifiedReplacementService"/> pushes through so
/// tests can inject faults at specific commit steps (stage move, permanent-delete disposal,
/// final rename). Every operation is either a presence probe, a metadata read, or an atomic
/// on-disk mutation the service actually issues — the surface is deliberately minimal so a
/// test wrapper can decide "throw on the Nth Move call" without touching real filesystem
/// primitives the service doesn't use.
/// </summary>
/// <remarks>
/// RecycleBin disposal is out of scope: it has its own tests + cross-volume handling. The
/// service takes a <see cref="RecycleBin"/> instance directly and routes there when
/// <see cref="ReplacementRequest.RecycleOriginal"/> is true.
/// </remarks>
internal interface IReplacementFileOperations
{
    /// <summary>Returns true if the path exists and is a file.</summary>
    /// <param name="path">Absolute path to probe.</param>
    /// <returns>True when the path names an existing file.</returns>
    bool FileExists(string path);

    /// <summary>Returns true if the path exists and is a directory. Directory-as-file
    /// collision at the destination is a distinct refusal from file-at-destination.</summary>
    /// <param name="path">Absolute path to probe.</param>
    /// <returns>True when the path names an existing directory.</returns>
    bool DirectoryExists(string path);

    /// <summary>Length in bytes of a file. Used for source-identity recheck + post-promote
    /// sanity that the staged output matches the caller's output.</summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns>File length in bytes.</returns>
    long GetLength(string path);

    /// <summary>Last write time in UTC ticks. Used for source-identity recheck.</summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns>Last-write-time-UTC ticks.</returns>
    long GetLastWriteTicksUtc(string path);

    /// <summary>Atomic same-volume rename (or copy+delete when the framework silently spans
    /// volumes). Fails without overwrite when the destination is preoccupied — the service
    /// pre-checks and expects this to succeed at call time.</summary>
    /// <param name="source">Existing file to move.</param>
    /// <param name="destination">Target path (must not exist).</param>
    void Move(string source, string destination);

    /// <summary>Permanent delete of a file. Used for the permanent-delete disposal path
    /// (<see cref="ReplacementRequest.RecycleOriginal"/> = false) and for owned-staging
    /// cleanup on failure.</summary>
    /// <param name="path">Absolute file path.</param>
    void Delete(string path);

    /// <summary>Restore Creation/LastWrite timestamps on a committed file.
    /// Non-throwing for tests: real implementation swallows platform-specific
    /// exceptions (Linux <c>utimensat(2)</c> returns EPERM as
    /// <see cref="System.UnauthorizedAccessException"/>; some network shares refuse) so a
    /// stamp restore that fails does not fail the whole commit.</summary>
    /// <param name="path">Absolute file path (the just-committed destination).</param>
    /// <param name="creationTimeUtc">Creation time to restore (UTC).</param>
    /// <param name="lastWriteTimeUtc">Last-write time to restore (UTC).</param>
    void TrySetTimestampsUtc(string path, DateTime creationTimeUtc, DateTime lastWriteTimeUtc);
}
