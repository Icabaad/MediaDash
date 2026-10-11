# Files context menu review and fixes

Reviewed the local Files-tab manual-add and protection implementation on 9 October 2026. Existing unrelated local changes were preserved.

## Findings fixed

- Menu actions combined a saved filename with the current directory at submission time. They now capture the complete target when opened; navigation cancels pending long presses and closes the menu.
- Long presses could trigger the normal file click afterward. The following touch-generated click is suppressed, and multiple touches cancel the pending menu.
- Keyboard button roles had no Enter/Space activation. The menu now supports both, clamps its measured size to the viewport (including expanded actions), and restores focus. The protection dialog supports Escape and keeps Tab focus inside.
- Document listeners and pending long presses survived page hiding. Listeners now follow the existing page lifetime and reattach once per visit.
- Protection errors displayed raw JSON. Fetch and XHR failures now display the server's message. Duplicate protection submissions are blocked while saving.
- Manual video re-encoding accepted other file types and stream/disc placeholders. Unsupported files and indexed folders now receive a clear rejection. Invalid or relative paths receive a bad request rather than throwing.
- An existing dismissed issue could suppress insertion while the endpoint returned a successful response with ID zero. It now returns a conflict explaining how to restore the dismissed issue. Queued retries retain the existing issue.
- Manual additions against protected paths are refused explicitly.
- Protection previously filtered emitted issues after analysis. Scheduled scans now filter indexed media before scanning; filesystem and sidecar scanners check candidate protection before analysis. Empty-folder scanning preserves protected descendants while still discovering unprotected sibling folders. The result filter remains in place for rules changed during a scan.

## Validation

- Release build and complete C# suite: **1,483 passed, 1 skipped, 0 failed** (1,484 total). The skipped test is the existing `CapturesSourceIdentityBeforeConversion` test.
- JavaScript regression suite: **25 passed, 0 failed**. Covers target capture, protection request masks, duplicate submissions, keyboard actions, long presses, page hide/reopen, error decoding, and dialog focus.
- Controller tests use a temporary SQLite database and a mocked Jellyfin library interface. Both file and folder protection persist through the real CRUD endpoint, invalidate cached decisions, block manual additions, and permit additions after removal. Dismissed/queued and invalid/unindexed request outcomes are checked.
- Fix-executor regression proves protection added after approval skips the fixer and keeps the issue queued; removing protection permits execution.
- Owned directory fixtures prove protected empty folders are excluded and unprotected siblings remain eligible.

## Limits

No Jellyfin deployment/restart, live-browser visual check, physical touch-device test, or production media changes were performed. Browser behavior is validated through the JavaScript DOM harness. Some filesystem sweeps still enumerate names under protected directories; protected candidates are excluded before file-content analysis. These tests do not establish complete release readiness for the repository's other concurrent changes.
