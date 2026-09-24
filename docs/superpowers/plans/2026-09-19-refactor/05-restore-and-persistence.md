# Restore workflows and persistence implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Keep migration/readers, restore behavior and store extraction in separate chunks.

**Goal:** Centralise restore safety and simplify database responsibilities without weakening conditional writes or upgrade compatibility.
**Architecture:** Retain MediaDashDb as a compatibility facade while extracting migrations/readers, then introduce a restore service shared by endpoints and focused stores only where they reduce coupling.
**Tech stack:** C#, Microsoft.Data.Sqlite, xUnit, real generated filesystem fixtures.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md). Requires 01, 02, 03 and 04-B. All global constraints apply.

## 05-A — migrations and row readers

**Modify:** `P/Data/MediaDashDb.cs`.
**Create:** `P/Data/MediaDashSchema.cs`, `IssueRowReader.cs`, `HistoryRowReader.cs`; `T/SchemaUpgradeTests.cs`, `RowReaderCompatibilityTests.cs`.
**Read tests:** MediaDashDbTests, MalformedDbRowsDoNotCrashTests, MalformedTimestampTests, OutOfRangeEnumHydrationTests, RestoredPathsTests, IssueRevertTests.
**Contract:** existing schema creation, user_version progression, row-shape tolerance and enum values remain equivalent. No new schema version merely for moving code.

- [ ] Inventory every schema version transition, including deliberate cache/issue invalidations. Preserve their order and one-time semantics. Current constructor creates tables before MigrateSchema; don't inadvertently run ALTER against missing tables or rerun data cleanup every startup.
- [ ] Create small synthetic historical DB fixtures in tests rather than depending on a developer's audit DB with unknown provenance. Include prior schemas with live history, restored signals, cached probes and malformed rows.
- [ ] Extract migration routines with a single connection and documented transaction boundaries. Read current SQLite behavior before wrapping everything in one transaction: VACUUM/PRAGMA behavior is not interchangeable with ordinary DDL. Run this against copies only.
- [ ] Extract readers by row type, preserving projection column order and enum/Guid/date validation. A valid row after a malformed row must still appear. Preserve the existing distinction between skipping a malformed row and substituting a bounded invalid timestamp; don't change data semantics casually.
- [ ] Ensure diagnostic logging on bad rows uses a safe path that does not recurse into the failing DB. Include table/id in details without leaking sensitive paths to unrelated output.
- [ ] Compare public query results before/after extraction for representative good/malformed DBs; run each upgrade twice to prove idempotence.

Example reader test contract:

```csharp
// Create a real temp DB with a bad enum row between two valid rows.
var issues = db.GetIssues();
Assert.Equal(new[] { goodFirstId, goodLastId }, issues.Select(x => x.Id));
// Reopen the same file: schema version and good data must remain stable.
```

**Tests:** fresh install; every supported prior version; twice-opened DB; failed migration leaves recoverable original/copy; non-object DetailsJson; invalid Guid/type/status/ticks; valid rows survive; history counters use the same exclusions. Test public read/query paths, not only the reader helper in isolation.

## 05-B — one restore workflow, honest claim state

**Modify:** `P/Api/MediaDashController.cs` RestoreFromHistory/RestoreOptimizedCopy and manifest-only restore routes; `P/Fixers/RecycleBin.cs` restore/purge/consolidation coordination; `P/Data/MediaDashDb.cs`; DI.
**Create:** `P/Fixers/RestoreService.cs`, `RestoreRequest.cs`, `RestoreOutcome.cs`; `T/RestoreServiceTests.cs`, `RestoreConcurrencyTests.cs`, `RestoreRecoveryTests.cs`.
**Read:** RestoreClaimTests, DirectoryRestoreTests, RestoreSuffixedProtectionTests, RestoreResolveNonCollidingPathTests, RecycleBinOriginManifestTests and existing optimized-restore tests.

Important current asymmetry: `RestoreOptimizedCopy` calls TryClaimRestore; `RestoreFromHistory` does not. Current TryClaimRestore uses `history.restored = 1` as the claim, and ReleaseRestoreClaim resets it without an owner token. **Do not merely move these methods into a service and claim races/crash recovery are solved.** Characterize both paths first.

```csharp
internal enum RestoreKind { History, OptimizedCopy, ManifestEntry }
internal sealed record RestoreRequest(RestoreKind Kind, long? HistoryId,
    string? ManifestEntryPath, bool Force);
internal sealed record RestoreOutcome(bool Success, FixReason Reason,
    string? RestoredTo, bool Suffixed, string? TechnicalDetail);
// RestoreService handles validation/claim/file operation/final DB state/notifications.
// Controller retains exact route parameters and maps outcome to existing HTTP contracts.
```

Perform these as small substeps within 05-B; if a schema change is needed, give it a separate review/checkpoint before migrating endpoints:

- [ ] Define entry identity shared across history and manifest routes. Multiple history rows can reference the same recycled file; row ID alone cannot serialize physical access. Canonical bin-entry identity and directory/batch relationships must be considered.
- [ ] Require library-boundary validation for destination and trusted registered bin-root/manifest validation for source. History data and manifest paths are not authorisation. Guard symlinks/junctions and both file/directory collisions through existing boundary helpers.
- [ ] Acquire an owned operation claim before disposing a force-restore destination. Make purge/delete/consolidate honor the same active entry/batch claim. Short acquisition lock/transaction only; no DB write lock held while copying media.
- [ ] Separate in-progress from completed restore. Default recommendation is an additive operation table if current persistence cannot distinguish them. Keep `history.restored` meaning completed, set it only after actual success. Store claim owner and known source/destination so process death becomes reviewable.
- [ ] Scope any additive table to present restore correctness. A minimal design is `restore_operations(operation_id TEXT PRIMARY KEY, entry_key TEXT UNIQUE, history_id INTEGER NULL, source_path TEXT, destination_path TEXT, state TEXT, started_utc_ticks INTEGER, detail TEXT NULL)`, with states `Pending`, `Completed`, `NeedsReview`. Use parameterised SQL, owner-matched updates/releases, migration tests, and explicit cleanup of completed operation records. A completed claim cannot permanently block recycling/restoring a later distinct entry.
- [ ] On failure before mutation, release only the owned claim so retry remains possible. On partial mutation, retain NeedsReview and recovery paths; do not clear ownership and let another request blindly repeat the force swap. On restart, Pending becomes a reconciliation candidate; inspect known paths, preserve uncertain data and never automatically repeat destructive moves.
- [ ] During normal success, move the original or choose collision suffix; force restore first recycles the occupying file and gives it its own restorable history. Complete DB restore/protection updates in a short transaction; protect both original and suffixed destination under current autoqueue semantics.
- [ ] On DB failure after successful filesystem restore, report actual restored path and incomplete bookkeeping. Do not return a generic retriable failure that invites another destructive force operation. Preserve operation evidence for reconciliation.
- [ ] Preserve directories and Unicode/long-name handling. Ensure final filesystem move refuses an intervening collision even if suffix selection found the path free earlier.
- [ ] Keep library notifications after mutation; notification failure is diagnostic, not permission to redo the restore. Exact public response compatibility remains a controller test.
- [ ] Migrate one endpoint, validate, then the other and manifest path. Remove old side-effecting controller code only after equivalent tests pass.

**Required concurrency/recovery tests:**

| Case | Assertions |
|---|---|
| Two requests for same row | Exactly one owned operation performs file mutation. |
| Different rows/routes reference same bin entry | Still only one physical restore. |
| Restore vs purge/delete/consolidate same batch | Claim-aware operation refuses/waits; no removed active source. |
| Force destination displaced, restore then fails | Displaced bytes and original bin bytes remain recoverable; explicit NeedsReview. |
| Process death after claim / after move / before DB completion | Restart preserves evidence and does not replay destructive work; completion inferred only from sufficient evidence. |
| Old owner releases after another operation begins | Owner mismatch cannot clear newer claim. |
| Occupied file/directory; Unicode bounded suffix | No overwrite, actual destination returned, both paths protected. |
| Poisoned row/manifest points outside allowed roots | Refused before file changes; no arbitrary write. |
| DB write or notification fails after move | Accurate actual-effect report; no automatic second restore. |

**Gate:** both restore workflows use one safety owner, old endpoint contracts still pass, recovery is demonstrated on copied DBs/generated files. This justified safety correction may require a schema migration; document it explicitly rather than hiding it as a file move.

## 05-C — focused stores and controller queries

**Modify:** MediaDashDb facade, current service consumers and controller aggregation.
**Create only as used:** `P/Data/MediaDashConnectionFactory.cs`, `IssueStore.cs`, `HistoryStore.cs`, `ProbeCacheStore.cs`, `DiagnosticStore.cs`; `P/Api/StatusQueryService.cs` if GetStatus aggregation warrants extraction; relevant contract tests.
**Contract:** one database, existing schema/query semantics; services depend only on necessary operations. No generic repository abstraction or ORM conversion.

- [ ] Extract connection creation/busy/PRAGMA settings first; retain disposal and pooling behavior. Do not open an independent connection for each operation inside the recorder's required transaction.
- [ ] Move issue transitions/queue selection together, history/restore metadata together, and caches together. Provide a deliberate transaction entry point for cross-store recording instead of exposing raw connections across layers.
- [ ] Retain MediaDashDb forwarding methods while migrating each consumer family. Don't duplicate SQL in both facade and store. Avoid an interface if a concrete narrow service and real temp DB tests suffice.
- [ ] Move status/library/history aggregate composition out of the controller where it mixes volume and DB lookups; keep DTO mapping and auth/status handling in HTTP layer. Query optimization or pagination changes need separate evidence, not accidental alterations.
- [ ] Remove facades only when no production callers depend on them and public compatibility permits removal. Keep functions named for domain operations rather than exposing generic SQL execution.
- [ ] Test transaction rollback, parallel reads/writes, malformed partial results, lifetime totals and diagnostics persistence. Include existing schema-upgrade and package startup tests.

**Gate:** smaller responsibility owners with no loss of atomic transitions, no new DB connection leaks/locks, unchanged response contracts. Fewer lines in MediaDashDb alone is not acceptance evidence.
