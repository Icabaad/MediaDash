using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// A row from the <c>restore_operations</c> table. Exposed by
/// <see cref="MediaDashDb.GetRestoreOperations"/> for the NeedsReview surface and for tests that
/// need to inspect state after a crash + reconciliation. Only three states exist in practice:
/// <list type="bullet">
/// <item><description><c>Pending</c> — an operation is in flight; the owner holds
/// <see cref="OperationId"/> and can Complete or Abort it.</description></item>
/// <item><description><c>NeedsReview</c> — an operation left partial state on disk; requires
/// manual triage.</description></item>
/// <item><description><c>Completed</c> — transient state written just before the row is deleted;
/// only observable if the process died between the UPDATE and the DELETE inside
/// <see cref="MediaDashDb.CompleteRestore"/> and startup reconciliation hasn't run
/// yet.</description></item>
/// </list>
/// </summary>
/// <param name="OperationId">GUID-shaped owner token issued by
/// <see cref="MediaDashDb.TryBeginRestore"/>. Only the caller holding this token can
/// Complete / Abort / MarkNeedsReview the row.</param>
/// <param name="EntryKey">Canonical identity of the bin entry (typically the recycle path).
/// Serializes physical access — two callers targeting the same key race on this string.</param>
/// <param name="HistoryId">Optional history row that triggered this restore. Null for
/// manifest-only restores where no history row exists.</param>
/// <param name="SourcePath">The bin file/directory being restored (recycle path).</param>
/// <param name="DestinationPath">The resolved final target on disk (post-suffix, post-force
/// swap). Rewritten on Complete to match the actual landing spot when the FS work
/// re-resolves.</param>
/// <param name="State">One of <c>Pending</c>, <c>Completed</c>, <c>NeedsReview</c>.</param>
/// <param name="StartedUtc">When the claim was acquired.</param>
/// <param name="Detail">Human-readable explanation when the row is in
/// <c>NeedsReview</c>; null otherwise.</param>
public sealed record RestoreOperationRow(
    string OperationId,
    string EntryKey,
    long? HistoryId,
    string SourcePath,
    string DestinationPath,
    string State,
    DateTime StartedUtc,
    string? Detail);
