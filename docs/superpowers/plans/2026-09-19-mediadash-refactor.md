# MediaDash staged refactor implementation plan

> **For agentic workers:** Use `superpowers:executing-plans`, if available, to execute this plan task by task. Steps use checkbox syntax. Read this master and only the active package; do not load or implement the whole programme in one session.

**Goal:** Make existing MediaDash behavior safer to change by giving outcomes, execution policy, file replacement, restore, persistence and UI lifecycle explicit owners.

**Architecture:** Incrementally extract behavior behind the current plugin, scheduled-task and HTTP entry points. Introduce small services around real responsibilities, retain existing interfaces while migrating consumers, and remove transitional adapters once their callers are migrated. File operations and SQLite are not one atomic transaction; recovery must remain explicit.

**Required physical organisation:** Follow the [file-layout contract](2026-09-19-refactor/FILE-LAYOUT.md). Each scanner, fixer, service, controller and reusable supporting type has its own named source file. Dashboard JavaScript and CSS are separate embedded assets. Creating sections or extra closures inside the existing large files is only an intermediate step, not the finished refactor.

**Tech stack:** C# net9.0, Jellyfin plugin APIs, Microsoft.Data.Sqlite, xUnit, embedded plain JavaScript/HTML/CSS, PowerShell/bash test harnesses. No new runtime framework, ORM, mediator, message bus or frontend build pipeline is needed.

**Spec:** [PLAN.md](../../../PLAN.md), [CLAUDE.md](../../../CLAUDE.md), [coverage review](../../testing/REGRESSION-COVERAGE-REVIEW-2026-09-19.md), [roadmap acceptance matrix](../../testing/ROADMAP-TEST-MATRIX.md), and the design decisions in this master. This plan implements the user's refactor request; it does not authorise implementing all roadmap features.

## How Claude should work

Copy this starting instruction into Claude:

```text
Implement the staged MediaDash refactor described in
docs/superpowers/plans/2026-09-19-mediadash-refactor.md.
Read that master and package 00 first. Preserve the existing dirty working tree.
Read its FILE-LAYOUT.md contract and physically split responsibilities into
separate files; preserve the existing one-file-per-scanner/fixer organisation.
Work one chunk at a time as the senior maintainer: verify current code, make
routine design decisions, write behavior/failure tests, implement, review and
record evidence in the plan's PROGRESS.md. Continue to the next ready chunk
after its gate passes; do not stop to ask permission for routine local work.
When context is tight, stop at a tested boundary with an exact continuation note.
Do not implement deferred roadmap features, publish, push, or touch production
Jellyfin. Keep required runtime checks explicitly pending if unavailable.
```

Act as the senior developer responsible for preserving users' files and maintaining this code after the refactor. Check assumptions against the current source. Prefer a small explicit contract over a clever generic abstraction. Make routine design decisions autonomously; record why when deviating from a proposed file/class name or boundary. Preserve behavior unless a separately reproduced defect requires a correction. Keep that correction distinguishable from the extraction.

Start with package 00. Complete one chunk through its tests and review before starting another. A package can take several sessions; a chunk should have one explainable result and a focused diff. Roughly 3–6 production files is a useful review target, not a rule that forces artificial splitting. Changing constructors across many callers is its own chunk. Stop at a green boundary when context is getting tight; write the continuation record instead of beginning another subsystem.

Do not ask for approval for every local extraction or test. Do pause dependent work when a real product decision changes consent, destructive behavior, compatibility or the public contract and this plan does not resolve it. State the precise decision and continue independent work where possible. Do not spawn parallel editors of FixTask, MediaDashDb, the controller or the UI. Default to sequential execution.

## Verified planning baseline and drift

Inspected 2026-09-19, local checkout `C:\dev\mediadash`, base HEAD
`4809c3dd890aa4e6c8f6595252ef59fc1aadee54`, with a substantial dirty tree containing Claude's fixes. HEAD alone does not identify the reviewed source. New `SweepOrphanProtectionTests.cs` appeared after the earlier test audit, so **998/998 is historical evidence from that earlier turn, not the refactor baseline**. Establish fresh results in 00-A.

Current landmarks (search method names; line numbers drift):

| Component | Responsibility currently mixed together |
|---|---|
| `ScheduledTasks/FixTask.cs` (~1,436 lines) | Scheduling, policy, pairing/companions, execution, exception classification, DB writes, progress, diagnostics. |
| `Fixers/FixResult.cs` | Success/dry-run booleans, user message, technical detail, savings and recovery paths; lacks typed failure reason. |
| `Fixers/TranscodeFixer.cs`, `TrackFixer.cs`, `EmbeddedCoverArtFixer.cs`, `PlayabilityFixer.cs` | Output production/verification and separate replacement/recovery sequences. Playability's repair policy is not identical to the other fixers. |
| `Api/MediaDashController.cs` (~2,140 lines) | HTTP mapping, restores, system/library aggregation, diagnostics and task control. |
| `Data/MediaDashDb.cs` (~1,853 lines) | Migrations, row readers, cache, issue state, history, restores, diagnostics, aggregates. |
| `Plugin.cs`, `Api/PluginState.cs`, `Api/Diagnostics.cs` | Static live state, persisted summaries and startup attachment. |
| `Configuration/configPage.html` (~10,441 lines) | Markup/styles, page state, networking, polling, rendering, event wiring and settings. |

Paths in package file lists are relative to the repository. `P/` means `Jellyfin.Plugin.MediaDash/`; `T/` means `Jellyfin.Plugin.MediaDash.Tests/`. Expand these prefixes when editing. Proposed files do not already exist unless stated; check before creating a duplicate.

## Global constraints

- Work in `C:\dev\mediadash`. Never reset/clean/stash away other work, amend commits, force-push, push, publish releases, or mutate GitHub issues/PRs. This is a local refactor. Do not execute `tools/release.ps1` as a verification command: it publishes.
- Preserve existing uncommitted work. Snapshot its identity and separate your files. A clean worktree created from HEAD omits those fixes: do not switch to one without explicitly carrying and verifying the intended source state.
- Live testing here is limited to `http://localhost:8099`. Never touch `192.168.1.117`. Only generated, owned fixtures may be approved for destructive operations; the shared fixture library also contains real media. Bulk approval of the entire queue is not a safe test setup.
- Keep net9.0. Current production csproj pins Jellyfin.Controller/Model **10.11.0** compile-only; the test project uses **10.11.8** runtime references. Preserve the lowest declared ABI, test it, and do not mistake those two versions for a typo to normalise. Keep support for the project's 10.11 and 12 host lines.
- Do not add static references to host `User` types that moved between Jellyfin versions. Preserve the reflection compatibility bridge in StaleContentScanner and native-library/runtime packaging behavior.
- Preserve plugin GUID, routes, JSON field meanings, embedded page name/resource, XML config property names/defaults and persisted enum numbers. Additive changes still need contract tests. Keep analyzers and warnings-as-errors enabled.
- Preserve Windows/Linux/macOS/Docker path and platform handling. Do not silence platform tests or infer actual filesystem case sensitivity solely from OS name.
- Preserve library boundaries, last audio/video, verified outputs before original disposal, collision refusal, current per-type disposal policy, dry-run default ON, free-space checks and indeterminate-probe refusal. More detail in each operation package.
- Retain the existing SQL conditional transition semantics and malformed-row tolerance. Do not replace conditional writes with read-then-unconditional-update.
- Keep technical details end-to-end and behind a disclosure in UI. User-facing strings are never an internal control protocol.
- Keep the embedded plain-JS UI and its current visual design. This is not a redesign or framework migration. Responsive checks cover 360/390/430 px, desktop 1280/1920, light and dark.
- Preserve all current regression tests; update a test only when its responsibility intentionally moves, documenting the equivalent replacement. Do not weaken assertions to accommodate an extraction.

## Design decisions to keep consistent across packages

1. **Outcome is separate from issue status.** A terminal refusal can stop retries without claiming the media was repaired. Initially preserve the current DB status mapping through an explicit adapter; new Needs-attention state is roadmap work, with a separate migration later.
2. **Planning is pure, execution is authoritative.** A plan describes work using current metadata and consent. It is never permission to ignore later config/status/path changes. Preview endpoints are deferred; the internal planning seam is created now.
3. **Policy sees operation-specific intent.** A manual task trigger bypasses scheduling/idle gates only where currently intended. It never approves every detected issue, bypasses protection, or authorises incidental track removal.
4. **Replacement is a concrete protocol.** The shared service owns staging, collision handling, disposal and recovery; callers own codec-specific output verification. A verification callback must run within the service's protected sequence, not be a forgeable `bool verified` field.
5. **One result records actual effects.** The recorder receives what really executed, not all initially planned companions. Savings count once; sidecars remain individually restorable.
6. **Database lifetime stays simple.** One connection factory, short transactions, parameterised SQL, no transaction held across ffmpeg/filesystem/network waits. Split along data responsibilities only after shared hydration/transaction semantics are stable.
7. **Inject mutable external dependencies where needed.** Use `TimeProvider` for newly extracted timing and small seams for file commit faults/config access. Pure static helpers remain static; do not introduce an interface for every class.
8. **Transient state is not persistent intent.** Run flags live in a service; disabled fixers/protection/restored-path signals remain persisted where they belong. A restart must not resurrect a manual bypass flag.
9. **Fault fixes stay explicit.** If characterization reveals a current bug, add a failing reproducer, fix it in its own chunk or clearly isolated change, and carry the corrected invariant forward. Do not preserve an unsafe behavior merely for parity.

Before introducing a new abstraction, answer: which two current callers or which
present safety/test boundary requires it; who owns its state; which dependency
direction it removes; and which behavior test would detect an incorrect adapter.
If the answer is only “a future feature might need this,” leave it out. When a
proposed extraction makes its callers harder to follow, revise the boundary and
record the tradeoff instead of mechanically following the suggested class list.

## Package order and independently reviewable chunks

| Package | Chunks | Prerequisites | Deliverable |
|---|---|---|---|
| [00 — baseline and trustworthy tests](2026-09-19-refactor/00-baseline-and-test-gates.md) | 00-A baseline; 00-B harness truth; 00-C OS checks; 00-D file organisation | None | Fresh baseline, reliable validation and one type per file. |
| [01 — typed results and details](2026-09-19-refactor/01-typed-results.md) | 01-A result contract; 01-B producers/consumers; 01-C detail decoding | 00 | Scheduling no longer parses user messages; details decoded at a clear boundary. |
| [02 — runtime and policy](2026-09-19-refactor/02-runtime-and-policy.md) | 02-A run context; 02-B effective policy; 02-C diagnostics | 01 | Fresh policy decisions and controlled service lifetimes. |
| [03 — safe replacement](2026-09-19-refactor/03-safe-replacement.md) | 03-A protocol/seam; 03-B first fixer; 03-C remaining callers | 01, 02-B | One verified replacement/recovery owner. |
| [04 — fix orchestration](2026-09-19-refactor/04-fix-orchestration.md) | 04-A planner; 04-B recorder; 04-C executor | 01, 02, 03 | Small scheduled-task adapter with tested operation semantics. |
| [05 — restore and persistence](2026-09-19-refactor/05-restore-and-persistence.md) | 05-A migrations/readers; 05-B restore service; 05-C focused stores | 01, 02, 03, 04-B | Thin restore routes, consistent claims and data ownership. |
| [06 — dashboard boundaries](2026-09-19-refactor/06-dashboard-boundaries.md) | 06-A lifecycle/network; 06-B tabs; 06-C settings; 06-D separate assets | 00-B, 02-A; run after 05 | Separate tab, networking, lifecycle, settings and stylesheet files. |
| [07 — integration and handoff](2026-09-19-refactor/07-integration-and-handoff.md) | 07-A contracts; 07-B live matrix; 07-C final audit | All | Package-level evidence and explicit readiness report. |

All 26 chunks start unchecked. Completing a code extraction does not complete its runtime evidence gate. The recommended execution order is the table order; dependencies describe necessary contracts, not permission for simultaneous edits of shared files.

## Per-chunk workflow

- [ ] Read this master, active package, affected source and tests; inspect git diff since previous checkpoint.
- [ ] Confirm assumptions. Record one paragraph of any changed design decision in the continuation log before building on it.
- [ ] Add behavior characterization that passes on the starting implementation; new contracts/bug fixes need a failing test before implementation. Do not claim an extraction test must fail before code moves.
- [ ] Implement the smallest vertical slice. Add DI wiring with the slice that consumes it, not in a speculative scaffolding pass.
- [ ] Run focused tests, then required broader checks. For filesystem/DB/HTTP changes, include the relevant failure and concurrency cases; for UI run the viewport/theme gate.
- [ ] Review the diff against the invariants, verify no unrelated churn, and record evidence paths and limitations.
- [ ] Tick only completed steps, update the continuation record, then proceed to the next ready chunk. Do not publish or stage unrelated files. Local commits, if used under existing user instructions, contain only the reviewed chunk.

Canonical local commands (from repo root):

```powershell
git -c safe.directory=C:/dev/mediadash status --short
git -c safe.directory=C:/dev/mediadash diff --stat
dotnet build Jellyfin.Plugin.MediaDash.sln --configuration Release --nologo
# Replace 01A with the active chunk identifier; use a unique run directory each time.
dotnet test Jellyfin.Plugin.MediaDash.sln --configuration Release --no-build --nologo --logger 'trx;LogFileName=results.trx' --results-directory artifacts/refactor/01A
git -c safe.directory=C:/dev/mediadash diff --check
```

Focused example: `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FixTaskStaleFailureTests --nologo`. Newly named tests are listed in each package; use their class name in the filter. Build before `--no-build`. For docs-only work, validate links/contracts rather than rerunning the entire media suite. Preserve the xUnit DB/Diagnostics collections until tests no longer share those resources.

## Continuation record

Create `docs/superpowers/plans/2026-09-19-refactor/PROGRESS.md` during 00-A. Do not mark future work done in advance. Use this record at every session boundary:

```markdown
## Checkpoint: 01-A
- Source: base commit + current diff/untracked inventory artifact path
- Completed: exact steps and new contracts
- Files changed by this chunk: paths
- Validation: commands, exit status, counts and artifact paths
- Runtime evidence: passed / blocked / not run, with reason
- Decisions: compatibility adapters retained and why
- Known defects: reproducer, severity, fix status
- Next: package/step plus the smallest safe action
- Shared tree: external edits detected and how reconciled
```

Do not store tokens, live config secrets or user media in tracked artifacts. A failed gate is a reason to diagnose, not to reset the baseline. A blocked hardware/host check remains blocked; it is never inferred from another host's unit tests.

## Explicitly deferred feature work

This refactor creates seams for previews, per-library profiles, persistent Never-modify, bounded retries, minimum savings, new history reports and richer diagnostics. It does not ship those features, alter their roadmap completion state or invent schema fields for speculative uses. Add a new schema only when a present correctness requirement needs it (for example, safe restore operation recovery), with a separate migration and tests. Keep those deliberate changes visible in the checkpoint.

## Definition of complete

All selected chunks have current evidence; the file-layout contract is satisfied with actual separate source files; no production control flow relies on friendly-message matching; migrated replacement paths pass injected-failure tests; plan/actual effects agree; restore paths share enforced claim/recovery rules; API/config/DB compatibility is demonstrated; UI lifecycle, embedded-asset loading and viewport tests pass; package runtime limitations are stated. Report changed files, retained adapters, deferred features and **Safe-to-push** or **Do-not-push**, with the exact validation limits. No release is published by this plan.
