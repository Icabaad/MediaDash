namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Outcome of a guarded <see cref="MediaDashDb.TryTransitionIssueStatus"/> call. Lets callers
/// (controller endpoints, fixer completion path) distinguish "no such issue" from "row exists but
/// its current status doesn't allow this transition" so 404 vs 409 vs 204 stays honest.
/// </summary>
public enum StatusTransitionResult
{
    /// <summary>Row moved from an allowed source status to the target status.</summary>
    Applied = 0,

    /// <summary>No issue exists with the given id.</summary>
    NotFound = 1,

    /// <summary>
    /// The issue exists but its current status isn't in the caller's allowed source set — a stale
    /// Approve of a Fixed row, a Revert after the fixer completed mid-request, etc. Callers should
    /// prompt the UI to refresh rather than retry the same action blindly.
    /// </summary>
    Conflict = 2
}
