# Integration, compatibility and final handoff implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Evidence gathering is part of completion, not an optional final note.

**Goal:** Demonstrate the refactor preserves real plugin behavior and report remaining risks honestly.
**Architecture:** Contract tests plus isolated package/host/media validation; retain all evidence by candidate identity. No remote publication.
**Tech stack:** .NET/xUnit, PowerShell/browser, Jellyfin hosts, bundled ffmpeg/ffprobe.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md), [regression review](../../../testing/REGRESSION-COVERAGE-REVIEW-2026-09-19.md), [roadmap matrix](../../../testing/ROADMAP-TEST-MATRIX.md). Requires the preceding packages.

## 07-A — compatibility and dependency audit

**Modify only if needed:** tests, DI registration, package/build metadata for actual defects. Do not bump release version or changelog merely to describe internal file moves.
**Create:** `T/RefactorCompatibilityTests.cs` if existing contract tests cannot house the assertions; local artifacts under `artifacts/refactor/07A/`.

- [ ] Run full Release build/test on final source. Retain TRX and exact source/diff identity. Confirm Windows/Ubuntu results independently; do not count another OS's result twice.
- [ ] Compare HTTP route/method/auth/JSON/status contracts against 00-A. Specifically ordinary-user denial, missing/conflicting issue transitions, restore response path/suffix and diagnostics technical details. Reflection attributes alone do not prove real authorization middleware.
- [ ] Load old XML configuration fixtures and ensure current defaults/modes/disabled arrays/language preferences are preserved. Save/reload with unknown fields where host serializer supports them; record actual serializer behavior rather than assuming forward compatibility.
- [ ] Upgrade populated copied DBs through final schema, reopen twice, run transitions/history/restore reads, and verify malformed rows do not turn API surfaces into HTTP 500.
- [ ] Check DI lifetimes and cycles: per-plugin run/config/diagnostic owner, no singleton capturing disposed scoped service, no diagnostics/DB recursive persistence, no duplicate live static/service state.
- [ ] Search production for obsolete message classifiers, duplicate replacement protocols, direct issue status SQL outside intended transition owners, temporary facade callers and orphaned helpers. Explain necessary remaining duplication such as independent final guards.
- [ ] Rebuild publish output to a local artifact directory and inspect DLL/deps/XML/resource contents. Preserve FlattenWindowsRuntime behavior and runtime dependencies; do not bundle duplicate Jellyfin/SQLite/native assemblies. Use build/publish commands, not release.ps1.

Suggested commands:

```powershell
dotnet publish Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj -c Release -o artifacts/refactor/07A/publish
rg -n 'IsStaleFailure|BucketReason|result\.Message\.Contains' Jellyfin.Plugin.MediaDash -g '*.cs'
rg -n 'UPDATE issues SET status|Plugin\.Instance|static.*(PauseReason|BypassIdle)' Jellyfin.Plugin.MediaDash -g '*.cs'
git -c safe.directory=C:/dev/mediadash diff --check
```

Search matches require review; they are not automatically defects. A source search is not a substitute for behavior tests.

## 07-B — isolated runtime and recovery matrix

**Read before running:** `tools/deploy-local.ps1`, harness preflight, generated fixture manifest and host config. The existing deploy script says it kills running local Jellyfin and deploys to both v10/v12 directories; do not run it blindly while another session uses those processes. Establish the exact intended local process/path first and retain its logs/state. Existing authorisation for local verification does not cover unrelated instances or user media.
**Create:** fresh run record containing package SHA-256, host/OS/filesystem/ffmpeg versions, selected fixture IDs, expected outcomes and evidence paths. Do not store credentials.

- [ ] Check localhost:8099 identity/auth and isolated generated-library roots before mutation. Disable unrelated automatic work and restore original test settings in finally. Verify backup/recovery for configuration and copied DBs before upgrade testing.
- [ ] Load the built package and confirm deployed DLL hash matches; inspect plugin load/DI/native dependency logs. Exercise both supported host lines sequentially on the allowed test target, including the minimum declared 10.11 ABI if the current fixture host is newer.
- [ ] Run scanner → approve selected generated IDs → dry-run → real execution → history → restore, checking source/output/restored hashes and expected streams at each stage. Do not bulk-approve unrelated queue entries.
- [ ] Run real audio/subtitle+quality combined case, disable/revoke companion before commit and prove disallowed stream removal does not happen. Test config flip to dry-run during long work.
- [ ] Run repair-required, healthy, DRM/unknown-probe and rename-only cases; output must meet the fixture's declared allowed outcome. Restore recovery bytes and play output through Jellyfin at start/middle/end, with seeks and relevant audio/subtitle selection.
- [ ] Test genuine cross-volume file/directory move and restore on an isolated target where available. Verify source retained on partial copy failure and source/deployed path policy. Same-directory helper tests do not cover real EXDEV/mount/ACL behavior.
- [ ] Run controlled process interruption in the isolated host at a known replacement/restore checkpoint, then restart and verify survivor protection/reconciliation. Never kill an unrelated Jellyfin instance to inject this fault. If checkpoint injection cannot be achieved safely, record this gate blocked and retain deterministic fault-injection evidence separately.
- [ ] Execute full UI matrix from 06-C and anonymous/non-admin/admin HTTP checks. Save test-specific logs and screenshots with identifying details redacted.
- [ ] Record macOS/network-volume/GPU coverage as manual/blocked/not-applicable with reasons. Never infer GPU support from command generation or macOS from a Linux unit pass.

**Per-case result template:**

```text
Case: RG-01 / transcode-final-rename-failure
Source candidate: commit + diff artifact; package and deployed DLL SHA-256
Environment: OS, filesystem(s), Jellyfin, ffmpeg, fixture generator version
Setup: generated fixture IDs, policy, source SHA-256, expected stream inventory
Trigger: exact selected request/fault point
Observed: HTTP/result code, file paths/hashes, DB/history, diagnostics
Recovery: surviving copy, restore SHA-256, restart/sweep behavior
Playback: pass/failed/not run; client, seek/track observations
Verdict: pass/fail/blocked; evidence paths; cleanup result
```

## 07-C — architecture and user handoff

**Modify:** PROGRESS and a short `docs/refactoring.md` describing actual final service responsibilities if the refactor is implemented. Update CLAUDE.md only with durable verified conventions, not speculative future promises. Link new test gates from docs/testing/INDEX.md as appropriate.

- [ ] Re-read user-facing scenarios from the roadmap matrix against final boundaries: preview can consume plan, policy can receive future profiles/protection, retry can consume typed outcomes, history can consume actual effects. These extension points must be understandable without implementing the features.
- [ ] Remove temporary duplicate ownership and dead compatibility adapters where all callers moved. If retained for external compatibility, list owner, consumers and removal condition; do not leave unnamed permanent transition layers.
- [ ] Validate every item in [FILE-LAYOUT.md](FILE-LAYOUT.md), including physical JS/CSS/tab files and the published embedded-resource manifest. Include final file-to-responsibility inventory; logical sections or partial-file shuffling do not meet this gate.
- [ ] Review destructive code for a single responsible protocol, narrow exception handling, honest partial outcomes, source rechecks, no old cleanup deleting recovery and no scheduler replay after recording failure.
- [ ] Verify existing tests remain useful: helpers test decision logic, integration tests prove wiring, media tests prove bytes/streams, browser tests prove interactions. No test asserts only that a moved function still exists.
- [ ] Recheck git status for external changes and report which files belong to this work. Do not bundle another agent's pending fix into a claimed verified candidate without rerunning affected checks.
- [ ] Produce final report with completed chunks, actual API/schema changes, preserved behavior, tests/results, runtime/platform limitations, outstanding defects and explicit readiness. Do not claim completion of unexecuted gates.

Readiness rules:

| State | Handoff |
|---|---|
| Safety defect, false-pass harness, failed build/test or unreviewed overlapping change | Do-not-push; name blocker and next reproducer/action. |
| Local code checks pass but required host/playback/restore gate missing | Do-not-push for release readiness; state exactly which implementation work is complete. |
| Required local/OS/host/media/UI evidence passes on the identified candidate | Safe-to-push for the reviewed candidate; user retains publishing/push control. |

No number of green unit tests alone upgrades the first two rows to the last. A clear limited result is preferable to an unsupported claim that the refactor cannot lose media.
