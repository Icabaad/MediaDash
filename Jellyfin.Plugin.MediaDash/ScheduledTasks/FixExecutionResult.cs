using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Executor-produced summary of one completed fix operation. Feeds the recorder so history and
/// status writes stay atomic and reflect actual effects — not the plan's intent.
/// </summary>
/// <param name="Result">The fixer's typed <see cref="FixResult"/> (Applied / Preview / Failed / Refused / NoChange / Deferred).</param>
/// <param name="PrimaryIssueId">The plan's primary issue id, echoed here so the recorder can cross-check invariants without recomputing.</param>
/// <param name="ResolvedIssueIds">
/// The issue ids the executor considers "resolved by this operation". Populated with the primary id
/// (and any surviving companions) on a real-run success; empty on dry-run success; primary only for
/// <see cref="QueueDisposition.CompleteLegacy"/> failures. A revoked/dismissed companion is never
/// listed here even if the plan named it. The recorder writes success history rows and issues
/// <c>Queued → Fixed</c> transitions strictly against this set.
/// </param>
internal sealed record FixExecutionResult(
    FixResult Result,
    long PrimaryIssueId,
    IReadOnlyList<long> ResolvedIssueIds);
