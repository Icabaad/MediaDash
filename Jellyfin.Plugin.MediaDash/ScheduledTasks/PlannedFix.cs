using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// One deterministic unit of fix work: a primary issue plus any co-queued companions
/// a single fixer call resolves in the same operation. Produced by
/// <see cref="FixPlanBuilder"/>; consumed by the executor.
/// </summary>
/// <param name="Kind">Operation shape — see <see cref="PlannedFixKind"/>.</param>
/// <param name="Primary">The issue driving the operation.</param>
/// <param name="Companions">Co-resolved issues; empty for <see cref="PlannedFixKind.Single"/>.</param>
internal sealed record PlannedFix(
    PlannedFixKind Kind,
    Issue Primary,
    IReadOnlyList<Issue> Companions);
