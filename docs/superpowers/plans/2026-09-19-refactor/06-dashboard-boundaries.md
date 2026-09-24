# Dashboard lifecycle and component boundaries implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` if available. Migrate one tab at a time and retain the existing visual design.

**Goal:** Reduce coupling between requests, page lifecycle, tab rendering and settings without changing the embedded-page deployment model.
**Architecture:** Plain JavaScript factories with a page controller owning lifecycle/state and separately implemented tab components. In-page extraction in 06-A/B/C is transitional; 06-D must move implementations into separate embedded JavaScript/CSS assets according to [FILE-LAYOUT.md](FILE-LAYOUT.md).
**Tech stack:** Existing HTML/CSS/JavaScript and Jellyfin emby components; browser interaction tests plus isolated JavaScript lifecycle tests.
**Spec:** [Master](../2026-09-19-mediadash-refactor.md); [UI regression cases](../../../testing/REGRESSION-COVERAGE-REVIEW-2026-09-19.md). Requires 00-B and 02-A; execute after 05 to avoid simultaneous API/UI churn.

## 06-A — lifecycle, networking and polling

**Modify:** `P/Configuration/configPage.html` near API helpers, refresh, schedulePoll, page show/hide handlers and global listeners.
**Create:** `tools/comprehensive-test/ui-lifecycle-self-tests.js` or equivalent test file using an available JS runtime; extend `phases/07-ui.ps1` interaction cases.
**Contract:** one active poll cycle per mounted page; no stale response renders after hide/reopen or query changes; every owned timer/listener has a disposer.

Current pagehide clears pollTimer, but there are other setTimeout chains and document/window handlers. Characterize actual Jellyfin reuse/remount events first; don't assume React-style unmount behavior.

Proposed in-page shape:

```javascript
function createPageLifetime() {
    var active = false, generation = 0, cleanups = [];
    return {
        start: function () { active = true; generation++; return generation; },
        current: function () { return generation; },
        accepts: function (token) { return active && token === generation; },
        own: function (cleanup) { cleanups.push(cleanup); },
        stop: function () {
            active = false; generation++;
            var pending = cleanups; cleanups = [];
            pending.forEach(function (cleanup) { cleanup(); });
        }
    };
}
```

Use named listeners so cleanup can remove them. A page generation guards hide/reopen; each tab query also needs a latest-request sequence so two in-page requests cannot render out of order. Where ApiClient cannot abort a request, ignore stale results; do not claim cancellation when only rendering is suppressed.

- [ ] Capture current page initialization/show/hide behavior with a browser and record request count while opening/closing three times. Assert behavior, not exact implementation text.
- [ ] Make one API adapter for plugin routes and consistent error translation; preserve authentication, HTTP status and technical detail. Avoid converting 401/403 into empty successful arrays. Settings calls through ApiClient remain compatible.
- [ ] Add lifetime and per-query request tokens. Hide invalidates pending responses; reopening creates a new generation. Poll scheduling only occurs while active, in one place, after the relevant request completes/fails.
- [ ] Track all timers/listeners owned by migrated page code, including global click/keydown/scroll handlers and long-press timers. Dispose safely even if one cleanup throws; final implementation must not leave the remaining cleanups unexecuted.
- [ ] Add fake-clock/deferred-promise tests for duplicate start, hide during request, reopen, slow old request finishing after newer request, rejected request and timer disposal. Include successful polling control case.

Example lifecycle assertions:

```javascript
var life = createPageLifetime();
var old = life.start();
console.assert(life.accepts(old));
life.stop();
var fresh = life.start();
if (life.accepts(old) || !life.accepts(fresh)) throw new Error('stale lifecycle token');
```

Use throwing assertions in the actual test runner; `console.assert` alone may not produce a failing process exit. Prefer loading the actual function from the page in the test/browser rather than copying its implementation into a test and testing only the copy.

**Gate:** repeated navigation creates no multiplied polling/listeners; stale responses never overwrite current tab/filter state; error visibility and API credentials remain correct.

## 06-B — tab ownership

**Modify:** configPage renderIssues/filter functions, renderHistory, recycle loaders, Errors rendering and showTab. Leave CSS/markup IDs stable initially.
**Tests:** extend real browser phase with tab-specific assertions; isolated tests for pure filtering/formatting where practical.
**Contract:** a tab receives root/API/strings/read-only relevant state; owns its controls and local filter state; exposes activate, render, deactivate/dispose. Shared page state is passed deliberately, not accessed through every closure.

```javascript
// Contract for factories implemented inside the embedded script:
// createHistoryTab({ root, api, strings, lifetime }) returns
// { activate: function(){}, render: function(rows){}, dispose: function(){} }.
// Each method must have behavior and tests when introduced; do not add empty shells for every tab.
```

- [ ] Start with History because filename-only search and row rendering provide a bounded slice. Preserve current savings/grouping behavior, technical expansion and restoration actions.
- [ ] Extract Recycle bin next, retaining filename-only filter (including the existing CSS display override where needed), directory rows, disabled controls, force/non-force restore consequences and actual restored path display.
- [ ] Extract Issues filters/actions and Errors disclosure separately. Keep counts and 999+ cap as presentation; don't silently change server totals or dedup semantics.
- [ ] For every tab, route mutations through API adapter and refresh affected tabs explicitly. A History restore may invalidate Issues/Bin; model that as a specific refresh event/callback, not a global reload that loses settings edits.
- [ ] Check hostile filenames/diagnostic text render as text or through existing escaping. Do not introduce raw innerHTML interpolation while extracting templates. Test `<img onerror=...>` as harmless text with no script execution.
- [ ] Keep keyboard focus, expanded rows and filters through nonstructural refreshes where current behavior requires it. Dispose global handlers/timers when leaving the page.
- [ ] Re-run the affected tab's viewport/theme checks before migrating the next tab. Do not redesign styles in the same diff.

**Tests:** empty/loading/error/populated state; long Unicode filename; filename-only search does not match parent path; restore busy/error/success; repeated clicking cannot enqueue duplicate requests unintentionally; selected filters survive polling; technical details available without raw exception primary text; old request cannot repopulate previous filter results; keyboard activates actions and focus stays visible.

## 06-C — settings/configuration mapping

**Modify:** loadSettings, saveSettings, wizard/save paths and fix-order controls in configPage; no config schema change.
**Create tests:** pure config-form mapping tests and real persistence/reload tests for affected controls.
**Contract:** form editing does not mutate live server configuration until save; save preserves fields not owned by the current form; server-side disable/re-enable remains authoritative.

- [ ] Extract config-to-form and form-to-config mapping from DOM wiring. Define ownership of each property, including arrays, absent legacy fields and unknown future fields.
- [ ] Maintain original config object for merging unowned fields; don't replace complete config with a small partial object unless server API explicitly supports patches. Preserve null/default handling.
- [ ] Characterize dirty-state tracking, saved status, async save failure and wizard resume. Slow poll response must not overwrite unsaved edits.
- [ ] Test disabling SMART/fixers persists across page and server restart, stops server work, and re-enabling works symmetrically. A hidden UI row alone is not disablement.
- [ ] Retain confirmation semantics for existing destructive settings and button wording. Refactoring must not remove current explicit consent or add extra confirmation for harmless actions.
- [ ] Validate 360/390/430 px phones and 1280/1920 desktop, light/dark, long translated labels, keyboard focus and all seven tabs. Save screenshots plus browser errors/request observations, not only screenshots.

**06-C gate:** no new frontend framework/build step; current functionality/visuals preserved; timer/request/listener ownership is explicit. Continue to 06-D; in-page components alone do not satisfy the user's physical file split.

## 06-D — separate embedded JavaScript and stylesheet assets

**Modify:** `P/Configuration/configPage.html`, `P/Plugin.cs` page/resource registration, production csproj EmbeddedResource entries.
**Create:** the styles/scripts/tab files in [FILE-LAYOUT.md](FILE-LAYOUT.md), plus `T/EmbeddedAssetManifestTests.cs` and browser loading checks.
**Contract:** repository and published resources contain separate real implementations; configPage.html is markup with minimal bootstrap. Existing page identity, API routes, visual behavior and plain-JS deployment are preserved.

- [ ] Prove resource loading with one non-destructive asset on the actual supported host mechanism. Inspect existing host APIs/registration rather than guessing that relative disk paths resolve. Test base-path deployment and both host lines before migrating all assets.
- [ ] Add explicit embedded-resource entries and deterministic bootstrap dependencies. Map public asset names to a fixed resource allow-list if a resource adapter is necessary; never expose arbitrary paths or assembly resources.
- [ ] Move API/lifetime/controller implementations first, then one tab per small diff, then wizard/settings mapping. Include Overview and Files; do not leave their large implementation inline while marking the split complete.
- [ ] Move CSS preserving cascade/source order. Put common tokens/layout in mediadash.css and responsive overrides in responsive.css; preserve theme selectors and the filename-filter display override.
- [ ] Keep only one intentional application namespace or host-compatible module registration mechanism. No global registration per refresh, no eval/inline copied script injection as a substitute for loading the actual asset.
- [ ] Add assembly tests asserting every registered JS/CSS resource exists and public names are unique. Add browser tests for correct MIME, dependency order, missing-asset error, repeated page navigation and version/cache refresh after upgrade.
- [ ] Run full viewport/theme and tab interaction matrix with separate assets loaded from the deployed plugin. Remove the original inline implementations/styles only after migrated equivalents are active; verify no duplicate handlers/rules remain.

**Exit:** physical UI split and packaging/runtime tests pass. If resource delivery cannot be verified, mark 06-D blocked; do not fall back to a completed single-file design.
