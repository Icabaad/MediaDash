# Verified replacement and recovery implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. This is a high-risk package: migrate one fixer at a time and run its fault matrix before proceeding.

**Goal:** Give replacement of an existing media file one auditable protocol while preserving format-specific verification and disposal policy.
**Architecture:** A concrete replacement service with a narrow filesystem seam and verification callback. Existing LibraryGuard, FreeSpaceGuard, OutputVerifier, RecycleBin and SwapRecoveryHelper remain useful collaborators.
**Tech stack:** C#, real temp filesystem fixtures, fault injection, xUnit; later real ffmpeg/Jellyfin validation.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md). Requires 01 and 02-B. All global constraints apply.

## 03-A — protocol and failure seam

**Read:** TranscodeFixer replacement/finally; TrackFixer replacement/finally and external sidecars; EmbeddedCoverArtFixer replacement; PlayabilityFixer.TrySwapRepairedAsync; LibraryGuard.SweepOrphanSidecars; SwapRecoveryHelper; SidecarPath if present.
**Create:** `P/Fixers/VerifiedReplacementService.cs`, `ReplacementRequest.cs`, `ReplacementResult.cs`, `ReplacementFileOperations.cs`; `T/VerifiedReplacementTests.cs`.
**Do not migrate a production caller until fault tests below pass.**

Suggested contract (define types before consumers; keep constructor ownership explicit):

```csharp
internal sealed record ReplacementRequest(
    string SourcePath, string OutputPath, string DestinationPath,
    long ExpectedSourceLength, long ExpectedSourceMtimeUtcTicks,
    bool RecycleOriginal);
internal sealed record ReplacementResult(bool Applied, FixReason Reason,
    string? RecyclePath, string? RecoveryPath, string? TechnicalDetail);
// ReplaceAsync takes ReplacementRequest,
// Func<CancellationToken, Task<string?>> verifyOutput (null means valid),
// Func<PolicyDecision> checkCurrentPolicy, and CancellationToken.
// It returns ReplacementResult; success factories in the caller retain media-specific messages.
```

Length+mtime is a useful stale-source guard, not cryptographic identity. Capture it before producing output and recheck immediately before commit. For a full threat model involving hostile same-size/mtime replacement or symlink swaps, stronger handle/identity checks may be needed; document limits rather than claiming race freedom from a pre-check.

File seam should expose only operations actually needed to inject commit faults: move without overwrite, delete owned temp, existence/type check and metadata read. Keep recycle via RecycleBin; do not rebuild its cross-volume behavior in a generic filesystem framework. Default implementation uses real System.IO. Tests should mostly use real temp files with a wrapper that fails the Nth named operation.

- [ ] Build a protocol table for each current caller: source/destination relationship, output path location, recycle/permanent behavior, timestamps, verification and notifications. Playability currently recycles repaired originals regardless of general failed-repair removal mode: preserve this deliberate difference.
- [ ] Define ownership: service may remove only its own output/staging files; it cannot delete unrelated same-name temp files. Use unique collision-safe naming compatible with current NAME_MAX/Unicode protections.
- [ ] Refuse invalid/outside-library destinations, missing source, source/output alias, directory-as-file collision and preexisting destination other than the intended original slot. Check file and directory existence. Preserve final no-overwrite behavior when destination differs from source.
- [ ] Sequence: verify output; recheck source identity/current policy; stage output on destination volume; confirm staged output is the verified one; dispose original according to explicit request; move staged output into place; return recovery references. If copy is required between volumes, copy fully and validate the copied bytes before deleting any source.
- [ ] Keep notification/history out of this protocol's success decision: a failed library refresh after successful replacement must not trigger a second destructive replacement. Caller records notification failures separately.
- [ ] Cancellation before original disposal aborts safely. After disposal begins, finish the minimal commit-or-recovery sequence despite cancellation, then report actual effects. Never throw cancellation from finally and erase the only valid output.
- [ ] On final move failure, preserve staged output and/or recover original where safe. If recovery rename itself fails, protect the actual remaining staging path from sweep; a diagnostic mentioning a path is not durable protection. Integrate current sweep protections and test an aged survivor through cleanup and restart.
- [ ] If durable recovery evidence is absent in the current mechanism, make conservative cleanup preserve uncertain files. A new journal/schema is a separate justified slice, not an assumed feature. No automatic replay of destructive operations from incomplete metadata.

Fault matrix — tests compare sentinel bytes, paths, outcome and recovery references:

| Fault point | Required result |
|---|---|
| Verification fails, throws, times out, or cancels | Original unchanged; no disposal; owned output handled safely; non-success. |
| Source changes after output production | Refuse stale output; changed source remains. |
| Policy/dry-run changes after encoding | No replacement; current original preserved. |
| Destination appears before commit | No overwrite of that unrelated file/directory; source recoverable. |
| Stage move/copy fails or partial copy fills disk | Source intact; incomplete owned output cannot be presented as success. |
| Recycle/disposal fails | Source remains if still present; staged output not lost if disposal partially succeeded. |
| Final rename fails after disposal | Recoverable source or verified output survives; typed RecoveryRequired with actual path. |
| Recovery rename fails; then orphan sweep runs after age cutoff | At least one valid copy remains; sweep cannot delete uncertain recovery evidence. |
| Cancel after disposal | Commit or recovery completes; result describes actual effect, no false Preview. |
| Success, same path and different extension | Output bytes correct; source disposal policy honored; no unrelated temps removed. |
| Unicode near NAME_MAX / directory at recovery candidate | Unique bounded recovery name; no data loss. |

Useful test shape, using the test wrapper you implement:

```csharp
// Seed source and verified output with distinct known bytes.
// Configure the real-files wrapper to throw on the final destination move.
var result = await service.ReplaceAsync(request, _ => Task.FromResult<string?>(null),
    () => new PolicyDecision(true, FixReason.None), CancellationToken.None);
Assert.False(result.Applied);
Assert.Equal(FixReason.RecoveryRequired, result.Reason);
Assert.NotNull(result.RecoveryPath);
Assert.Equal(expectedOutputBytes, File.ReadAllBytes(result.RecoveryPath!));
// Invoke the real sweep with the owned survivor aged past its cutoff; assert bytes again.
```

Use an injected failure at a named step, not wall-clock timing. A plain dummy verifier proves commit logic only; media verification is tested in 03-B/C and 07.

## 03-B — migrate TranscodeFixer only

**Modify:** `P/Fixers/TranscodeFixer.cs`, DI registration; existing Transcode tests.
**Create:** `T/TranscodeReplacementIntegrationTests.cs`.
**Consumes:** 03-A service plus 02 policy check. **Produces:** transcode-specific verified output and typed effect/result, without its own swap/finally protocol.

- [ ] Route existing OutputVerifier through callback; preserve larger-output rejection, selected target path, timestamps, sidecar handling and metadata behavior.
- [ ] Pass exact disposal setting and current source identity. Preserve configured encoder/container decisions. Do not change ffmpeg options as part of moving commit code.
- [ ] Translate ReplacementResult into FixResult with recovery technical detail, byte accounting and no invented success when only recovery remains.
- [ ] Remove only the redundant transcode commit/finally code. Do not remove final library/stream guards that protect a different boundary.
- [ ] Run all Transcode, FreeSpace, OutputVerifier and recovery tests. Add one whole-fixer fixture showing policy changes while fake encoding is paused before commit; no source mutation occurs.
- [ ] Run a generated real-media transcode/restore case locally after fixture preflight; compare stream inventory, output decode and restored source hash. If live environment unavailable, keep this gate pending and do not claim caller runtime proof.

**Gate:** transcode's existing behavior and recovery are proven; no other fixer migrated yet.

## 03-C — Track, cover art, then repair

**Modify:** TrackFixer, EmbeddedCoverArtFixer, PlayabilityFixer, DI; relevant tests.
**Create:** `T/TrackReplacementIntegrationTests.cs`, `CoverArtReplacementIntegrationTests.cs`, extend PlayabilityFixerRepairTests.

- [ ] Migrate TrackFixer first. Embedded track verification and external subtitle disposal are separate effects; don't delete sidecars because a primary remux failed. Preserve AdditionalRecycled rows and current one-pass behavior.
- [ ] Test MP4 audio incompatibility, subtitle whitelist aliases, last-audio/video, empty/unknown metadata and combined-consent changes. Compare expected mapped streams and actual sample output.
- [ ] Migrate EmbeddedCoverArtFixer. Preserve audio stream/cover semantics, scanner/fixer existing-cover parity and timestamp behavior. Test existing external cover appears during processing and output verification failure.
- [ ] Migrate Playability repair swaps with its stronger structural/decode checks and always-recycle repaired-original policy. Healthy and Indeterminate still bypass repair. Rename-only mismatch remains a separate byte-preserving move operation, not a transcode.
- [ ] Apply the shared failure matrix to each adapter using parameterized tests where semantics match. Keep caller-specific policy tests separate where semantics intentionally differ.
- [ ] Search migrated callers for duplicated disposal/final-swap/catch-cleanup logic. Remove dead paths only after all intended callers use the service. Do not route thumbnail optimization or pure file moves through media-replacement semantics without a separately demonstrated match.

**Gate:** all four intended callers have a tested production path through the service; sweep cannot delete any documented recovery outcome; real codec/restore evidence is linked separately from synthetic file-operation tests.
