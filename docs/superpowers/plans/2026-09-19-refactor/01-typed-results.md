# Typed outcomes and detail parsing implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available; implement and validate each chunk separately.

**Goal:** Stop internal decisions depending on friendly text while preserving current queue/history semantics.
**Architecture:** Add typed reasons to FixResult, migrate producers before deleting the message adapter, and centralise issue-detail deserialization at the boundary.
**Tech stack:** C#, System.Text.Json, xUnit.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md). All global constraints apply. Requires 00.

## 01-A — explicit result contract

**Modify:** `P/Fixers/FixResult.cs`.
**Create:** `P/Fixers/FixOutcome.cs`, `FixReason.cs`, `FixResultPolicy.cs`; `T/FixResultPolicyTests.cs`.
**Read:** `FixTask.IsStaleFailure`, `IsSubtitleProviderQuotaExhausted`, `BucketReason`; `FixTaskStaleFailureTests`, `BucketReasonContractTests`, `FixTaskSubtitleQuotaTests`.

Proposed names/signatures (one type per file; internal unless API exposure is necessary):

```csharp
internal enum FixOutcome { Unspecified, Applied, Preview, NoChange, Deferred, Refused, Failed }
internal enum FixReason
{
    None, Unknown, SourceMissing, SourceChanged, AlreadyHealthy, NothingToRemove,
    DestinationCollision, OutsideLibrary, TypeDisabled, LibraryDisabled,
    ConsentChanged, Protected, ProbeUnavailable, DrmProtected, UnsupportedFormat,
    IncompatibleContainer, InvalidCombinedRequest, ExistingCover,
    InsufficientSpace, FileLocked, ProviderQuota, VerificationFailed,
    ProcessFailed, AccessDenied, Cancelled, RecoveryRequired
}
internal enum QueueDisposition { KeepQueued, CompleteLegacy }
internal static class FixResultPolicy
{
    internal static QueueDisposition GetQueueDisposition(FixOutcome outcome, FixReason reason);
}
```

The declaration above specifies the API, not a compilable implementation block. `CompleteLegacy` deliberately describes current terminal handling: existing code marks certain non-success stale/refused outcomes Fixed to stop retries. It must not be interpreted as a successful media repair. Do not add new persisted issue statuses in this chunk. `Protected` reserves a typed refusal if needed by current restored-path policy; it does not implement future Never-modify.

- [ ] Inventory every current classifier phrase and its producer, expected queue disposition, bucket and quota effect in test data. Include exception branches, not just `FixResult.Fail` calls.
- [ ] Add `Outcome` and `Reason` without breaking existing construction sites. Use `Unspecified = 0` for legacy/unclassified results during migration; default enum zero must not accidentally classify a failure as Applied. Reject Unspecified at the final execution boundary once migration is complete.
- [ ] Preserve `Success`, `WasDryRun`, message, technical detail, bytes, saved bytes, recovery path and additional recycled sidecars for current consumers. Define invariants for final factories: Applied implies success and not dry-run; Preview implies success and dry-run; other outcomes are not successful mutations.
- [ ] Implement queue disposition from outcome/reason with a conservative unknown fallback that keeps work queued and visible under existing failure policy. Do not mark Unknown complete just to suppress noise.
- [ ] Add table tests for every inventoried mapping and invariant. A translated/different Message with identical outcome/reason must produce identical scheduling behavior.
- [ ] Run focused tests and full Release tests. Do not migrate every caller in this same chunk.

Example test for the proposed policy:

```csharp
[Theory]
[InlineData("NoChange", "AlreadyHealthy", "CompleteLegacy")]
[InlineData("Deferred", "ProviderQuota", "KeepQueued")]
[InlineData("Failed", "Unknown", "KeepQueued")]
public void Queue_policy_is_explicit(string outcome, string reason, string expected)
{
    Assert.Equal(Enum.Parse<QueueDisposition>(expected),
        FixResultPolicy.GetQueueDisposition(Enum.Parse<FixOutcome>(outcome),
            Enum.Parse<FixReason>(reason)));
}
```

**Gotcha:** do not conflate cancellation, refusal, deferral, successful no-op and actual repair merely because they all previously used Success=false. Preserve current behavior first, then name the distinctions so later features can change it safely.

## 01-B — migrate producers and consumers in bounded batches

**Modify:** fixers returning FixResult; `P/ScheduledTasks/FixTask.cs`; `P/Api/HistoryDto.cs` only if required to retain existing technical-detail behavior.
**Create/update tests:** `T/FixOutcomeIntegrationTests.cs`, existing stale/quota/bucket/fanout tests.
**Contract consumed:** 01-A reasons and disposition. **Produced:** all runtime result decisions use reason codes; messages only render/log.

- [ ] Batch 1: migrate PlayabilityFixer and simple refusal fixers, including DRM, healthy revalidation, missing source, collision and unsupported type. Keep `RevalidationVerdict.Indeterminate` distinct and non-destructive.
- [ ] Batch 2: migrate TrackFixer/TranscodeFixer/EmbeddedCoverArtFixer, including verification, incompatible audio/container, invalid combined request, cover exists and recovery-required cases. Include all success, dry-run and catch paths.
- [ ] Batch 3: migrate MissingSubtitleFixer/provider failures and scheduler exception classification. Third-party stderr/provider strings may be parsed once at that integration boundary, then return a typed reason. Do not pretend text parsing can be eliminated from external tools themselves.
- [ ] After each batch run its focused tests; retain a clearly named temporary legacy adapter only for not-yet-migrated producers. Give the adapter no new callers.
- [ ] Change scheduler tally/quota/stale decisions to typed values, including caught file-not-found, directory-not-found, sharing violations and disk-full exceptions. Preserve current diagnostics buckets and queue handling using the characterization table.
- [ ] Verify main and sidecar history preserve TechnicalDetail according to existing schema/DTO contract. If a missing write is an existing defect, add its explicit reproducer and separate correction.
- [ ] Remove message classifiers and the legacy adapter once all production producers are migrated. Search for `IsStaleFailure`, `BucketReason`, quota phrase matching and `result.Message.Contains`; only external-boundary parsing and display tests may remain.
- [ ] Make contradictory final FixResult states unconstructable through factories or validate centrally. Keep API compatibility if this type is exposed; do not change wire enums without a dedicated decision.

**New tests:** two messages/locales with identical reason yield same queue state; same phrase with Unknown reason does not impersonate an AlreadyHealthy outcome; quota stops only subsequent subtitle downloads; dry-run records no actual savings/status completion; recovery-required carries actual preserved path; every migrated reason has a policy/bucket mapping; exception details survive history/API projection.

**Gate:** full tests and production searches prove no internal control flow depends on friendly wording. Do not delete useful message-format tests; retarget them to presentation behavior.

## 01-C — typed issue details, preserving persisted JSON

**Modify:** `P/Fixers/TrackFixer.cs`, `PlayabilityFixer.cs`; matching scanners and `P/Data/Issue.cs` only as needed.
**Create:** `P/Data/IssueDetailsReader.cs`, `PlayabilityDetails.cs`, `SubtitleCleanupDetails.cs`; `T/IssueDetailsReaderTests.cs`.
**Contract:** reader returns a validated result without throwing for malformed/old JSON. Persisted field names and meaning stay unchanged. Begin with these two payloads; do not model every scanner speculatively.

Suggested surface:

```csharp
internal static class IssueDetailsReader
{
    internal static bool TryReadPlayability(string? json, out PlayabilityDetails? details);
    internal static bool TryReadSubtitleCleanup(string? json, out SubtitleCleanupDetails? details);
}
internal sealed record PlayabilityDetails(string? Reason, string? RenameToExtension);
internal sealed record SubtitleCleanupDetails(IReadOnlyList<string> ExternalFiles);
```

- [ ] Confirm actual JSON keys/shapes from scanner writers and fixers before implementing attributes/readers. Preserve `reason`, `rename_to_ext` and existing external-file key exactly. Optional absent fields may be valid legacy input; wrong type/non-object is not valid consent.
- [ ] Add negative tests for null, empty, invalid JSON, arrays/scalars, wrong field types, excessive/unexpected members and unknown reason strings. Preserve unknown fields where round-tripping existing payloads is required.
- [ ] Route these two fixers through one reader each. Parsing success does not authorise paths: library and source-role checks still run on every external sidecar before mutation.
- [ ] Test that a malicious external path equal to the primary video, outside the library or traversing a link is refused by execution. Unknown/missing rename target never guesses a destructive action.
- [ ] Confirm old persisted fixtures still read and newly emitted payloads are compatible. No DB migration or JSON rewrite is needed for this extraction.

Example:

```csharp
[Theory]
[InlineData("[]")]
[InlineData("42")]
[InlineData("{\"rename_to_ext\":42}")]
public void Malformed_playability_details_are_not_valid_actions(string json)
{
    Assert.False(IssueDetailsReader.TryReadPlayability(json, out _));
}
```

**Exit:** 01 contract recorded in PROGRESS, all current tests remain meaningful, and 02 can consume typed reasons without knowing human-readable text.
