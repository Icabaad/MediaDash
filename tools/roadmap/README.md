# Roadmap

Single-page kanban for planning MediaDash releases. Not a plugin feature — dev tool.

## Acceptance tests

The [roadmap test matrix](../../docs/testing/ROADMAP-TEST-MATRIX.md) specifies
acceptance cases for all unfinished cards, including research and superseded
items. Add test and fresh evidence links to a card's notes before marking it
complete. The [coverage review](../../docs/testing/REGRESSION-COVERAGE-REVIEW-2026-09-19.md)
records current harness gaps and shared regression/release gates. Planned tests
do not count as executed coverage.

## Version format and release order

Use exactly four numeric components: **major release.major feature.bug group.hotfix**.

| Component | When to increment | Example |
|---|---|---|
| Major release | A substantial product generation or incompatible change; reset the other components. | `2.0.0.0` |
| Major feature | A coherent new capability; reset bug group and hotfix. | `1.1.0.0` |
| Bug group | A planned batch of corrections and regression coverage without new product features; reset hotfix. | `1.1.1.0` |
| Hotfix | A narrowly scoped urgent correction to a shipped release. | `1.1.1.1` |

Feature releases may contain fixes. Create follow-up bug groups and hotfixes when actual defects justify them, rather than adding empty placeholder releases. No `2.0.0.0` scope is committed yet; the planned additions fit the 1.x generation.

The 2026-09-18 reorganisation preserves shipped numbers and completed cards. The existing pending `1.0.7.8` bundle keeps its assignment as a transition exception because it already mixes implemented fixes and features; its release approval is unchanged. Feature work previously assigned to `1.0.8.0` moves into the feature releases below.

| Planned version | Scope |
|---|---|
| `1.0.8.0` | Stabilisation, documentation reconciliation and cross-platform/version release checks. |
| `1.1.0.0` | Per-library profiles, persistent protection, repair-only actions, previews, before/after history, audio-only repair and minimum encoding savings. |
| `1.2.0.0` | Bounded retries, plugin-data maintenance, low-space warnings, diagnostic exports, progress and optional digests. |
| `1.3.0.0` | Audio conversion, original-track preservation, explicit track policies and subtitle extraction. |
| `1.4.0.0` | Media organiser and carefully gated duplicate automation. |
| `1.5.0.0` | Library/Overview redesign using the policy, maintenance and history data introduced earlier. |
| `1.6.0.0` | Leaving soon review list with countdown and Keep action; files remain in their libraries. |

Later fixers depend on the preview, protection and policy support in `1.1.0.0`. These are planned targets, not implementation or release claims. Automatic stale deletion stays in Backlog and requires revising the detect-only scope in `PLAN.md`. Hardware-dependent enhancements and drive-maintenance research remain uncommitted. Superseded cards preserve their notes and point to the canonical card; they are not marked implemented.

The populated board shows pending and planned releases first, then backlog, superseded cards and historical releases. Card completion records implementation status; it does not mean the containing release has shipped.

## Open it

Double-click `index.html`, or open the file path directly in Chrome/Edge. `file://` works — no server needed.

## First launch

The welcome card gives you two options:

- **New file** — pick a location for `roadmap.json`. The page seeds empty buckets for the planned bug group (1.0.8.0), feature releases (1.1.0.0 through 1.6.0.0) and Backlog. Every change autosaves to the file you picked.
- **Open existing** — load a previously-saved `roadmap.json`.

Open `tools/roadmap/roadmap.json` in your checkout to load the populated plan. If it is already open while an external edit is made, reload it with **Open existing** before editing so stale browser state does not overwrite the file.

## Using it

| Action | How |
|---|---|
| Add item to a column | `+ Add item` at the bottom of the column, or `+ Version` in the header to add a new column first. |
| Move item between versions | Drag the card, drop on another column. |
| Edit item (title, notes, kind) | Click the card. `Save` commits, `Cancel` reverts. |
| Delete item | Hover the card, click the `×` in the corner. |
| Rename version | Click the column name. Enter commits, Escape cancels. |
| Delete version | Click the `⋯` next to the column name. Any items in it move to Backlog. |

Backlog is special — it can't be deleted; it's the fallback for orphaned items.

## What Claude sees

Same JSON file. Ask Claude to read `tools/roadmap/roadmap.json` when you want it to know what's queued for which version.

Schema:

```json
{
  "versions": [{ "id": "v-1-0-8-0", "name": "1.0.8.0", "order": 3 }],
  "items": [{
    "id": "01H7...", "title": "…", "notes": "…", "kind": "feature",
    "versionId": "v-1-0-8-0",
    "createdAt": "2026-09-05T…", "updatedAt": "2026-09-05T…"
  }]
}
```

## Browser support

Autosave uses the File System Access API — Chromium-based only (Chrome, Edge, Brave, Opera). Firefox and Safari get a graceful fallback: manual `Download` button and no autosave. If you're on those, you'll need to re-download after each session.

## Not doing (kept out on purpose)

- Multi-user sync, remote hosting, auth — offline personal tool.
- Filter / search — small dataset, scroll works.
- Undo history — the JSON file is the history; keep backups if you care.
- Import from CHANGELOG / spec files — seed manually.
- Subtasks — items are flat.

If any of these become painful, add them then. Not before.
