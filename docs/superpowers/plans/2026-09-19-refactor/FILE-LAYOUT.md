# Required physical file layout

This is part of the [master refactor plan](../2026-09-19-mediadash-refactor.md), added at the user's explicit request. It overrides the earlier proposal to finish the UI extraction with all JavaScript still inside configPage.html. The implementation remains plain JavaScript and embedded plugin assets, with no required frontend build pipeline.

## Rules

1. Each concrete scanner lives in `Scanners/<ScannerName>.cs`; each concrete fixer lives in `Fixers/<FixerName>.cs`. Keep the existing names and responsibilities unless a separate tested design decision changes them. Most already follow this convention; do not churn or duplicate them.
2. Each service, controller, shared helper, interface, enum, record and DTO has one matching named `.cs` file. Group by responsibility/folder, not giant `Models.cs`, `Helpers.cs`, `Services.cs` or `AllScanners.cs` files.
3. Put real shared behavior in its own collaborator. Splitting a giant class into `*.part1.cs`/`*.part2.cs`, `#region`s or partial files solely to reduce line count does not establish ownership. Retain `partial` where required for GeneratedRegex/source generation.
4. A tiny genuinely private nested value carrier or native interop struct may remain with its sole owner; document that exception in the layout inventory. A nested class containing independent behavior, such as a compatibility adapter, should become a separately named internal class. Do not make private internals public just to move them.
5. Test fixtures follow the same convention: separate named test classes/files and small explicit shared fixture helpers. Do not place hundreds of new tests in a catch-all refactor test class.
6. Preserve namespaces during mechanical moves wherever possible. File path does not require namespace churn. Changing nested types can change reflection names/accessibility; update all legitimate callers/tests and verify no external contract depends on them.
7. Every new source file must be compiled/embedded and used. An unused extracted copy next to the original does not satisfy this plan. No duplicate scanner/fixer registration or lost registration.

## Preserve the existing scanner/fixer files

The inventory at this update already includes these concrete classes as separate files:

| Scanners | Fixers |
|---|---|
| ArtworkScanner.cs | ArtworkFixer.cs |
| AudioLanguageScanner.cs | DuplicateFixer.cs |
| DuplicateScanner.cs | EmbeddedCoverArtFixer.cs |
| EmbeddedCoverArtScanner.cs | MediaGrouperFixer.cs |
| MediaGrouperScanner.cs | MediaSorterFixer.cs |
| MediaSorterScanner.cs | MissingSubtitleFixer.cs |
| MissingSubtitleScanner.cs | NfoFixer.cs |
| NfoScanner.cs | OrphanCleanupFixer.cs |
| OrphanCleanupScanner.cs | PlayabilityFixer.cs |
| PlayabilityScanner.cs | SubtitleFontFixer.cs |
| QualityScanner.cs | SuspiciousFileFixer.cs |
| StaleContentScanner.cs | TrackFixer.cs |
| SubtitleFontScanner.cs | TranscodeFixer.cs |
| SubtitleLanguageScanner.cs | TrickplayOptimizeFixer.cs |
| SuspiciousFileScanner.cs | |
| TranscodeLogScanner.cs | |
| TrickplayOptimizeScanner.cs | |

There is intentionally no one-to-one scanner/fixer pairing: TrackFixer handles multiple track issue types, and stale-content detection does not imply permission to add a destructive fixer. File organisation must not create new features or split a combined media operation into multiple ffmpeg passes.

## Known mixed-file moves

Perform these mechanical moves in 00-D, with unchanged namespaces, values and behavior:

| Current owner | Move to | Keep |
|---|---|---|
| `Api/RecycleBinItem.cs` | `Api/RecycleProvenance.cs` | RecycleBinItem in its original file. |
| `Fixers/RevalidationResult.cs` | `Fixers/RevalidationVerdict.cs` | RevalidationResult in its original file. |
| `Scanners/DuplicateSignals.cs` | `Scanners/ConfidenceTier.cs` | DuplicateSignals, including required partial/regex support. |

During the relevant package, also evaluate these nested helpers rather than blindly moving every nested declaration: `StaleContentScanner.UserApiBridge`, `TranscodeLogScanner.Aggregate`/`LogSummary`, `MediaGrouperScanner.MovieCandidate`, and format-specific result records. UserApiBridge is a strong candidate for `Compat/UserApiBridge.cs` because it owns host-version adaptation. A private, passive value record can remain nested under rule 4. Inventory current source afresh; these examples are not exhaustive.

## Resulting responsibility map

The following is a target layout, not an instruction to pre-create unused classes. The package introducing each responsibility also creates its file, tests and DI wiring. Existing unrelated files remain intact.

```text
Jellyfin.Plugin.MediaDash/
  Plugin.cs                         # plugin identity/startup/page registration
  PluginServiceRegistrator.cs        # dependency wiring
  Scanners/
    IScanner.cs
    ProbingScannerBase.cs
    <each existing scanner>.cs
    <each shared scanner helper>.cs
  Fixers/
    IFixer.cs
    <each existing fixer>.cs
    FixOutcome.cs
    FixReason.cs
    QueueDisposition.cs
    FixResult.cs
    FixResultPolicy.cs
    FixIntent.cs
    PolicyDecision.cs
    FixPolicyEvaluator.cs
    VerifiedReplacementService.cs
    ReplacementRequest.cs
    ReplacementResult.cs
    ReplacementFileOperations.cs
    RestoreService.cs
    RestoreKind.cs
    RestoreRequest.cs
    RestoreOutcome.cs
    RevalidationVerdict.cs
    RevalidationResult.cs
  ScheduledTasks/
    ScanTask.cs
    FixTask.cs
    FixPlanBuilder.cs
    PlannedFixKind.cs
    PlannedFix.cs
    FixExecutor.cs
    FixExecutionResult.cs
    FixResultRecorder.cs
    RunState.cs
    RunSnapshot.cs
  Configuration/
    PluginConfiguration.cs
    IPluginConfigurationProvider.cs
    PluginConfigurationProvider.cs
    configPage.html                  # markup and minimal bootstrap only
    styles/
      mediadash.css                  # shared visual tokens/layout
      responsive.css                # breakpoint rules, loaded after base
    scripts/
      bootstrap.js                  # explicit dependency loading/mount
      api-client.js
      page-lifetime.js
      page-controller.js
      settings-mapper.js
      wizard.js
      tabs/
        overview-tab.js
        issues-tab.js
        history-tab.js
        files-tab.js
        recycle-tab.js
        errors-tab.js
        settings-tab.js
    i18n/                           # retain existing locale resources
  Data/
    MediaDashDb.cs                   # transitional facade, no duplicate SQL
    MediaDashConnectionFactory.cs
    MediaDashSchema.cs
    IssueRowReader.cs
    HistoryRowReader.cs
    IssueStore.cs
    HistoryStore.cs
    ProbeCacheStore.cs
    DiagnosticStore.cs
    IssueDetailsReader.cs
    PlayabilityDetails.cs
    SubtitleCleanupDetails.cs
    <each existing entity/enum>.cs
  Api/
    MediaDashController.cs           # HTTP adapter; workflows live in services
    FileBrowserController.cs
    StatusQueryService.cs
    RecycleProvenance.cs
    <each request/response DTO>.cs
  Diagnostics/
    DiagnosticService.cs
    <sink interface, if needed>.cs
  Compat/
    UserApiBridge.cs                 # if moved, retain reflection-only host bridge
    <existing compatibility helpers>.cs
```

If a capability such as system stats remains large after extracting its independent responsibilities, split its collaborators into named files too. Do not mechanically create a controller per route or a class per method. The objective is that someone can find one responsibility, its dependencies and its tests without navigating a monolithic implementation.

## UI loading and packaging are part of the split

Source assets must remain separate in the repository and be included as embedded resources in the plugin. Verify supported Jellyfin resource loading on both host lines; physical relative paths beside the HTML are not automatically web URLs. Use a narrowly scoped, registered embedded-resource mechanism, preserving the configuration page's identity and host authentication expectations. Do not add a general filesystem-serving endpoint.

Each tab's real implementation belongs in its own asset; empty wrappers loading the old inline implementation fail acceptance. Keep only minimal bootstrap inline if host lifecycle requires it. Load dependency assets deterministically, handle missing assets visibly, and avoid re-registering modules/listeners on page navigation. Base-path deployments, correct MIME types, cache invalidation and content-security restrictions must be tested. If separate asset loading is blocked, report the gate incomplete instead of silently retaining the monolithic page.

## File-layout acceptance checklist

- [ ] Every concrete scanner and fixer has exactly one owning source file and retains its intended DI registration and issue-type handling.
- [ ] Every top-level production type has its own named file; private nested exceptions are explicit and justified.
- [ ] Shared services contain the implementation; old callers delegate rather than retaining copied logic.
- [ ] Generated-regex partial declarations, access modifiers, enum numbers and serializer field names remain valid.
- [ ] HTML, CSS, networking/lifecycle, wizard/settings mapping and each dashboard tab are physically separate assets.
- [ ] Published assembly contains every required resource, and both supported hosts load the page without missing/duplicate assets.
- [ ] Existing code tests plus package media/UI/compatibility gates pass after the moves.
- [ ] Final handoff includes a file-to-responsibility inventory and any documented exceptions.
