namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Typed classification of what a fix attempt actually did. Distinguishes
/// successful mutation from successful no-op, refusal, deferral and failure so
/// scheduling decisions do not depend on friendly-language wording of
/// <see cref="FixResult.Message"/>. See <see cref="FixReason"/> for the paired
/// cause code and <see cref="FixResultPolicy"/> for the queue disposition rule.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> is the default zero value used while producers are
/// migrated in 01-B. It MUST NOT be interpreted by policy as a successful outcome:
/// see <see cref="FixResultPolicy.GetQueueDisposition"/>. Once migration completes,
/// factories should reject an <see cref="Unspecified"/> outcome at construction.
/// </remarks>
internal enum FixOutcome
{
    /// <summary>Producer has not yet been migrated to typed outcomes (01-B default).</summary>
    Unspecified = 0,

    /// <summary>A real mutation succeeded. Implies <see cref="FixResult.Success"/> = true and <see cref="FixResult.WasDryRun"/> = false.</summary>
    Applied,

    /// <summary>A dry-run inspection succeeded — nothing was written. Implies Success = true and WasDryRun = true.</summary>
    Preview,

    /// <summary>The fixer decided nothing needed doing (already healthy, nothing to remove, cover already present).</summary>
    NoChange,

    /// <summary>The fixer intentionally deferred (external quota exhausted, transient host contention that we choose to retry).</summary>
    Deferred,

    /// <summary>The fixer refused because a hard precondition failed (outside library, DRM, collision, unsupported format, invalid request).</summary>
    Refused,

    /// <summary>The fixer or an enclosing catch reports a real failure the scheduler must remember.</summary>
    Failed
}
