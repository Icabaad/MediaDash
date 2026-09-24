// MediaDash Issues tab. Pure helpers (`badgeText`, `issueMatchesFilters`) were
// extracted in 06-D.7; the full-DOM factory createIssuesTab followed on
// 2026-09-23 (06-FOLLOWUP-1e, the last of the four tab-factory extractions).
// Same 06-D pattern as the other tabs: served by PluginAssetController,
// consumed via <script src>, self-tests load it via fs+vm.
//
// The factory body is byte-identical to the previously inline copy apart from:
//   - `esc`, `issueMatchesFilters` resolved through the shared window.MediaDash
//     namespace via local aliases.
//   - Ten outer-IIFE closures (`fmt`, `bytes`, `fileExtOf`, `mediaCategoryOf`,
//     `refresh`, `post`, `bulkIssueAction`, `closeIssueContextMenu`,
//     `openInFileBrowser`, `updateTabBadge`) are taken from deps at the call
//     site.
//   - `deps.lifetime` is accepted for parity but not consumed here (see the
//     comment at the top of the factory).
//   - `encodeURIComponent`, `window.confirm`, `window.alert`, `window.open`,
//     `JSON`, `Object`, `Math` (JS globals) are used unchanged.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Tab badge cap. Anything > 999 shows as "999+" so the badge stays fixed-width.
    ns.MediaDash.badgeText = function badgeText(n) {
        return n > 999 ? '999+' : String(n);
    };

    // Combined issue-row filter: type ∈ enum, text substring match on FileName || Path,
    // AudioLanguage-specific language filter matched against removeLanguages inside
    // DetailsJson. Non-audio rows pass through the lang filter unaffected (they simply
    // don't participate). Malformed DetailsJson on an audio row → no match.
    ns.MediaDash.issueMatchesFilters = function issueMatchesFilters(issue, typeFilter, textFilter, langFilter) {
        if (typeFilter && issue.Type !== typeFilter) return false;
        if (textFilter) {
            var f = String(textFilter).toLowerCase();
            var fn = (issue.FileName || '').toLowerCase();
            var pth = (issue.Path || '').toLowerCase();
            if (fn.indexOf(f) < 0 && pth.indexOf(f) < 0) return false;
        }
        if (langFilter) {
            if (issue.Type !== 'AudioLanguage') return true;
            try {
                var d = JSON.parse(issue.DetailsJson || '{}');
                return (d.removeLanguages || []).indexOf(langFilter) >= 0;
            } catch (e) { return false; }
        }
        return true;
    };

    ns.MediaDash.createIssuesTab = function createIssuesTab(deps) {
        var root = deps.root, api = deps.api, strings = deps.strings;
        var fmt = deps.fmt, bytes = deps.bytes;
        var fileExtOf = deps.fileExtOf, mediaCategoryOf = deps.mediaCategoryOf;
        var refresh = deps.refresh, post = deps.post, bulkIssueAction = deps.bulkIssueAction;
        var closeIssueContextMenu = deps.closeIssueContextMenu;
        var openInFileBrowser = deps.openInFileBrowser;
        var updateTabBadge = deps.updateTabBadge;
        var esc = ns.MediaDash.esc;
        var issueMatchesFilters = ns.MediaDash.issueMatchesFilters;
        // deps.lifetime is accepted for parity but not consumed — the wrapped
        // api() already carries the page-lifetime token guard, and the
        // in-flight `loading` flag below prevents overlapping /Issues fetches
        // within a single lifetime (preserving the pre-refactor _issuesLoading
        // semantics — see loadIssues comment). Adding a per-tab loadSeq would
        // be dead code today.
        var ISSUE_PAGE_SIZE = 100;
        var lastIssueList = [];
        var lastFilteredIssues = [];
        var issuePage = 1;
        var issueLangFilter = '';
        var showingDismissed = false;
        var loading = false;

        function sameFolder(a, b) {
            function dir(p) {
                var i = Math.max(String(p || '').lastIndexOf('/'), String(p || '').lastIndexOf('\\'));
                return i > 0 ? p.substring(0, i).toLowerCase() : '';
            }
            return dir(a) === dir(b);
        }

        function renderTotals() {
            var el = root.querySelector('#mdIssueTotals');
            if (!el) return;
            var counts = {};
            lastIssueList.forEach(function (i) { counts[i.Type] = (counts[i.Type] || 0) + 1; });
            var keys = Object.keys(counts).sort();
            if (!keys.length) { el.innerHTML = ''; return; }
            var current = root.querySelector('#mdTypeFilter').value;
            var total = lastIssueList.length;
            var html = '<button is="emby-button" type="button" class="raised mdTotalPill" data-type="" style="font-size:80%;background:' + (current === '' ? '#00a4dc' : 'rgba(255,255,255,.08)') + ';">All <b style="margin-left:.3em;">' + total + '</b></button>';
            keys.forEach(function (k) {
                var t = strings.types[k] || { title: k, color: '#888' };
                var active = current === k;
                html += '<button is="emby-button" type="button" class="raised mdTotalPill" data-type="' + esc(k) + '" style="font-size:80%;background:' + (active ? t.color : 'rgba(255,255,255,.08)') + ';border-left:3px solid ' + t.color + ';">' + esc(t.title) + ' <b style="margin-left:.3em;">' + counts[k] + '</b></button>';
            });
            el.innerHTML = html;
            el.querySelectorAll('.mdTotalPill').forEach(function (b) {
                b.addEventListener('click', function () {
                    root.querySelector('#mdTypeFilter').value = b.getAttribute('data-type');
                    issuePage = 1;
                    issueLangFilter = '';
                    renderTotals();
                    renderLangFilter();
                    renderFiltered();
                });
            });
        }

        function renderLangFilter() {
            var row = root.querySelector('#mdIssueLangRow');
            var sel = root.querySelector('#mdIssueLangFilter');
            if (!row || !sel) return;
            var typeFilter = root.querySelector('#mdTypeFilter').value;
            var candidates = lastIssueList.filter(function (i) {
                return i.Type === 'AudioLanguage' && (!typeFilter || typeFilter === 'AudioLanguage');
            });
            if (!candidates.length) { row.style.display = 'none'; return; }
            var langs = {};
            candidates.forEach(function (i) {
                try {
                    var d = JSON.parse(i.DetailsJson || '{}');
                    (d.removeLanguages || []).forEach(function (l) { if (l) langs[l] = true; });
                } catch (e) { /* malformed details — skip */ }
            });
            var opts = Object.keys(langs).sort();
            if (!opts.length) { row.style.display = 'none'; return; }
            row.style.display = 'flex';
            root.querySelector('#mdIssueLangLabel').textContent = strings.langFilterLabel || 'Filter by audio language:';
            var current = issueLangFilter && opts.indexOf(issueLangFilter) >= 0 ? issueLangFilter : '';
            issueLangFilter = current;
            var html = '<option value="">' + esc(strings.langFilterAll || 'All languages') + '</option>';
            opts.forEach(function (l) {
                html += '<option value="' + esc(l) + '"' + (l === current ? ' selected' : '') + '>' + esc(l) + '</option>';
            });
            sel.innerHTML = html;
        }

        function renderFiltered() {
            var el = root.querySelector('#mdIssueList');
            var approveAllBtn = root.querySelector('#mdApproveAll');
            var clearAllBtn = root.querySelector('#mdClearAllIssues');
            var pager = root.querySelector('#mdIssuePager');
            var typeFilter = root.querySelector('#mdTypeFilter').value;
            var textFilter = (root.querySelector('#mdIssueFilter').value || '').trim().toLowerCase();
            var list = lastIssueList.filter(function (i) {
                return issueMatchesFilters(i, typeFilter, textFilter, issueLangFilter);
            });
            lastFilteredIssues = list;

            // Pagination: clamp page after filter changes, then slice.
            var totalPages = Math.max(1, Math.ceil(list.length / ISSUE_PAGE_SIZE));
            if (issuePage > totalPages) issuePage = totalPages;
            if (issuePage < 1) issuePage = 1;
            var pageStart = (issuePage - 1) * ISSUE_PAGE_SIZE;
            var pageSlice = list.slice(pageStart, pageStart + ISSUE_PAGE_SIZE);

            // ponytail: skip re-render when the visible list is unchanged. Without this the 3s status poll
            // during a fix run replays mdFade on every card (visible flicker) and swaps click listeners mid-interaction.
            var fingerprint = typeFilter + '|' + textFilter + '|' + issueLangFilter + '|' + issuePage + '|' + pageSlice.map(function (i) { return i.Id + ':' + i.Status; }).join(',');
            if (el.getAttribute('data-fingerprint') === fingerprint) return;
            el.setAttribute('data-fingerprint', fingerprint);

            if (typeFilter && list.length > 1) {
                var ft = strings.types[typeFilter] || {};
                approveAllBtn.style.display = '';
                approveAllBtn.querySelector('.mdApproveAllLabel').textContent = fmt(ft.approveAll || strings.approveAll, list.length);
                root.querySelector('#mdApproveAllNote').textContent = strings.approveAllNote;
            } else if (!typeFilter && list.length > 0) {
                approveAllBtn.style.display = '';
                approveAllBtn.querySelector('.mdApproveAllLabel').textContent = fmt(strings.approveAllTypes, list.length);
                root.querySelector('#mdApproveAllNote').textContent = strings.approveAllNote;
            } else {
                approveAllBtn.style.display = 'none';
                root.querySelector('#mdApproveAllNote').textContent = '';
            }

            if (list.length > 0) {
                clearAllBtn.style.display = '';
                if (showingDismissed) {
                    clearAllBtn.classList.add('mdRevertMode');
                    root.querySelector('#mdClearAllIcon').textContent = 'restore_from_trash';
                    root.querySelector('#mdClearAllLabel').textContent = fmt(strings.unignoreAll || 'Unignore all {0} shown', list.length);
                    // Approve-all makes no sense on ignored rows — hide it in this view.
                    approveAllBtn.style.display = 'none';
                } else {
                    clearAllBtn.classList.remove('mdRevertMode');
                    root.querySelector('#mdClearAllIcon').textContent = 'delete_sweep';
                    root.querySelector('#mdClearAllLabel').textContent = fmt(strings.clearAll || 'Clear all {0} shown', list.length);
                }
            } else {
                clearAllBtn.style.display = 'none';
            }

            if (!list.length) {
                var empty = showingDismissed ? (strings.noIgnored || 'No ignored issues.') : strings.noIssues;
                el.innerHTML = '<div class="fieldDescription" style="margin-top:1em;">' + esc(empty) + '</div>';
                pager.style.display = 'none';
                return;
            }

            if (totalPages > 1) {
                pager.style.display = 'flex';
                root.querySelector('#mdIssuePageInfo').textContent = fmt(strings.page || 'Page {0} of {1}', issuePage, totalPages);
                root.querySelector('#mdIssuePagePrev').disabled = issuePage <= 1;
                root.querySelector('#mdIssuePageNext').disabled = issuePage >= totalPages;
            } else {
                pager.style.display = 'none';
            }

            var html = '';
            pageSlice.forEach(function (i) {
                var t = strings.types[i.Type] || { title: i.Type, color: '#888' };
                var isQueued = i.Status === 'Queued';
                var statusBadge = isQueued
                    ? '<span class="mdBadge" style="background:rgba(0,164,220,.35);color:#fff;">queued</span> '
                    : '';
                // "You restored this before" badge: rendered on any Detected row whose (path, type)
                // is in the restored_paths table. Tells the user why the row isn't auto-queuing
                // even though the type is set to Automatic — MediaDash won't override their restore.
                var restoredBadge = i.WasPreviouslyRestored
                    ? '<span class="mdBadge" style="background:rgba(245,158,11,.20);color:#fbbf24;border:1px solid rgba(245,158,11,.5);" title="MediaDash will not auto-fix this file again because you restored it before. Click Approve to fix it anyway, or Ignore this to stop seeing it.">↩ you restored this before</span> '
                    : '';
                var verifyBtn = '';
                if (i.Type === 'Playability' && i.ItemId && i.ItemId !== '00000000-0000-0000-0000-000000000000') {
                    verifyBtn = '<button is="emby-button" type="button" class="raised mdOpenInJellyfin" data-item="' + esc(i.ItemId) + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:middle;margin-right:.2em;">play_arrow</span>Try to play</button>';
                }
                // Split into (buttons-only) and (hint) so the mdIssueActions column stays narrow
                // and the hint wraps under the body — otherwise the flex column expands to fit
                // multi-line hint text and squeezes the body to 0px.
                var actionButtons, actionHint;
                var isDismissed = i.Status === 'Dismissed';
                if (isDismissed) {
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised mdUnignore" data-id="' + i.Id + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">restore_from_trash</span><span>' + esc(strings.unignore || 'Unignore') + '</span></button>';
                    actionHint = '<strong>' + esc(strings.unignore || 'Unignore') + ':</strong> ' + esc(strings.unignoreHint || 'Move this issue back to the open list.');
                } else if (isQueued) {
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised mdUndoAction" data-id="' + i.Id + '" style="font-size:80%;" title="' + esc(strings.undoActionHint) + '"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">undo</span><span>' + esc(strings.undoAction) + '</span></button>' +
                        '<button is="emby-button" type="button" class="raised mdDismiss" data-id="' + i.Id + '" style="font-size:80%;"><span>' + esc(strings.dismiss) + '</span></button>';
                    actionHint = '<span class="material-icons" style="font-size:15px;vertical-align:middle;color:var(--md-primary);">schedule</span> Queued for next fix run — click "Run fixes now" on Overview. Change your mind? Click Undo.';
                } else {
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised button-submit mdApprove" data-id="' + i.Id + '" style="font-size:80%;" title="' + esc(t.actionHint || '') + '"><span>' + esc(t.action || strings.approve) + '</span></button>' +
                        '<button is="emby-button" type="button" class="raised mdDismiss" data-id="' + i.Id + '" style="font-size:80%;"><span>' + esc(strings.dismiss) + '</span></button>';
                    // Label each half of the hint with the button it describes so the two
                    // sentences don't run together and read as one confusing description of a
                    // single action. Field report: "Downloads one via… · Excludes this file
                    // from future scans" was parsed as one sentence.
                    var approveLabel = esc(t.action || strings.approve);
                    var dismissLabel = esc(strings.dismiss);
                    actionHint =
                        '<strong>' + approveLabel + ':</strong> ' + esc(t.actionHint || strings.approveHint) +
                        ' · <strong>' + dismissLabel + ':</strong> ' + esc(strings.dismissHint);
                }
                var keeperPath = '';
                if (i.Type === 'Duplicate' && i.DetailsJson) {
                    try { var d = JSON.parse(i.DetailsJson); keeperPath = d && d.keeperPath ? d.keeperPath : ''; } catch (e) { keeperPath = ''; }
                }
                var pathBlock;
                if (keeperPath) {
                    pathBlock = '<div class="fieldDescription" style="margin-top:.3em;"><b style="color:#ef4444;">Delete this copy:</b></div>' +
                        '<div class="fieldDescription mdIssuePath" data-path="' + esc(i.Path) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open this folder in the Files tab">' + esc(i.Path) + '</div>' +
                        '<div class="fieldDescription" style="margin-top:.3em;"><b style="color:#22c55e;">Better copy (kept):</b></div>' +
                        '<div class="fieldDescription mdIssuePath" data-path="' + esc(keeperPath) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open the keeper folder in the Files tab">' + esc(keeperPath) + '</div>';
                } else {
                    pathBlock = '<div class="fieldDescription mdIssuePath" data-path="' + esc(i.Path) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open this folder in the Files tab">' + esc(i.Path) + '</div>';
                }
                html += '<div class="mdCard mdIssueCard mdFade" style="--md-issue-accent:' + t.color + ';' + ((isQueued || isDismissed) ? 'opacity:.72;' : '') + '" data-issue="' + i.Id + '">' +
                    '<div class="mdIssueLayout">' +
                        '<div class="mdIssueDisc"><span class="material-icons">' + esc(t.icon || 'info') + '</span></div>' +
                        '<div class="mdIssueBody">' +
                            '<div class="mdIssueTitleRow">' +
                                '<span class="mdIssueFilename">' + esc(i.FileName) + '</span>' +
                                '<span class="mdIssueTagChip">' + esc(t.title) + '</span>' +
                                statusBadge +
                                restoredBadge +
                            '</div>' +
                            pathBlock +
                            '<div class="mdIssueHint">' + esc(i.SuggestedFix) +
                                (i.SizeSavings > 0 ? ' <span class="mdIssueSavings">(' + esc(bytes(i.SizeSavings)) + ')</span>' : '') +
                            '</div>' +
                        '</div>' +
                        '<div class="mdIssueActions">' + actionButtons + '</div>' +
                    '</div>' +
                    '<div class="mdIssueFooter">' +
                        '<div class="mdIssueActionHint">' + actionHint + '</div>' +
                        '<div class="fieldDescription mdRowMsg mdIssueRowMsg"></div>' +
                    '</div>' +
                '</div>';
            });
            el.innerHTML = html;
            wireRowHandlers(el);
        }

        // Row-button click handlers get re-attached on every renderFiltered
        // but attach to the fresh innerHTML nodes; the previous set dies with
        // the replaced DOM, so no lifetime.own bookkeeping is needed.
        function wireRowHandlers(el) {
            el.querySelectorAll('.mdApprove').forEach(function (b) {
                b.addEventListener('click', function () { rowAction(b, 'Approve', strings.approved); });
            });
            el.querySelectorAll('.mdDismiss').forEach(function (b) {
                b.addEventListener('click', function () { rowAction(b, 'Dismiss', strings.dismissed); });
            });
            el.querySelectorAll('.mdUndoAction').forEach(function (b) {
                b.addEventListener('click', function () { undoRowAction(b); });
            });
            // Un-ignore reuses the /Issues/{id}/Revert endpoint via undoRowAction — same guard,
            // same reload path, so a mis-click on a Dismissed row lands the same as the Queued undo.
            el.querySelectorAll('.mdUnignore').forEach(function (b) {
                b.addEventListener('click', function () { undoRowAction(b); });
            });
            el.querySelectorAll('.mdIssuePath').forEach(function (p) {
                p.addEventListener('click', function () { openInFileBrowser(p.getAttribute('data-path')); });
            });
            el.querySelectorAll('.mdOpenInJellyfin').forEach(function (b) {
                b.addEventListener('click', function () {
                    var id = b.getAttribute('data-item');
                    // Open the item's detail page in a new tab; user hits Play there. Falls out to the
                    // web root if the id format changes between Jellyfin versions — the tab will just
                    // land on the home screen instead of a broken deep link.
                    window.open('/web/#/details?id=' + encodeURIComponent(id), '_blank', 'noopener');
                });
            });
        }

        function rowAction(button, action, doneText) {
            var id = button.getAttribute('data-id');
            post('Issues/' + id + '/' + action).then(function () {
                var row = button.closest('[data-issue]');
                row.querySelector('.mdRowMsg').textContent = doneText;
                row.style.opacity = '.55';
                // Keep the in-memory issue list in sync with the server. Without this the
                // renderFiltered fingerprint (Id + Status) stays stale, and a follow-up
                // activate() short-circuits its DOM re-render because "nothing changed" — that's
                // the class of bug that broke undo before we mutated Status here.
                var issue = lastIssueList.find(function (i) { return String(i.Id) === String(id); });
                if (issue) issue.Status = (action === 'Approve') ? 'Queued' : 'Dismissed';
                // Replace the action cluster with a single Undo button. Users often mis-click
                // Approve / Dismiss; the Fix task runs on a schedule so there's a real window to
                // change your mind. Revert flips the issue back to Detected on the server.
                var actionsCell = row.querySelector('.mdIssueActions');
                if (actionsCell) {
                    actionsCell.innerHTML = '<button is="emby-button" type="button" class="raised mdUndoAction" data-id="' + id + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">undo</span><span>' + esc(strings.undoAction) + '</span></button>';
                    var undoBtn = actionsCell.querySelector('.mdUndoAction');
                    undoBtn.addEventListener('click', function () { undoRowAction(undoBtn); });
                }
                // Refresh in the background so PendingFixCount reflects the approval right away — otherwise
                // clicking "Run fixes now" immediately after approving reads the stale count and shows "nothing to fix".
                refresh(true);
            });
        }

        function undoRowAction(button) {
            var id = button.getAttribute('data-id');
            button.disabled = true;
            post('Issues/' + id + '/Revert').then(function () {
                // Bust the fingerprint so renderFiltered doesn't short-circuit. The list
                // returned from activate() will report Status=Detected, which is what the row
                // ALREADY reads as in lastIssueList after a manual DOM-only Approve — leaving
                // the fingerprint identical and the render skipped. Explicitly clearing it here
                // forces the re-render every time.
                var listEl = root.querySelector('#mdIssueList');
                if (listEl) listEl.removeAttribute('data-fingerprint');
                activate();
                refresh(true);
            }, function () {
                button.disabled = false;
                var row = button.closest('[data-issue]');
                if (row) {
                    row.querySelector('.mdRowMsg').textContent = strings.undoFailedMsg;
                }
            });
        }

        // ---------- Issue context menu (per-list) ----------

        // Wires the right-click menu on the issue list. Uses event delegation
        // on the list container so cards added on later renders pick it up
        // automatically. Global dismissal listeners (click/keydown/scroll) are
        // owned by the top-level wireIssueMenuGlobals() via ownGlobal, so
        // pagehide disposes them and every pageshow re-attaches fresh.
        function installIssueContextMenu() {
            var list = root.querySelector('#mdIssueList');
            var menu = root.querySelector('#mdIssueCtxMenu');
            if (!list || !menu || list.dataset.ctxInit) return;
            list.dataset.ctxInit = '1';
            list.addEventListener('contextmenu', function (ev) {
                var card = ev.target && ev.target.closest ? ev.target.closest('.mdIssueCard') : null;
                if (!card) return;
                var issueId = card.getAttribute('data-issue');
                var issue = lastIssueList.find(function (i) { return String(i.Id) === String(issueId); });
                if (!issue) return;
                ev.preventDefault();
                showIssueContextMenu(ev.clientX, ev.clientY, issue);
            });
        }

        function showIssueContextMenu(x, y, issue) {
            var menu = root.querySelector('#mdIssueCtxMenu');
            var typeTitle = (strings.types[issue.Type] && strings.types[issue.Type].title) || issue.Type;
            var ext = fileExtOf(issue.Path) || '(no extension)';
            var cat = mediaCategoryOf(issue.Path);
            var folderMatches = lastIssueList.filter(function (i) { return sameFolder(i.Path, issue.Path); });
            var typeMatches = lastIssueList.filter(function (i) { return i.Type === issue.Type; });
            var extMatches = lastIssueList.filter(function (i) { return fileExtOf(i.Path) === ext.toLowerCase() || (ext === '(no extension)' && fileExtOf(i.Path) === ''); });
            var catMatches = lastIssueList.filter(function (i) { return mediaCategoryOf(i.Path) === cat; });

            var opts = [
                { label: fmt(strings.ctxIgnoreFolder || 'Ignore all issues in this folder', '') + ' (' + folderMatches.length + ')', ids: folderMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreType || 'Ignore all "{0}" issues', typeTitle) + ' (' + typeMatches.length + ')', ids: typeMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreExt || 'Ignore all {0} files', ext) + ' (' + extMatches.length + ')', ids: extMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreMedia || 'Ignore all {0} issues', cat) + ' (' + catMatches.length + ')', ids: catMatches.map(function (i) { return i.Id; }) }
            ];

            var html = '';
            opts.forEach(function (o, idx) {
                html += '<div class="mdCtxItem" data-idx="' + idx + '" style="padding:.55em 1em;cursor:pointer;white-space:nowrap;" onmouseover="this.style.background=\'rgba(255,255,255,.08)\'" onmouseout="this.style.background=\'\'">' + esc(o.label) + '</div>';
            });
            menu.innerHTML = html;

            // Show first so we can measure, then clamp inside the viewport.
            menu.style.display = 'block';
            menu.style.left = '0px';
            menu.style.top = '0px';
            var rect = menu.getBoundingClientRect();
            var px = Math.min(x, window.innerWidth - rect.width - 8);
            var py = Math.min(y, window.innerHeight - rect.height - 8);
            menu.style.left = Math.max(4, px) + 'px';
            menu.style.top = Math.max(4, py) + 'px';

            menu.querySelectorAll('.mdCtxItem').forEach(function (it) {
                it.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    var idx = +it.getAttribute('data-idx');
                    var opt = opts[idx];
                    closeIssueContextMenu();
                    if (!opt.ids.length) {
                        window.alert(strings.ctxNothing || 'No matching open issues.');
                        return;
                    }
                    if (!window.confirm(fmt(strings.ctxConfirm || 'Ignore {0} issue(s)?', opt.ids.length))) return;
                    bulkIssueAction(opt.ids, 'Dismiss').then(function () { activate(); refresh(true); });
                });
            });
        }

        function renderIssues(list) {
            lastIssueList = list || [];
            // Keep the tab badge in lock-step with the actual list length so it can't disagree
            // with the "All: N" totals pill or the visible cards. Server's Status.OpenIssueTotal
            // is only used as a first-paint fallback in renderStatus. Skip when viewing Dismissed —
            // the Issues badge is meant to signal open work, not the ignore pile.
            if (!showingDismissed) {
                updateTabBadge('#mdIssueBadge', lastIssueList.length);
            }
            renderTotals();
            renderLangFilter();
            renderFiltered();
        }

        function activate() {
            // Review P2-7: prevent overlapping /Issues requests. On a 20 K-row library the
            // response is ~10 MB and takes ~2.3 s — but Status polls at 3 s cadence and can
            // trigger a fresh activate() before the previous one lands. Overlapping copies
            // burn CPU, memory, and network with no benefit; only the last render "wins".
            // Skip queued fetches when one's in flight; the next poll will pick up the delta.
            if (loading) return;
            loading = true;
            // ponytail: always fetch the full list for the current view — the type filter is applied
            // client-side by renderFiltered so the totals row can show counts for every type
            // simultaneously. The Dismissed view uses the same rendering path.
            var url = showingDismissed ? 'Issues?status=Dismissed' : 'Issues?openOnly=true';
            api(url)
                .then(renderIssues)
                .then(function () { loading = false; })
                .catch(function () { loading = false; });
        }

        function setLangFilter(l) {
            issueLangFilter = l || '';
            issuePage = 1;
            renderFiltered();
        }

        function nextPage() { issuePage++; renderFiltered(); }
        function prevPage() { if (issuePage > 1) { issuePage--; renderFiltered(); } }

        function toggleShowIgnored() {
            showingDismissed = !showingDismissed;
            // Reset paging + type filter — dismissed and open sets are disjoint so a stale
            // filter would just show "no results" for the other view.
            issuePage = 1;
            root.querySelector('#mdTypeFilter').value = '';
            // Force a fresh render — the fingerprint is content-based, not mode-based.
            var listEl = root.querySelector('#mdIssueList');
            if (listEl) listEl.removeAttribute('data-fingerprint');
            activate();
        }

        // dispose() intentionally no-op. Per-row handlers are re-attached on
        // every renderFiltered() to fresh innerHTML nodes; the old ones die
        // with their DOM. The `list.contextmenu` handler is DOM-scoped and
        // guarded by list.dataset.ctxInit so it wires once per persistent
        // view DOM. Global click/keydown/scroll dismissal listeners are owned
        // by the top-level wireIssueMenuGlobals() via ownGlobal (06-A.2), so
        // they round-trip cleanly on pagehide/pageshow. Reserved for future
        // teeth when 06-D pulls the context-menu into its own owned lifecycle.
        function dispose() {}

        return {
            activate: activate,
            render: renderFiltered,
            dispose: dispose,
            setLangFilter: setLangFilter,
            setPage: function (n) { issuePage = n | 0; renderFiltered(); },
            nextPage: nextPage,
            prevPage: prevPage,
            toggleShowIgnored: toggleShowIgnored,
            showingIgnored: function () { return showingDismissed; },
            count: function () { return lastIssueList.length; },
            filteredIds: function () { return lastFilteredIssues.map(function (i) { return i.Id; }); },
            installContextMenu: installIssueContextMenu
        };
    };
})();
