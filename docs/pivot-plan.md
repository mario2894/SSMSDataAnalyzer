# Pivot selected rows — development plan

Status: **Phase 1 + 2 shipped and confirmed live (v0.11.0 / v0.11.1).** Phase 3 **item 13 (FK
link icons) in progress** (user go-ahead 2026-09-10; items 11, 12, 14 not requested).
Decisions D1–D3 were built as recommended; D4 is resolved: FK icons are done as Phase 3,
after Phases 1 and 2 shipped. Team: R1 (Opus) → U2 (Sonnet), interface in §12 (written by
R1, reviewed by the lead before U2 starts).

Live findings that override the spike (§7) — see docs/resultsgrid-api.md "LIVE CORRECTION":
- A right-click outside the selection does **not** move SSMS's selection. The command
  captures the right-clicked row in BeforeQueryStatus: a row outside the selection → pivot
  just that row.
- Ctrl+click on row numbers replaces the selection (native SSMS). Ctrl+click on **cells**
  adds to it, which is the documented way to pick rows far apart.

## 1. What it does

Select some rows in an SSMS results grid → right-click → **Pivot selected rows…**. A
window opens with the rows turned sideways: column names in the first column, one column
per selected row.

```
┌────────────────┬──────────────┬──────────────┬──────────────┐
│ Column         │ Row 3        │ Row 7        │ Row 12       │
├────────────────┼──────────────┼──────────────┼──────────────┤
│ ID             │ 4521         │ 4522         │ 4530         │
│ FundID      🔗 │ 17        ➜  │ 17        ➜  │ 19        ➜  │  ◀ differs
│ StatusID    🔗 │ 2         ➜  │ 2         ➜  │ 2         ➜  │
│ DateCreated    │ 2026-03-01…  │ 2026-03-01…  │ 2026-04-11…  │  ◀ differs
│ Note           │ NULL         │ NULL         │ Storno       │  ◀ differs
└────────────────┴──────────────┴──────────────┴──────────────┘
  Showing 3 of 3 selected rows · 42 columns · 3 differ
```

`➜` = the Go to source icon inside FK cells (section 4, item 13).

### Why it fits this extension

- **The pivot itself needs no database access.** It only reads the grid's own values
  through the portable `IGridStorage` API, so it works on any query (joins, cross-database
  queries, computed columns, `SELECT *`) with no base-table checks.
  Only the optional FK icons (item 13) touch the server, using the same describe call Go to
  source already makes.
- **It works on SSMS 22.3.** It uses the same API as Find in Results and Go to source
  (`IGridStorage.GetCellDataAsString`, `IGridControl.GetHeaderInfo`,
  `IGridControl.SelectedCells`), so there is no newer-API split.

## 2. Selection rules

| Situation | Behavior |
|---|---|
| Whole rows selected (clicking the row numbers) | Pivot those rows |
| A block of cells selected | Pivot every row the block touches, with **all** columns |
| Several separate selections (Ctrl+click) | Combine them, remove duplicate rows, keep **grid order** (not click order) |
| Only one cell selected | Pivot that one row (record view, useful for wide tables) |
| More than the limit selected | See decision D1. Recommended: show the first N and a banner "Showing first 100 of 3,412 selected rows" |
| Ctrl+A on a 1M-row result | Must stay cheap. Count rows from the ranges (`BlockOfCells`), never by visiting each row |

**Limit:** default **100**, configurable in Tools → Options → Data Analyzer (range 1–500).
It also caps memory use: at most 500 rows × columns × display text.

## 3. Snapshot semantics

- Values are copied into memory **when the pivot opens**. After that the pivot does not
  depend on the grid, so re-running the query or closing the tab does not break it.
- Values are the grid's **display text**. That means the SSMS "Maximum characters
  retrieved" cut-off and SSMS date formatting apply.
- **Known limitation:** the portable API can't tell a real NULL from the text `'NULL'`
  (already documented for Go to source in docs/newer-grid-api.md). Difference highlighting
  inherits this.

## 4. Features by phase

### Phase 1 — core feature

1. Command **Pivot selected rows…**:
   - on the results grid right-click menu (`GUID_SQLEditorGroup:0x0070`, same group as
     Go to source / Find…)
   - placed on the Tools menu via `<CommandPlacements>`, not a second `<Button>`
   - `<CanonicalName>SsmsDataAnalyzer.PivotRows</CanonicalName>` so a shortcut can be
     assigned
   - no `DefaultInvisible`/`DynamicVisibility`, see the lessons from v0.9.x
2. Dockable **ToolWindowPane** (same pattern as Find in Results, so the keyboard works).
   The first column ("Column") is frozen and the grid is virtualized. Uses VS theme brushes.
3. Snapshot model (section 3) and banner: "Showing X of Y selected rows · N columns".
4. Ctrl+C copies the selected cells as tab-separated text (pastes into Excel).
5. Duplicate grid column names (`SELECT a.ID, b.ID`) are shown as `ID`, `ID (2)`. Rows are
   keyed by ordinal, never by name.
6. Header row labels are `Row <grid row number>`, using 1-based numbers as SSMS shows them.

### Phase 2 — comparing rows

7. **Highlight differing columns**: a column is marked when not all pivoted values are
   equal (ordinal comparison of display text).
8. Toggle **Show only differing columns**.
9. Toggle **Hide columns that are NULL in every row**.
10. **Column-name filter box** (substring, case-insensitive) for wide tables.

### Phase 3 — extras

11. **Choose the header column**: label each column by a chosen column's value (e.g.
    `ID = 4522`) instead of `Row 7`.
12. **Multiple pivots**: a new pivot opens a new tab, using a multi-instance tool window,
    instead of replacing the previous one.
13. **Go to source icon inside FK cells (DBeaver style)**. See section 5.
14. **Copy as Markdown table**, for Jira and Confluence bug reports.

## 5. Item 13 in detail — FK link icon inside cells

### Goal

Like DBeaver: a cell whose column is a declared foreign key shows a small link/arrow icon
aligned to the **right edge of the cell**. Clicking the icon opens the referenced row,
using exactly the same flow as the existing **Go to source…**:

- a new connected query window
- the query is auto-executed
- the query is filtered by that cell's value

The FK column's name cell can also carry a small 🔗 marker so FK columns are recognizable
before hovering.

### When the icon appears

A cell gets the icon only if **all** of these hold:

1. The pivot's source grid resolved cleanly: batch agreement per CONTRACT.md Amendments
   16/17, column count and names match.
2. That pivot row's grid column maps to a base column with a declared FK (reuse the FK
   metadata from Amendments 14/15, including multi-FK handling).
3. The cell value is not NULL and can be formatted by `SqlLiteralFormatter` (same rules as
   today: decline float/binary/MAX text).

If resolution fails, no icons are shown. The banner then shows a short explanation
(e.g. "FK links unavailable: query has 2 batches and none matched this grid"), the same
decline text Go to source already produces. Icons are never shown for a guessed table.

### How

- **Resolve once per pivot, not per cell.** `ResultsGridGoToSourceResolver` already
  describes the whole result shape to validate one clicked column. Split out a
  "describe + map every grid column to its base column + FK" step that returns a
  per-ordinal map, and have both Go to source and the pivot use it. One describe call per
  batch serves the whole pivot.
- **Asynchronous and non-blocking.** The pivot opens immediately with plain values. Icons
  appear when resolution finishes, with a cancellation token tied to closing the window.
  The describe timeout comes from the existing option.
- **Captured when the pivot opens:** the editor query text (or selection) and SSMS's own
  `UIConnectionInfo` for that tab. These are needed because the source tab may be closed or
  re-run later. The connection object is held in memory only, per CONTRACT.md Amendment 13:
  no password is extracted, stored, logged or shown.
- **Clicking the icon** calls the existing `TryOpenNewQueryWindowAsync` path with the
  resolved target (schema, table, column) and the literal value. No second implementation.
- **Rendering:** WPF `DataGridTemplateColumn` cell template with a `DockPanel`:
  - the value text fills the left
  - an icon `Button` is docked right; it is visible when the row is an FK and the cell
    value is not NULL
  - the icon uses a VS image moniker (e.g. `KnownMonikers.GoToReference`) so it follows
    the theme
  - tooltip: "Go to [ABB].[DCFund] where [ID] = 17"
  - clicking the icon must not change the cell selection, and the icon is not a tab stop
    (keyboard users use the context menu entry below)
- **Keyboard / context menu alternative:** right-click a pivot cell → **Go to source…**
  (same command, same enabled rules), so the feature isn't mouse-only.

### Edge cases

| Case | Behavior |
|---|---|
| Composite FK | No icon; same decline as Go to source today |
| Column with multiple FKs | Icon opens the chooser Go to source already uses |
| Source tab closed after the pivot opened | Still works; uses the captured connection info. If the connection is no longer usable, a status-bar message is shown |
| Query changed in the editor after pivoting | Irrelevant; resolution uses the captured text |
| Table names with dots (`[ABB].[ABB.Something]`) | Covered by existing bracket-quoting; add a test case |
| Windows / SQL login / Entra | Same `GridConnectionInfo` rules as Go to source (Entra declined) |

## 6. Architecture

| Piece | Where | Testable headless |
|---|---|---|
| `PivotBuilder`: selection ranges + column names + cell reader → rows, truncated flag, total selected, differ flags | Core/Pivot (netstandard2.0, pure) | ✅ unit tests |
| `PivotSelection`: row ranges → sorted distinct row indexes, count without visiting each row | Core/Pivot (pure over `RowRange`, no SSMS types) | ✅ unit tests |

Pure logic lives in **Core**, not Vsix: the test project references only Core. The Vsix
side converts `BlockOfCells` into `RowRange` and nothing more.
| Result-shape resolver (item 13) extracted from `ResultsGridGoToSourceResolver` | Vsix/ResultsGrid | ✅ against the seeded DB, same as today |
| `PivotRowsCommand`: menu command, reads the selection from the right-clicked grid (reuse `GridClickCapture`) | Vsix/Pivot | ❌ manual |
| `PivotToolWindow` + `PivotView.xaml` | Vsix/Pivot | ❌ manual |
| Options: `PivotRowLimit` (default 100, 1–500) | existing Options page | — |

Tests to add, at minimum:

- the selection-combining rules from section 2
- the limit and truncation banner numbers
- duplicate column names
- the differ calculation, including NULL display text
- the empty selection
- FK map for a join, a cross-database query and a dotted table name

## 7. Spike before Phase 1

1. **Does right-clicking change the selection?** If a right-click outside the current
   selection moves it to the clicked cell, read the selection in the mouse-down hook
   (`GridClickCapture`) rather than when the menu runs.
2. **How are whole-row selection and Ctrl+A represented** in `BlockOfCells`? They may use
   special column values or flags (e.g. -1 or a full-width block). Check this in the IL
   with `spikes/OeProbe` (rebuild it first) and confirm live.
3. **Which grid is "the" grid** when a tab has several result sets: confirm
   `GridClickCapture` identifies the right-clicked one (it already does for Go to source).

## 8. README / VERSION

- New README feature section "Pivot selected rows", written for PO/BA/QA: where to click,
  what the limit is, the snapshot note, and the NULL caveat.
- Keyboard shortcuts section: add `SsmsDataAnalyzer.PivotRows`.
- Bump the minor version per phase. Ship the `.vsix` to `dist/`.

## 9. Open decisions

| # | Question | Recommendation |
|---|---|---|
| D1 | Over the limit: show the first N with a banner, or refuse? | Show first N + banner |
| D2 | Columns: always all, or only the selected columns? | Always all (filter + "only differing" handle wide tables) |
| D3 | First release scope: Phase 1 only, or Phase 1 + 2? | 1 + 2 together; comparing rows is the main value |
| D4 | Item 13 timing: Phase 3, or pull into the first release? | Phase 3, after the resolver extraction is done and tested |

**Until the user says otherwise, the team builds to the recommendations** (D1 show first N,
D2 all columns, D3 Phase 1+2, D4 Phase 3 later).

## 10. Frozen interface (lead-owned — agents code against this, do not change it)

Namespace `SsmsDataAnalyzer.Core.Pivot`, folder `src/SsmsDataAnalyzer.Core/Pivot/`,
netstandard2.0. Grid rows are **0-based**; grid columns use the existing **1-based grid
index** (0 = gutter), the same convention as `GetCellDataAsString`.

```csharp
/// Inclusive range of grid rows. First <= Last, both >= 0.
public readonly struct RowRange
{
    public RowRange(long first, long last);
    public long First { get; }
    public long Last { get; }
}

public static class PivotSelection
{
    /// Distinct rows covered by all ranges (overlaps counted once).
    /// Must be O(ranges log ranges) — never enumerate rows (Ctrl+A on 1M rows).
    public static long CountDistinctRows(IEnumerable<RowRange> ranges);

    /// First `limit` distinct rows, ascending grid order. limit >= 1.
    public static IReadOnlyList<long> TakeDistinctRows(IEnumerable<RowRange> ranges, int limit);
}

public sealed class PivotColumn
{
    public int GridOrdinal { get; }      // 1-based grid column
    public string Name { get; }          // header text as the grid shows it
    public string DisplayName { get; }   // Name, or "Name (2)", "Name (3)" for repeats
    public bool Differs { get; }         // not all values ordinally equal (needs >= 2 rows)
    public bool AllNull { get; }         // every value == PivotBuilder.NullDisplayText
}

public sealed class PivotResult
{
    public IReadOnlyList<long> Rows { get; }            // pivoted grid rows, ascending
    public IReadOnlyList<PivotColumn> Columns { get; }  // grid order
    public string[][] Values { get; }                   // [columnIndex][rowIndex]; never null (null cell text -> "")
    public long TotalSelectedRows { get; }
    public bool Truncated { get; }                      // TotalSelectedRows > Rows.Count
    public int DifferingColumnCount { get; }
}

public static class PivotBuilder
{
    public const string NullDisplayText = "NULL";
    public const int DefaultRowLimit = 100, MinRowLimit = 1, MaxRowLimit = 500;

    /// columnNames[i] is grid column i+1. readCell(row, gridOrdinal) returns display text.
    /// rowLimit is clamped to [MinRowLimit, MaxRowLimit]. Empty selection -> empty Rows,
    /// no exception. Calls readCell only for pivoted rows.
    public static PivotResult Build(
        IReadOnlyList<string> columnNames,
        IEnumerable<RowRange> selection,
        int rowLimit,
        Func<long, int, string> readCell);
}
```

**Allocated IDs (lead):**

| Item | Value |
|---|---|
| `PackageIds.PivotRowsCommandId` | `0x0202` |
| Pivot tool window persistence GUID | `1de89e5c-9c40-455e-9ad7-3441a767b7f9` |
| CanonicalName | `SsmsDataAnalyzer.PivotRows` |
| Option | `PivotRowLimit` (int, default 100), category next to `AutoExecuteGoToSourceQuery` |

## 11. Team

Designed for **low token use**:

- One frozen interface, so agents don't need to talk to each other.
- Disjoint file ownership, so no worktrees are needed.
- Small models wherever the work is fully specified.
- Opus only where a bug would silently jump to the wrong table.

### Roster

| Agent | Model | Wave | Owns (exclusive) | Why this model |
|---|---|---|---|---|
| **Lead** | Opus (this session) | all | this file, `TEAM.md`, the release: build `.vsix`, `dist/`, version bump, commit/push | Integration and verification need the full project history |
| **S1 grid-spike** | Sonnet | 1 | `spikes/OeProbe/`, appends to `docs/resultsgrid-api.md` | Bounded IL lookup with a known tool; answers 3 yes/no questions |
| **P1 pivot-core** | Sonnet | 1 | `src/SsmsDataAnalyzer.Core/Pivot/`, `tests/SsmsDataAnalyzer.Tests/Pivot/` | Fully specified pure logic + xUnit; the spec above is the whole task |
| **U1 pivot-shell** | Sonnet | 2 | `src/SsmsDataAnalyzer.Vsix/Pivot/` + the listed lines in `VSCommandTable.vsct`, `PackageGuids.cs`, `DataAnalyzerPackage.cs`, `Options/DataAnalyzerOptionsPage.cs`, `Options/OptionsAccessor.cs` | Follows existing patterns (Find tool window, Go to source command); the pitfalls list replaces Opus-level judgement |
| **D1 docs** | Haiku | 3 | `README.md` feature section + shortcut row, `dist/VERSION.md` entry | Pure writing from a bullet list the lead supplies; no code reading |
| **R1 fk-resolver** | Opus | Phase 3 | `src/SsmsDataAnalyzer.Vsix/ResultsGrid/` resolver split | Refactors correctness-critical code; a mistake jumps to the wrong table |
| **U2 fk-icons** | Sonnet | Phase 3 | `src/SsmsDataAnalyzer.Vsix/Pivot/` icon template + click wiring | UI on top of R1's finished, tested map |

### Waves

```
Wave 1 (parallel, background) ── S1 grid-spike ─┐
                               └─ P1 pivot-core ─┤
Wave 2 ───────────────────────── U1 pivot-shell ─┤  (needs S1 answers + P1 API compiled)
Wave 3 ── Lead: build, user test ── D1 docs ── Lead: release 0.11.0
Phase 3 (only after user feedback) ── R1 ── U2 ── Lead: release
```

Wave 1 agents cannot collide:

- S1 builds only OeProbe.
- P1 builds only Core and the tests.
- The VSIX project is built only in Wave 2 (by U1) and then by the lead.

### Token rules (every brief includes these)

1. **Read only the files listed in your brief.** No repo-wide exploration. If something
   missing blocks you, stop and report it.
2. **Never change the frozen interface (§10).** Report a needed change instead.
3. **Report in 15 lines or fewer:**
   - files changed
   - the test/build summary line
   - deviations
   - open questions
   
   No pasted code.
4. **Only the lead** builds the release `.vsix`, edits `dist/`, commits or pushes.
5. The lead verifies with the `dotnet test` summary, `git diff --stat`, and the
   interface-facing files only.
6. Fixes go to the **same agent** via SendMessage, never a fresh spawn.
7. No agent runs queries against user databases. The seeded `SsmsDataAnalyzerTest` DB is
   used only in Phase 3 (R1).
8. Never log or put cell values in error messages; they can be sensitive business data.

### Briefs

**S1 grid-spike (Sonnet).**

- Read first: `docs/resultsgrid-api.md` and the `spikes/OeProbe` README/Program.
- Rebuild OeProbe first (`dotnet build -c Release`, use `bin/Release/net8.0/OeProbe.dll`);
  the old binary is stale.
- Using IL of the SSMS grid assemblies, answer:
  1. Does a right mouse-down on a cell outside the current selection change
     `SelectedCells` before the context menu opens?
  2. How are whole-row selection (gutter click) and Ctrl+A represented in
     `BlockOfCells`? Give exact field values: X, Y, Right, Bottom, or sentinels like -1.
  3. What are `BlockOfCells`' public members and which index space do they use?
- Append a "Pivot selection" section to `docs/resultsgrid-api.md` with evidence
  (type/method names).
- Also write a 2-step manual check the user can run in SSMS to confirm answers 1 and 2.

**P1 pivot-core (Sonnet).**

- Read first: §2, §3, §10 of this file; one existing test file under `tests/` for style;
  `src/SsmsDataAnalyzer.Core/Export/ProfileFormat.cs` for code style.
- Implement §10 exactly.
- Tests must cover:
  - overlapping, adjacent, unordered and duplicate ranges
  - a 1M-row range (must be instant)
  - limit truncation and clamping
  - the empty selection
  - duplicate column names → `(2)`, `(3)`
  - `Differs` with 1 row (false) and 2+ rows
  - `AllNull`
  - null cell text → `""`
  - `readCell` is called only for pivoted rows
- Run `dotnet test`. All existing 75 tests must still pass.

**U1 pivot-shell (Sonnet).**

- Read first:
  - §1–§4 and §10 of this file, plus S1's section in `docs/resultsgrid-api.md`
  - `ResultsGrid/ResultsGridFindCommand.cs`, `ResultsGrid/GridFindToolWindow.cs`,
    `ResultsGrid/GridClickCapture.cs`, `ToolWindow/ProfileView.xaml`
  - `VSCommandTable.vsct`
- Implement Phase 1+2 UI: command, tool window, dynamic columns, banner, the 3 toggles,
  the filter box, Ctrl+C, and the `PivotRowLimit` option.
- Build the Vsix in Release, but do not install it and do not copy it to `dist/`.

Pitfalls (all learned the hard way in this repo):

- `.vsct`: define the command once in `<Buttons>`. Put the Tools menu entry in
  `<CommandPlacements>`. No `DefaultInvisible`/`DynamicVisibility`. Parent group is the
  results-grid group `GUID_SQLEditorGroup:0x0070`; copy the Find… button.
- WPF: every binding to a read-only property must be `Mode=OneWay`. A TwoWay default
  once blanked the whole grid.
- Keyboard: use a `ToolWindowPane`, not a floating `Window`. Do not bind Ctrl+F or F3
  globally.
- Colors: use VS theme brushes only (see `ProfileView.xaml`, `GridThemeColors.cs`).
- Snapshot everything at command time. Keep no reference to `IGridStorage` after
  `PivotBuilder.Build` returns.
- Threading and services: call `ThreadHelper.ThrowIfNotOnUIThread()` inside command
  lambdas (VSTHRD010). Cast to `(System.IServiceProvider)_package` before `GetService`.
- Failures go to the status bar plus `OeDiagnostics.Error`, never an unhandled exception.

**D1 docs (Haiku).**

- Read only `README.md` and `dist/VERSION.md`.
- Add a "Pivot selected rows" feature section in the existing PO/BA/QA style: where to
  click, the limit, the snapshot note, and the NULL caveat.
- Add a keyboard-shortcut row and a VERSION entry from the lead's bullet list.
- Change nothing else.

