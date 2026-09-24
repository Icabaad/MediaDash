# MediaDash refactor ownership

The plugin retains its Jellyfin entry points and HTTP/configuration contracts. It uses net9.0 and plain embedded JavaScript/CSS; no frontend build step is required.

## Backend

Scheduled tasks coordinate runs through `RunState`. `FixPlanBuilder` selects work, `FixExecutor` invokes the operation, and `FixResultRecorder` persists outcomes. `FixPolicyEvaluator` rechecks current policy immediately before mutations. Output replacement goes through `VerifiedReplacementService`; recovery precedes disposal. `RestoreService` owns restore claims and collision handling.

The SQLite stores separate issue, history and ancillary query responsibilities. Cross-store transactions remain in `MediaDashDb`. `ConfigChangeRescanner` invalidates affected detected rows and unions rapid saves into one debounced request; a full rescan dominates narrower requests.

## Dashboard

`Configuration/configPage.html` contains markup and the small bootstrap loader. `scripts/bootstrap.js` loads embedded assets through `ApiClient.getUrl`, preserving Jellyfin's base path. CSS and scripts must be embedded by the project and whitelisted by `PluginAssetController`.

`page-controller.js` supplies explicit dependency getters to the Overview, Issues, History, Files, Recycle, Errors, Settings and Wizard factories. Each factory owns its state and rendering. `page-lifetime.js` owns a page visit's generation, timers and cleanup callbacks; `api-client.js` suppresses responses and errors from retired generations. Files uses request sequence numbers for navigation within the same visit.

Settings cannot save before configuration and library checkboxes finish loading. Pending edits block incidental server hydration. File batches capture their source/destination at initiation so later navigation cannot change the user's approved paths.

## Verification

Run the Release build and C# suite, then:

```text
node --test tools/comprehensive-test/ui-lifecycle-self-tests.js tools/comprehensive-test/ui-regression-self-tests.js
pwsh -NoProfile -File tools/comprehensive-test/self-tests.ps1
pwsh -NoProfile -File tools/comprehensive-test/task-wait-self-tests.ps1
pwsh -NoProfile -File tools/repair-test/validator-self-tests.ps1
```

Live media verification must use generated owned fixtures, explicit library scoping, manual approvals and original-byte restore checks. A task is complete only after a new successful execution timestamp; observing Idle alone is insufficient. Never bulk-approve the shared fixture library, which also contains real media.

See the plan's `PROGRESS.md` and `artifacts/refactor/codex-finish/` for this checkout's evidence and unresolved infrastructure gates. Unit tests do not substitute for cross-volume, crash-interruption or host/platform testing.
