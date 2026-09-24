# Fix planning, execution and recording implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Planner, recorder and executor are separate review chunks.

**Goal:** Make FixTask a scheduling adapter with explicit, testable per-file operations.
**Architecture:** A pure planner produces candidate operations; an executor refreshes consent/state and runs them; a recorder commits actual effects and projects progress/history. No new parallel encoding behavior.
**Tech stack:** C#, xUnit, SQLite transaction tests.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md). Requires 01, 02, 03.

## 04-A — pure operation planner

**Read/modify:** FixTask.BuildTranscodeCompanions, combinedPairs/partners, FixerRank, queue filtering.
**Create:** `P/ScheduledTasks/FixPlanBuilder.cs`, `PlannedFix.cs`; `T/FixPlanBuilderTests.cs`.
**Contract:** deterministic grouping/order without config globals, filesystem writes, probing, DB status changes or ffmpeg execution.

```csharp
internal enum PlannedFixKind { Single, TrackPair, TranscodeWithTracks }
internal sealed record PlannedFix(PlannedFixKind Kind, Issue Primary,
    IReadOnlyList<Issue> Companions);
// Build(IReadOnlyList<Issue> eligibleIssues, ...) -> IReadOnlyList<PlannedFix>
// Supply ordering/path-comparison policy explicitly where current helpers require it.
```

- [ ] Characterize current ordering, manual fix order, size ordering, transcode precedence and audio/subtitle grouping with existing companion tests.
- [ ] Preserve deterministic tie-breaks (add issue ID as a stable final tie only if current order is unspecified and record it). Do not call filesystem size APIs inside the pure builder; supply captured size/order inputs.
- [ ] Group candidates by valid file identity policy. Current OrdinalIgnoreCase grouping is not proof that Linux case-distinct files are one file. Add a case-sensitive volume regression and correct unsafe merging explicitly if present. Do not resolve symlink/canonical paths with string lowercasing.
- [ ] Ensure one issue belongs to at most one operation. Multiple transcode-family issues on the same path must have a defined treatment: preserve current intended behavior, avoid double mutation, and do not claim all defects resolved without evidence.
- [ ] Build single/paired/transcode operations without teaching the planner codec decisions. Runtime execution can drop revoked companions and change the effective operation safely.
- [ ] Keep legacy helpers forwarding to the planner while migrating tests, then remove unused ones. No generic dependency graph engine is needed for the current three shapes.

**Tests:** empty queue; only audio/subtitle; pair; quality+audio+subtitle; duplicate IDs; multiple same-type issues; case-distinct paths; missing primary; stable order; disabled type already excluded; every input assigned once or explicitly deferred, never silently lost. Property-style randomized permutations with fixed seed can prove grouping invariants, but don't add a property-testing package just for this.

Example invariant:

```csharp
var assignedIds = plans.SelectMany(p => new[] { p.Primary.Id }
    .Concat(p.Companions.Select(c => c.Id))).ToArray();
Assert.Equal(assignedIds.Length, assignedIds.Distinct().Count());
Assert.Equal(expectedEligibleIds.OrderBy(id => id), assignedIds.OrderBy(id => id));
```

## 04-B — record actual effects atomically where possible

**Modify:** FixTask history/status blocks; MediaDashDb add a narrow transaction method rather than reaching into its connection from the scheduler.
**Create:** `P/ScheduledTasks/FixExecutionResult.cs`, `FixResultRecorder.cs`; `T/FixResultRecorderTests.cs`.
**Read tests:** FixTaskHistoryFanoutTests, FixTaskTranscodeCompanionTests, IssueRevertTests, BucketReasonContractTests.
**Contract:** result lists actually resolved issue IDs and actual recycled sidecars, with one primary savings attribution. A plan's companion list is not a completed-effects list.

```csharp
internal sealed record FixExecutionResult(FixResult Result, long PrimaryIssueId,
    IReadOnlyList<long> ResolvedIssueIds);
// Recorder consumes the primary issue context, execution result and completion time.
// It inserts history and conditionally transitions eligible issue rows in one short DB transaction.
```

- [ ] Characterize successful/dry-run/failed primary, paired companion and sidecar history rows and lifetime aggregate behavior. Keep SavedBytes separate from BytesFreed.
- [ ] Add a short DB transaction that records related history and conditional transitions together. Reuse current legal transition sets; inspect affected-row counts for conflicts. No filesystem operation runs inside this transaction.
- [ ] Only resolved IDs returned by the executor get completion rows/status changes. Revoked/dismissed companions are not recorded as successfully modified. If an operation genuinely changed bytes before a concurrent status change, history must still report reality while the conditional state transition can conflict.
- [ ] Handle failed DB persistence after successful file commit explicitly: diagnostics/recovery evidence must say mutation occurred but recording failed. Never automatically re-execute the destructive operation because AddHistory failed.
- [ ] Do not claim exactly-once effects from a transaction that starts after filesystem work. If safe retry of recorder is required, add an operation ID/idempotency key with a justified migration and tests; otherwise report failure and require reconciliation instead of automatic replay.
- [ ] Run real SQLite rollback tests: force error on a later history insert, assert no partial DB history/status transaction; verify original file-operation result is still observable to caller.

**Tests:** count savings once for three issues on one file; sidecar rows each restorable with zero double-counted savings; dry-run leaves queued state and actual totals unchanged; failed pair does not falsely resolve subtitle; typed terminal refusal retains failure history while applying documented legacy status; conflicting dismissal remains dismissed; malformed unrelated row does not lose good records; transaction failure rolls back DB changes.

## 04-C — executor and thin scheduled task

**Modify:** `P/ScheduledTasks/FixTask.cs`, task DI, consumers of progress/state.
**Create:** `P/ScheduledTasks/FixExecutor.cs`; `T/FixExecutorTests.cs`, `FixRunOrchestrationTests.cs`.
**Consumes:** planner, current policy/config provider, run state, existing IFixer dispatch, recorder, TimeProvider. Preserve IFixer.CanFix and FixAsync for unmigrated callers. A small adapter for TrackFixer.FixCombinedAsync is enough; don't redesign every fixer interface at once.

- [ ] Extract operation execution and error translation, leaving schedule/window/idle/task metadata in FixTask. Move sharing retries without changing count/delay semantics; fake time for tests.
- [ ] Refresh primary and every companion status/config immediately before execution. If subtitle revoked, run approved audio-only when safe; if no valid primary remains, defer without modifying the file. Feed only allowed operation intent into ffmpeg mapping.
- [ ] Recheck source/policy at the shared commit boundary after long work. Do not catch OperationCanceledException and label it corruption; distinguish cancel before versus after commit from replacement result.
- [ ] Preserve quota suppression for later subtitle downloads, per-item bin-cap/space checks, active-playback behavior, current manual bypass rules, max concurrency and fix order. No throughput optimization in this chunk.
- [ ] Run through recorder once per actual operation, then update coherent run snapshot. Distinguish operation count from issue count; preserve API's current documented denominator until a separately tested contract change.
- [ ] Remove dead branches exposed by extraction. For example, check for `queue.Count == 0` branches after an earlier unconditional empty-queue return; decide intended user message from characterization, not by retaining unreachable code.
- [ ] Confirm the scheduled task remains discoverable with identical key/triggers and manual controls. Save-settings must not resurrect user-deleted task schedules.

**Integration test scripts using injected tasks:** pause fake encoding with TaskCompletionSource; dismiss a companion or enable dry-run; release encoding; assert exact files/mappings/history. Simultaneously request start twice; one run. Cancel during retry delay and verify no next attempt. Force one file's permanent error followed by a valid file and verify the latter runs. Exhaust subtitle quota and verify quality work continues. Stop due to mid-run bin space without marking unexecuted issues complete.

**Gate:** FixTask is readable as scheduling plus service calls; no leftover second planner/recorder path; full regression suite and relevant generated-media flow pass. Internal plan support is complete, but public previews and new retry states remain future features.
