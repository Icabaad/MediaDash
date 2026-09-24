# Baseline and test gates implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Execute one chunk and its tests at a time.

**Goal:** Establish a reproducible starting point and stop the harness from reporting false success.
**Architecture:** Reuse existing unit/live harnesses, add truthful failure accounting and isolated harness self-tests. Do not run destructive phases merely to test the harness.
**Tech stack:** PowerShell 7, .NET 9/xUnit, existing CI YAML.
**Spec:** [Master plan](../2026-09-19-mediadash-refactor.md) and [coverage findings](../../../testing/REGRESSION-COVERAGE-REVIEW-2026-09-19.md). All master constraints apply.

## 00-A — baseline identity and characterization inventory

**Files:** create `PROGRESS.md` beside this file and untracked/local `artifacts/refactor/00A/`; read `CLAUDE.md`, production/test csproj, `.github/workflows/ci.yaml`, existing tests and current diff. Do not edit production code.
**Interface:** subsequent chunks consume a source identity, a test baseline and a list of preserved contracts.

- [ ] Record git HEAD/status/diff stat and hashes for every modified and untracked source/test file. Include untracked code; `git diff` alone omits it. Exclude binaries, tokens and generated caches from tracked records.
- [ ] Run Release build and full tests using master commands with `00A` as results directory. Record failures as present baseline defects and investigate, not automatic refactor regressions. Earlier 998 count is not an acceptance threshold.
- [ ] Inventory current HTTP routes/response shapes from controller attributes and local OpenAPI if available; record status mappings for approve/dismiss/revert and both restore routes. Capture XML configuration property names/defaults and DB user_version/schema.
- [ ] List each fixer that changes existing media, its disposal behavior, temp/swap naming, verifier, source recheck and recovery behavior. Include externally recycled sidecars and rename-only fixes.
- [ ] Verify tests added by Claude, especially SweepOrphanProtectionTests, are included. Record exact failure windows they cover so later extractions do not remove them.
- [ ] Create PROGRESS with next step 00-B. No deployment required for this chunk.

**Gotchas:** a no-op test from an OS-specific early return is not platform coverage; a release build DLL is not the deployed DLL; shared static diagnostics can still require serialized tests; command output can contain machine paths, so review artifacts before tracking them.

## 00-B — harness truth and ownership

**Modify:** `tools/comprehensive-test/lib/framework.ps1`, `run-full-tests.ps1`, `phases/07-ui.ps1`; `tools/repair-test/validate-outputs.ps1` and fixture baseline generation as necessary.
**Create:** `tools/comprehensive-test/self-tests.ps1` and `tools/repair-test/validator-self-tests.ps1` as isolated test entry points.
**Contract:** a required check returns PASS only after an asserted observation. Failed commands, phase exceptions, missing required tools and unexecuted required coverage cannot result in a passing release run. Quick/partial diagnostic runs remain available but are explicitly labelled partial.

- [ ] Add PowerShell self-tests that stub network/browser/file-operation dependencies, never the assertion itself. Require the harness's real accounting/exit-decision function. Extract that decision into a pure function if needed.
- [ ] Reproduce UI failures: a browser result `{ExitCode=1; Output='browser failed'}` must fail Login, Issues and Recycle bin tests. Browser command wrapper must check nonzero exit before interpreting output.
- [ ] Assert authenticated page state, active target panel and expected fixture rows/chips. Querying a DOM node without asserting the answer is not a test. Treat missing browser as blocked in release mode.
- [ ] Count phase-level exceptions as ERROR and preserve the original exception/log. Validate unknown phase IDs; always perform required preflight for selected phases. Teardown runs in `finally`, and teardown failure is recorded rather than hiding an earlier error.
- [ ] In repair validation, compare source SHA-256 plus original full path and current-run manifest association to restored/recycled bytes. Basename alone is never identity. Missing expected recycled original must fail, not WARN+exit 0. Healthy controls must retain hashes.
- [ ] Give repair fixtures declared allowed outcomes: must-repair, rename-only, untouched, or permitted-disposal. Do not accept recycling as a successful repair. Keep strict decode and actual Jellyfin playback as separate gates.
- [ ] Add test-target guard before any live harness mutation: parsed loopback URI on port 8099, owned generated fixture manifest and selected issue IDs tied to those paths. Reject redirects to disallowed hosts and arbitrary fixture paths. Do not treat `-OnlyPhases` as permission to skip ownership/preflight.
- [ ] Run self-tests as child processes and verify exit codes. Run PowerShell parser checks over changed scripts. Save output under `artifacts/refactor/00B/`.

Self-test assertions should use a small failure helper rather than require a new test framework:

```powershell
function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
# After executing a fake-failed-browser case through the actual harness:
Assert-Condition ($caseResult -ne 'PASS') 'A failed browser was accepted'
# After executing the runner as an isolated child process:
Assert-Condition ($childExitCode -ne 0) 'An aborted required phase exited successfully'
```

`$caseResult` and `$childExitCode` are observations from the self-test runner, not literal predetermined values. Implement the fixture setup in these test scripts. Required self-test table:

| Input | Expected |
|---|---|
| Browser exit 1, empty DOM, missing panel, console exception | Each respective case fails. |
| Phase throws before first test; unknown phase; required phase all skipped | Non-passing run with explicit reason. |
| Teardown throws after a failing case | Both failures retained, nonzero exit. |
| Old recycle entry shares basename but wrong hash/run | Repair validator fails. |
| Healthy file modified in place | Validator fails even though filename exists. |
| Target is nonloopback, wrong port or fixture outside manifest | Refused before auth, config writes or file changes. |
| Clean successful stub run | Pass; self-test must not make every result fail. |

**Gate:** these defects are verified fixed using isolated self-tests. Real UI/playback remains a later gate. Do not claim stub coverage as real UI validation.

## 00-C — OS checks and retained artifacts

**Modify:** `.github/workflows/ci.yaml`; add invocation of the PowerShell harness self-tests where their dependencies are stubbed.
**Contract:** Ubuntu and Windows build/test jobs retain individual TRX output; an OS failure is not hidden by the other OS's success.

- [ ] Add a Windows/Ubuntu matrix, `fail-fast: false`, Release build/test and an always-run artifact upload with distinct artifact names. Retain existing branch triggers. A local YAML edit does not prove the remote jobs passed.
- [ ] Keep shared-resource test collections in place. Do not add blanket retries around unit tests or catch/delete failures in cleanup.
- [ ] Run the local Windows equivalent and parse workflow syntax with available tooling. If Linux/Docker cannot run here, mark Linux pending until actual execution; do not use the existing integration script at port 8097 here.
- [ ] Record baseline matrix status and hand over to 01-A.

Suggested job shape to adapt to existing actions:

```yaml
strategy:
  fail-fast: false
  matrix:
    os: [ubuntu-latest, windows-latest]
runs-on: ${{ matrix.os }}
# Build in Release, test --no-build with --logger trx and an OS-specific results path.
# Upload those results with if: always(); a failed test must still fail the job.
```

**Exit criteria:** 00-A evidence exists, 00-B self-tests catch the old false passes, Windows is freshly validated, and other platform evidence is correctly labelled. Production refactors can proceed with pending unavailable environments, but final release readiness cannot claim them passed.

## 00-D — establish the physical file convention

**Spec:** [Required file layout](FILE-LAYOUT.md).
**Modify/create:** the three mixed-file enum moves listed there; a file-to-type inventory under the local 00D evidence directory. Do not start functional service extraction in this mechanical chunk.

- [ ] Inventory actual scanner/fixer registrations and top-level types, excluding generated obj/bin content. Use compiler-aware inspection when available; a regex inventory is only a candidate list.
- [ ] Move RecycleProvenance, RevalidationVerdict and ConfidenceTier into matching files. Preserve namespace, accessibility, underlying values, XML documentation and attributes; remove the old declarations.
- [ ] Verify each concrete scanner/fixer already has its own file; retain that structure. Shared TrackFixer does not require creating separate audio/subtitle fixers.
- [ ] Record nested-type exceptions and flag independent behavior helpers for extraction in the responsible later package. Avoid broad namespace/folder churn.
- [ ] Build and run affected existing tests, then the full Release suite for the mechanical move set. Verify DI registration inventory unchanged. No new behavior test is required solely to prove a file moved.
- [ ] Add layout inventory location to PROGRESS; apply one top-level type per file to every later package, including enum/interface declarations shown together in illustrative code blocks.

**Gate:** actual files follow the contract; no duplicate definitions or omitted compilation, no behavioral changes bundled with these moves.
