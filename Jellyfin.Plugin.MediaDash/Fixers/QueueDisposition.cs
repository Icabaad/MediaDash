namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// What the scheduler should do with an issue's queue row given a fix outcome.
/// See <see cref="FixResultPolicy.GetQueueDisposition"/> for the mapping rule.
/// </summary>
internal enum QueueDisposition
{
    /// <summary>Leave the issue in the queue. Next fix run will retry it — the caller's failure is expected to be transient (permission, disk full, provider quota, verification, generic I/O).</summary>
    KeepQueued,

    /// <summary>
    /// Mark the issue Fixed (via <c>MarkFixed</c>) even though nothing was repaired. This mirrors the
    /// legacy handling driven by <c>FixTask.IsStaleFailure</c>: some refusals and stale-state failures
    /// would retry forever if left queued, so we advance them to the terminal Fixed status to stop the
    /// 30-minute retry loop. Fresh scans still re-emit the issue if the state ever changes back.
    /// This is NOT a claim that the media was actually repaired — it is a queue-management decision.
    /// </summary>
    CompleteLegacy
}
