// MediaDash Errors tab factory. Extracted from configPage.html on 2026-09-23
// (post-06-D follow-up 1b) as the first full-DOM tab factory. Same 06-D pattern
// as the pure helper assets: PluginAssetController serves it, configPage.html
// consumes via <script src>, self-tests load it via fs+vm.
//
// Body is byte-identical to the previously inline createErrorsTab body — only
// wrapping (IIFE + namespace registration) and dependency plumbing changed:
//   - `esc` (5 uses) is now resolved through window.MediaDash.esc via the local
//     alias below rather than closing over the outer IIFE's alias.
//   - `errorMeta`, `updateErrorBadge`, `refresh` (previously closed over from
//     the outer IIFE) are now taken from deps at the call site — the runtime
//     wiring in configPage.html adds them to the deps bag alongside the
//     existing root/api/strings/lifetime keys.
//   - `ApiClient` (Jellyfin's global) is used unchanged.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    ns.MediaDash.createErrorsTab = function createErrorsTab(deps) {
        var root = deps.root, api = deps.api, strings = deps.strings;
        var errorMeta = deps.errorMeta, updateErrorBadge = deps.updateErrorBadge, refresh = deps.refresh;
        var esc = ns.MediaDash.esc;
        // Per-tab loadSeq layered on top of the wrapped api()'s lifetime
        // token — protects against a `refresh()` poll and a user-initiated
        // load racing within a single lifetime, where the older completion
        // could otherwise render its payload over the newer one.
        var errorsShowFull = false;
        var loadSeq = 0;

        function activate() {
            var listEl = root.querySelector('#mdErrorList');
            // Only show "Loading…" on the very first render — the periodic refresh() also calls
            // activate() when the Errors tab is open, and a flash of "Loading…" every 3s makes
            // the list feel like it's being wiped whenever a new error arrives.
            if (!listEl.getAttribute('data-fingerprint')) {
                listEl.innerHTML = '<div class="fieldDescription">Loading…</div>';
            }
            var mySeq = ++loadSeq;
            Promise.all([
                api('Errors' + (errorsShowFull ? '?full=true' : '')),
                api('Errors/Count').catch(function () { return { Total: 0 }; })
            ]).then(function (r) {
                if (mySeq !== loadSeq) return;
                var errs = r[0] || [];
                var persistedTotal = (r[1] && r[1].Total) || errs.length;
                processErrorsResponse(errs, persistedTotal);
            });
        }

        function processErrorsResponse(errs, persistedTotal) {
            var listEl = root.querySelector('#mdErrorList');
            // Fingerprint over source + creation timestamp + occurrence count so we skip
            // re-render when the response is identical to the last one. Prevents mdFade replays,
            // closes-on-refresh of user-expanded <details> blocks, and the "errors flash cleared"
            // perception when a new error arrives. AtUtc + Count uniquely identifies each
            // entry's state; on dedup Count bumps, on new entry AtUtc changes.
            // Fingerprint includes persistedTotal + showFull so a state transition (Load older
            // → shows 500; new error trips the count → 501) still triggers a re-render.
            var fingerprint = (errorsShowFull ? 'full:' : 'buf:') + persistedTotal + '|' + errs.map(function (e) {
                return (e.Source || '') + '|' + (e.AtUtc || '') + '|' + (e.Count || 1) + '|' + (e.LastAtUtc || '');
            }).join(',');

            // Badge tracks total persisted count, not just the visible slice, so the tab
            // signals accurately whether the user has unseen errors offscreen.
            updateErrorBadge(persistedTotal);
            if (!errs.length) {
                if (listEl.getAttribute('data-fingerprint') !== fingerprint) {
                    listEl.setAttribute('data-fingerprint', fingerprint);
                    listEl.innerHTML = '<div class="mdCard mdFade" style="text-align:center;padding:2em;"><span class="material-icons" style="font-size:3em;color:#10b981;">check_circle</span><div style="margin-top:.4em;font-weight:600;">No errors recorded.</div><div class="fieldDescription">Nice — everything the plugin has tried has succeeded (or failed silently in a way we haven\'t plumbed yet).</div></div>';
                }
                return;
            }
            if (listEl.getAttribute('data-fingerprint') === fingerprint) return;
            listEl.setAttribute('data-fingerprint', fingerprint);
            var loadOlderRow = '';
            if (!errorsShowFull && persistedTotal > errs.length) {
                // Older entries live on disk but weren't loaded — offer to fetch them all.
                loadOlderRow = '<div style="text-align:center;margin:.6em 0;">' +
                    '<button is="emby-button" type="button" id="mdErrorsLoadOlder" class="raised mdActionBtn" title="Read the full persisted diagnostics table (up to 5000 rows). Useful when you have more errors than fit in the fast in-memory cache.">' +
                    '<span class="material-icons">unfold_more</span>' +
                    '<span>Load older errors (' + (persistedTotal - errs.length) + ' more)</span>' +
                    '</button></div>';
            } else if (errorsShowFull && persistedTotal > 100) {
                loadOlderRow = '<div style="text-align:center;margin:.6em 0;">' +
                    '<span class="fieldDescription">Showing all ' + persistedTotal + ' persisted errors.</span>' +
                    '</div>';
            }

            var html = errs.map(function (e, i) {
                var meta = errorMeta(e.Source);
                var countBadge = (e.Count || 1) > 1
                    ? '<span class="mdBadge" style="background:rgba(239,68,68,.35);color:#fff;margin-left:.4em;">×' + e.Count + '</span>'
                    : '';
                var timeText = (e.Count || 1) > 1 && e.LastAtUtc
                    ? new Date(e.AtUtc).toLocaleString() + ' → ' + new Date(e.LastAtUtc).toLocaleTimeString()
                    : new Date(e.AtUtc).toLocaleString();
                var hintBlock = meta.hint
                    ? '<div style="margin-top:.4em;padding:.5em .7em;border-left:3px solid #f59e0b;background:rgba(245,158,11,.08);border-radius:4px;font-size:.9em;">' + esc(meta.hint) + '</div>'
                    : '';
                // One-click adopt for legacy-batch diagnostics. The message embeds the path in
                // single quotes: "Legacy-looking recycle batch '<path>' was not referenced …".
                // Rendered inside the header's right-column so the button sits top-right
                // matching the tab-header action buttons (Retry all fixes, Refresh, Clear).
                var actionButton = '';
                if (e.Source === 'RecycleBin.LegacyBatchNeedsReview') {
                    var pathMatch = (e.Message || '').match(/'([^']+)'/);
                    if (pathMatch) {
                        actionButton = '<button is="emby-button" type="button" class="raised button-submit mdErrorAdopt" data-batch="' + esc(pathMatch[1]) + '" style="font-size:80%;" title="Writes the ownership marker into this batch so it merges into the current recycle bin. After that the Recycle bin tab lists it and retention purges pick it up.">' +
                            '<span class="material-icons" style="font-size:1.1em;vertical-align:-3px;margin-right:.3em;">merge</span>' +
                            '<span>Merge into current bin</span>' +
                            '</button>';
                    }
                }

                // Since 1.0.7.6 messages are plain-language (raw ffmpeg / stderr dumps are truncated
                // into a separate technical field). Render the message inline above the hint so users
                // can see the actionable body at a glance — the specific library names in an overlap
                // warning, the file path in a permission error, etc. Fall back to the source slug in
                // the collapsed <details> for filing bug reports.
                var messageBlock = e.Message
                    ? '<div style="margin-top:.5em;font-size:.9em;white-space:pre-wrap;word-break:break-word;">' + esc(e.Message) + '</div>'
                    : '';
                // The plain-language message is always visible above; the deeper explanation
                // (impact if ignored, technical background) lives inside a collapsed <details>
                // for users who want it. The source slug stays as a small footer inside the
                // details block so bug-report copies still have it, without cluttering the
                // primary view.
                var deepDetail = (meta.hint || e.Source)
                    ? '<details style="margin-top:.5em;">' +
                        '<summary class="fieldDescription" style="cursor:pointer;font-size:.85em;user-select:none;">What does this mean?</summary>' +
                        hintBlock +
                        '<div class="fieldDescription" style="margin-top:.4em;font-size:.75em;opacity:.6;">source: <code>' + esc(e.Source || 'unknown') + '</code></div>' +
                        '</details>'
                    : '';
                return '<div class="mdCard mdFade" style="margin:.5em 0;border-left:4px solid #ef4444;">' +
                    '<div style="display:flex;justify-content:space-between;gap:1em;flex-wrap:wrap;align-items:flex-start;">' +
                    '<span style="font-weight:600;flex:1;min-width:0;"><span class="material-icons" style="font-size:1.1em;vertical-align:-3px;color:#ef4444;margin-right:.3em;">' + meta.icon + '</span>' + esc(meta.title) + countBadge + '</span>' +
                    '<div style="display:flex;flex-direction:column;align-items:flex-end;gap:6px;flex:0 0 auto;">' +
                    '<span class="fieldDescription" style="font-size:.82em;">' + timeText + '</span>' +
                    actionButton +
                    '<span class="fieldDescription mdErrorAdoptMsg" style="font-size:.8em;"></span>' +
                    '</div>' +
                    '</div>' +
                    messageBlock +
                    deepDetail +
                    '</div>';
            }).join('');
            listEl.innerHTML = html + loadOlderRow;
            var loadOlderBtn = root.querySelector('#mdErrorsLoadOlder');
            if (loadOlderBtn) {
                loadOlderBtn.addEventListener('click', function () {
                    errorsShowFull = true;
                    listEl.removeAttribute('data-fingerprint');
                    activate();
                });
            }

            Array.from(listEl.querySelectorAll('.mdErrorAdopt')).forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var batch = btn.getAttribute('data-batch');
                    var msg = btn.parentElement.querySelector('.mdErrorAdoptMsg');
                    btn.disabled = true;
                    msg.textContent = 'Merging…';
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/RecycleBin/AdoptBatch'),
                        data: JSON.stringify({ Path: batch }),
                        contentType: 'application/json'
                    }).then(function () {
                        msg.textContent = 'Merged. Check the Recycle bin tab to see its contents.';
                        // Bin size totals move — refresh Overview drives too. Keep the
                        // success text visible long enough to read before the re-render
                        // regenerates the DOM (the diagnostic itself is historical and
                        // stays until the user hits Clear).
                        setTimeout(function () { listEl.removeAttribute('data-fingerprint'); activate(); refresh(true); }, 2500);
                    }, function (xhr) {
                        btn.disabled = false;
                        msg.textContent = 'Could not merge (' + (xhr && xhr.status) + '). The path may no longer be a direct child of the configured recycle bin root.';
                    });
                });
            });
        }

        function dispose() {}

        return {
            activate: activate,
            dispose: dispose,
            setShowFull: function (v) { errorsShowFull = !!v; }
        };
    };
})();
