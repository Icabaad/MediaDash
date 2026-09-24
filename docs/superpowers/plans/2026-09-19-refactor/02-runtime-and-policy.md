# Runtime state and effective policy implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Finish one chunk before broad constructor/caller migration.

**Goal:** Give mutable run state and policy decisions explicit owners without freezing user intent for an entire run.
**Architecture:** Inject runtime/configuration services into existing task adapters; evaluate operation-specific eligibility from current config and issue state. Migrate diagnostics separately.
**Tech stack:** C#, DI, TimeProvider, xUnit.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md). All global constraints apply; requires 01.

## 02-A — run state and configuration boundary

**Read/modify:** `P/Plugin.cs`, `P/Api/PluginState.cs`, `P/ScheduledTasks/FixTask.cs`, `ScanTask.cs`, `P/Api/MediaDashController.cs`, `P/PluginServiceRegistrator.cs`.
**Create:** `P/ScheduledTasks/RunState.cs`, `RunSnapshot.cs`, `P/Configuration/PluginConfigurationProvider.cs`; `T/RunStateTests.cs`, `ConfigurationSnapshotTests.cs`.
**Contract:** controllers observe coherent snapshots; commands consume one-shot bypass exactly once; persistent summaries remain persisted, transient run flags do not survive restart.

Proposed minimal API, refine names in PROGRESS before consumers depend on them:

```csharp
internal sealed record RunSnapshot(bool IsRunning, string? CurrentPath,
    int Processed, int Total, string? PauseReason);
// RunState owns separate scan/fix instances or keys, never one shared 'busy' bool.
// TryBegin, UpdateProgress, Complete and ReadSnapshot are short synchronized operations.
internal interface IPluginConfigurationProvider
{
    PluginConfiguration GetSnapshot();
}
```

- [ ] Inventory every static flag and who writes/reads it: BypassIdleCheckOnce, IgnoreActivityForCurrentRun, PauseReason, LowImpactHintEmitted, manual scan grace, Plugin scan/fix progress, LastFixRun and redownload warnings. Separate persistent summary from transient control.
- [ ] Characterize API output during idle/running/paused/completed/cancelled states. Preserve null/zero semantics and progress units: IFixer reports 0..1 while scheduled task progress uses 0..100.
- [ ] Add RunState tests with simultaneous TryBegin callers: exactly one succeeds. A failed start cannot consume the other run's state. Use barriers/TaskCompletionSource, not Thread.Sleep races.
- [ ] Introduce TimeProvider for elapsed/grace/window timestamps in extracted code. Clock use must preserve UTC persistence and local scheduling semantics; TimeOnly overnight ranges must not become UTC accidentally.
- [ ] Implement configuration snapshots that copy mutable arrays used by execution. A reference to Plugin.Instance.Configuration is not an immutable snapshot. Do not use JSON serialization that changes XML defaults/null handling merely to clone configuration.
- [ ] Migrate one task and its controller status/command consumers; validate; migrate the other. Preserve Plugin facade properties temporarily if consumers still exist, but only one owner holds the state.
- [ ] Preserve PluginState's summary persistence and startup hydration without recursively writing it again. On shutdown/cancellation, cleanup belongs in finally and cannot leave Running=true.

**Tests:** no torn totals/current-path snapshot; begin/cancel/complete race; cancellation after completion harmless; one-shot bypass consumed once; ignore-activity resets at run end; failed startup cleans state; manual grace uses fake time; overnight window and clock change; modifying returned config arrays does not mutate saved config; next GetSnapshot sees changed settings; restart reloads summary but not transient bypass.

Example for the snapshot-copy test (construct provider with a test-owned config source):

```csharp
var first = provider.GetSnapshot();
var second = provider.GetSnapshot();
Assert.NotSame(first.AllowedAudioLanguages, second.AllowedAudioLanguages);
first.AllowedAudioLanguages[0] = "changed-in-test";
Assert.NotEqual("changed-in-test", provider.GetSnapshot().AllowedAudioLanguages[0]);
```

**Gotchas:** do not hold a state lock during persistence or library calls; don't serialize scan and fix globally if they had separate supported lifecycle rules; controller requests must not directly assign shared flags after migration. Tests may keep compatibility facades until all consumers move.

## 02-B — effective operation policy

**Modify:** FixTask queue/current-item filters, TranscodeFixer.IsTypeAuthorized, TrackFixer combined checks, config provider and DI.
**Create:** `P/Fixers/FixPolicyEvaluator.cs`, `FixIntent.cs`, `PolicyDecision.cs`; `T/FixPolicyEvaluatorTests.cs`, `FixPolicyRevalidationTests.cs`.
**Consumes:** 01 FixReason; current config snapshot; per-issue current status; LibraryGuard enabled-locations/boundary methods; existing restored-path signal.
**Produces:** typed allow/refuse decision for each intended operation. It does not perform I/O or mark queue status.

```csharp
internal enum FixIntent { Scheduled, Approved, Companion }
internal readonly record struct PolicyDecision(bool Allowed, FixReason Reason);
// FixPolicyEvaluator.Evaluate takes config, issue type/status, intent,
// within-enabled-library and restored/protection inputs explicitly.
// The caller obtains those inputs afresh; the evaluator contains no Plugin.Instance reads.
```

- [ ] Write a decision table from existing queue, autoqueue and fixer gates before coding. Distinguish configured capability from per-issue consent. `ManualApprove` means an individual queued issue or another documented explicit approval is required; a nonempty language allow-list is not approval.
- [ ] Preserve current handling of empty EnabledLibraries (all libraries), null FixerDisabled, case sensitivity of enum names and unknown fixer identifiers. Unknown future issue types must not default to allowed mutation.
- [ ] Apply existing restored-path rules where they currently prevent autoqueue. Do not broaden them silently into a permanent manual prohibition; future Never-modify is separate.
- [ ] Use the evaluator for initial candidate selection and again immediately before dispatch, with fresh primary and companion status/config. A companion no longer approved is removed from the operation and from completion effects.
- [ ] Audit `ManualApprove` incidental cleanup in TranscodeFixer: current IsTypeAuthorized only checks Off/disabled, so its name is not proof of per-issue consent. If that permits unapproved cleanup, add a failing integration test and fix the consent defect explicitly before extraction parity tests are updated.
- [ ] Pass allowed operations into argument generation, rather than making BuildArgs independently infer permission from a language preference. Preserve compatible streams when a cleanup operation is disallowed; incompatibility must refuse safely.
- [ ] Recheck dry-run/policy before destructive commit as well as before starting long ffmpeg work. If the user enables dry-run during encoding, retain original and discard or safely preserve output; do not commit under the stale setting.

**Required matrix:** Off/manual/automatic × disabled/enabled × queued/dismissed/detected × primary/companion; scoped/unscoped library; restored autoqueue/manual cases; unknown type; config flips after queue and during encode; primary approved with subtitle companion revoked; manual task trigger with unapproved issue.

**Assertions:** false decision identifies exact reason, no process/commit call for refused work, no filtered tracks for absent consent, no companion success row for a revoked operation, re-enable resumes only eligible work. Use actual mapping output for at least one audio/subtitle/quality combination, not only policy booleans.

**Gate:** every migrated caller uses the same policy semantics; runtime rechecks remain intact. Document any intentionally retained independent final guards (library/path/stream guards are defense in depth, not unwanted duplication).

## 02-C — diagnostics service without startup loss

**Modify:** `P/Api/Diagnostics.cs`, `PluginState.cs`, `PluginServiceRegistrator.cs`, callers in the active services; existing Diagnostics tests/collections.
**Create:** `P/Diagnostics/DiagnosticService.cs` and a narrow sink interface if necessary; `T/DiagnosticLifecycleTests.cs`.
**Contract:** bounded deduplicated in-memory view, persisted history, early-startup buffering, selective purge and full technical evidence retain current behavior.

- [ ] Characterize Record before Attach, repeated Attach, persisted reload, dedup timestamps/counts, max entries and purge-by-source-prefix. Preserve stable hash compatibility for existing diagnostic identities.
- [ ] Move ownership into a singleton DI service. Keep a temporary static facade only for startup callers that cannot yet receive injection; facade forwards to one owner and buffers safely until attachment.
- [ ] Avoid a dependency cycle: DB reader/logging failures must not recursively persist diagnostics back into the failing DB. Keep logger-only fallback for diagnostics persistence failures, with bounded in-memory evidence.
- [ ] Migrate service callers in batches. Do not change the public Errors routes or hide raw technical details.
- [ ] Add simultaneous Record/Clear/Attach tests, DB-unavailable startup, shutdown during persistence and SMART-only purge preserving other errors.
- [ ] Remove static mutable ownership only after all callers migrate. Drop global test serialization only if tests truly have separate service/DB instances; prove it with concurrent tests rather than simply deleting collections.

**Gate:** service startup and restart preserve diagnostics, no deadlock/recursion, and 03/04 can receive typed result and policy services without global state setup.
