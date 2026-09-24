using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Typed view of the <c>DetailsJson</c> payload written by
/// <see cref="Jellyfin.Plugin.MediaDash.Scanners.SubtitleLanguageScanner"/>. This chunk only
/// exposes the <c>externalFiles</c> array (the destructive sidecar-recycle list used by
/// <see cref="Jellyfin.Plugin.MediaDash.Fixers.TrackFixer"/>). The other persisted fields
/// (<c>removeIndexes</c>, <c>languages</c>, <c>warnings</c>) stay message-only for now —
/// callers that consume them can be routed later without a JSON rewrite. The persisted
/// key <c>externalFiles</c> stays verbatim.
/// </summary>
/// <param name="ExternalFiles">The sidecar paths flagged by the scanner. Every downstream
/// action (recycle/delete) is still guarded by <c>LibraryGuard</c> + a self-referential
/// video path check — parsing success does NOT authorise a file operation.</param>
internal sealed record SubtitleCleanupDetails(IReadOnlyList<string> ExternalFiles);
