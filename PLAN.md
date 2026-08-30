# SSMS Data Analyzer — Implementation Plan

A table **data profiler** for SQL Server Management Studio: right-click a table in
Object Explorer → get a per-column report (fill rate, distinct values, last fill date).

---

## 1. Environment (verified 2026-08-25)

| Item | Value |
|---|---|
| SSMS 22 | `22.9.12105.275`, `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE` — **the only target** |
| Shell | Visual Studio **18.x**, amd64 (the bundled Copilot VSIX targets `[18.0, 19.0)`) |
| VSIX install target | `Id="Microsoft.VisualStudio.Ssms" Version="[22.0,)"`, `<ProductArchitecture>amd64</ProductArchitecture>` |
| 3rd-party VSIX proven working | `Extensions\SQLPrompt`, `Extensions\SqlLizard` |
| Useful assemblies in IDE dir | `ObjectExplorer.dll`, `sqlmgmt.dll`, `SqlWorkbench.Interfaces.dll`, `Microsoft.SqlServer.Smo.dll`, `Microsoft.SqlServer.ConnectionInfo.dll` |
| User ext hive | `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_*\Extensions` |
| **Missing prerequisite** | **VS "Visual Studio extension development" workload is NOT installed** (checked Community + Professional) |

**Target: SSMS 22 only.** Manifest pinned to `[22.0,)`; no compatibility shims, no VS 17 shell
branches, no runtime probing for older Object Explorer API shapes. Runtime: **.NET Framework
4.7.2/4.8** for the VSIX — the VS 18 shell is still Framework-hosted.

---

## 2. The feature

### Object Explorer → right-click a table → **Analyze Data**

Opens a dockable tool window with one row per column:

| Column | Meaning | Cost |
|---|---|---|
| Column / Type / Nullable / Identity / PK | metadata | free (`sys.columns`) |
| **Rows** | `COUNT_BIG(*)` total rows | pass 1 |
| **Filled** | `COUNT_BIG([col])` — non-NULL | pass 1 |
| **Fill %** | Filled / Rows | derived |
| **Blank** | `''` / all-whitespace count (string columns) | pass 1 |
| **Distinct** | exact `COUNT(DISTINCT)` among filled rows | pass 2 (the expensive one) |
| **Distinct %** | Distinct / Filled — selectivity | derived |
| **Last fill** | `MAX(DateCreated)` over rows where that column IS NOT NULL | pass 1 |
| **Min / Max** | for orderable types | pass 1 |
| **Avg length** | `SUM(DATALENGTH)/COUNT` | pass 1 |
| **Flags** | `DEAD` (0 filled), `CONSTANT` (distinct = 1), `UNIQUE` (distinct = rows), `SPARSE` (fill < 5%) | derived |

*Last fill* is the "when was this column last actually populated" signal — the single most
useful metric for deciding whether a column is still in use.

### DateCreated resolution

1. Look for a column literally named **`DateCreated`** (case-insensitive) — the primary rule.
2. If absent, fall back to a configurable ordered candidate list:
   `CreatedDate, CreatedOn, Created, InsertDate, DateInserted, RowCreatedAt, ModifiedDate`.
3. If still absent, *Last fill* is greyed out as `n/a` with a tooltip naming the columns that
   were searched. Everything else still profiles normally.
4. The resolved column is shown in the tool window header, with a dropdown of every
   datetime-typed column so the user can override it.

---

## 3. SQL strategy (the part that decides whether this is usable)

### Pass 0 — metadata + row estimate (instant, never touches the table)

```sql
SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable, c.is_identity, c.column_id
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@table);

SELECT SUM(ps.row_count)
FROM sys.dm_db_partition_stats ps
WHERE ps.object_id = OBJECT_ID(@table) AND ps.index_id IN (0, 1);
```

The estimate drives the guardrail: above a configurable threshold (default **10M rows**) the
UI warns and pre-selects sampling / approximate mode *before* running anything.

### Pass 1 — one scan for everything except distinct counts

Generated dynamically across all N columns:

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    COUNT_BIG(*)                                               AS [_TotalRows],
    COUNT_BIG([Col1])                                          AS [Col1$filled],
    MAX(CASE WHEN [Col1] IS NOT NULL THEN [DateCreated] END)   AS [Col1$lastfill],
    MIN([Col1])                                                AS [Col1$min],
    MAX([Col1])                                                AS [Col1$max],
    SUM(CAST(DATALENGTH([Col1]) AS BIGINT))                    AS [Col1$bytes],
    SUM(CASE WHEN LTRIM(RTRIM([Col1])) = '' THEN 1 ELSE 0 END) AS [Col1$blank]
    /* ... repeated per column ... */
FROM [sch].[tbl];
```

**One table scan** yields fill counts, last-fill dates, min/max and average length for every
column at once. Emit only the aggregates each type actually supports (no `MIN` on `xml`, no
blank-check on numerics).

### Pass 2 — distinct counts, **exact only**

`APPROX_COUNT_DISTINCT` is not used anywhere in this tool. Every distinct count is a true
`COUNT(DISTINCT …)`, and the whole pass is engineered around making that affordable.

Each distinct aggregate needs its own sort-or-hash distinct operator, so a single query with
40 of them produces a plan with a spool feeding 40 distinct branches — a memory-grant and
tempdb-spill hazard on a production server. The pass is therefore **split by how each column
can be counted**:

**2a. Index-backed columns (cheap — do these first).** Any column that is the *leading key* of
an existing index gets its own single-column query:

```sql
SELECT COUNT_BIG(*) FROM (SELECT DISTINCT [Col] FROM [sch].[tbl] WITH (INDEX(ix_name))) d;
```

This is a narrow index scan, not a table scan — often orders of magnitude cheaper. Identify
them from `sys.index_columns WHERE key_ordinal = 1`.

**2b. Everything else — batched.** Remaining columns are grouped into batches of a
**configurable size (default 8)** and run sequentially:

```sql
SELECT COUNT(DISTINCT [Col3]) AS [Col3$distinct],
       COUNT(DISTINCT [Col7]) AS [Col7$distinct],
       /* … up to batch size … */
FROM [sch].[tbl]
OPTION (MAX_GRANT_PERCENT = 25);
```

- `MAX_GRANT_PERCENT` caps how much of the server's memory one profiling query can reserve —
  it may spill to tempdb, but it will not starve the real workload.
- LOB and wide string columns (`nvarchar(max)`, `varchar(max)`, long `nvarchar(n)`) are the
  most expensive distincts by far; give them **their own batch of one** and place them last so
  the user can cancel before paying for them.
- Batches run sequentially with `IProgress` reporting, and cancellation is honoured *between*
  batches as well as inside one — so partial results are always kept, never discarded.

**Progressive UI.** Pass 1 returns fast and renders the full grid; the Distinct / Distinct %
cells show a spinner and fill in batch by batch. The window is useful within seconds even when
the distinct pass runs for minutes.

**Cost preview before running.** Using the pass-0 row estimate, the tool shows
"≈ *k* scans over *n* rows" and a per-column checklist, defaulted so the user can deselect
expensive columns before a single query is issued. Above the row threshold (default 10M) the
run requires explicit confirmation.

### Sampling — restricted on purpose

`TABLESAMPLE SYSTEM (@pct PERCENT)` is available for pass 1 (fill counts, min/max, last-fill
date scale or hold up honestly under sampling) but is **disabled for pass 2**. Distinct counts
from a sample cannot be extrapolated — a 1% sample of a 10M-row table tells you almost nothing
about true cardinality, and scaling it linearly produces confidently wrong numbers. When
sampling is on, the Distinct column is blanked with an explanatory tooltip rather than filled
with a guess. Exact distinct means exact distinct or nothing.

### Correctness / safety rules

- Always `READ UNCOMMITTED` — never block a production workload.
- Configurable **query timeout** (default 120 s) and a real **Cancel** wired to
  `SqlCommand.Cancel()` plus a `CancellationToken`.
- Optional `OPTION (MAXDOP n)` hint.
- Type exclusion table: `text`/`ntext`/`image`, `xml`, `geography`, `geometry`, `hierarchyid`,
  `varbinary(max)`, CLR UDTs — metadata only, aggregates skipped with a reason tooltip rather
  than a failed query.
- **Batch against the select-list limits**: chunk pass 1 into groups of ~60 columns so wide
  tables (200+ columns) don't exceed the 1024-expression limit.
- Identifiers escaped by bracket-doubling; schema/table names bound as parameters wherever
  possible. No concatenation of user values into SQL text.
- Permissions: catch errors 229/230 (`SELECT` denied) and show a clear message, not a stack trace.

---

## 4. Architecture

```
SsmsDataAnalyzer.sln
├── src/SsmsDataAnalyzer.Core/        netstandard2.0 — zero VS dependencies
│   ├── Model/          TableRef, ColumnMeta, ColumnProfile, TableProfile, ProfileOptions
│   ├── Metadata/       SchemaReader        (sys.columns, sys.index_columns, row estimate)
│   ├── Sql/            ProfileSqlBuilder   (pass 1 / pass 2 generation, escaping)
│   │                   DistinctPlanner     (index-backed vs batched, LOB isolation, ordering)
│   ├── TableProfiler.cs                    (orchestration, IProgress<T>, CancellationToken)
│   └── Export/         MarkdownExporter, CsvExporter
├── src/SsmsDataAnalyzer.Vsix/        net472 — VSPackage
│   ├── DataAnalyzerPackage.cs        AsyncPackage, command registration
│   ├── Commands/       AnalyzeTableCommand, AnalyzeDatabaseCommand
│   ├── ObjectExplorer/ OeContextBridge.cs  (late-bound reflection into ObjectExplorer.dll)
│   ├── ToolWindow/     ProfileToolWindow.cs, ProfileView.xaml, ProfileViewModel.cs
│   ├── Options/        DataAnalyzerOptionsPage (DialogPage)
│   └── source.extension.vsixmanifest, VSCommandTable.vsct
├── src/SsmsDataAnalyzer.Cli/         net8.0 — same Core, scriptable / CI profiling
└── tests/SsmsDataAnalyzer.Tests/     xUnit — SQL-generation snapshots + LocalDB integration
```

Core is deliberately separate: the SQL generation and profiling logic is the valuable,
testable part and must not depend on the VS shell. It also gives a working CLI fallback if
the VSIX integration hits a wall in SSMS 22.

### Object Explorer integration — three tiers, build in this order

- **Tier B (build first, guaranteed to work):** top-level menu item + tool window with its own
  server / database / table pickers, seeded from the active query window's connection
  (`DTE.ActiveDocument` → `SqlWorkbench.Interfaces` / `ServiceCache.ScriptFactory`). No
  dependence on undocumented APIs.
- **Tier A — VERIFIED FEASIBLE on SSMS 22 (Agent C spike, see `docs/oe-api.md`).** A real
  right-click item on table nodes via
  `Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer`. The original plan assumed
  late-bound reflection against an unsupported API; the spike disproved that assumption —
  **every type on the path is public** (`IObjectExplorerService`, `IMenuHandler`,
  `IWinformsMenuHandler`, `INodeInformation`, and `HierarchyObject` with a public
  `AddChild(string, object)`). Use **compile-time references**, wrapped in try/catch behind a
  feature flag: same degrade-to-Tier-B safety, without tripling the size of `OeContextBridge.cs`
  for nothing.

  **The OE node context menu is WinForms, not `.vsct`.** `ExplorerHierarchyNode.ShowContextMenu`
  builds a `ContextMenuStrip` from `IWinformsMenuHandler.GetMenuItems()`. The legacy `CommandID`
  branch still exists but is dead code for SQL nodes — **a `.vsct` group parented to the OE
  context menu will silently never appear.** `.vsct` stays for Tier B/C only.

  Two non-optional implementation constraints:
  1. `GetMenuItems()` **must never throw** — it runs inside SSMS's menu construction, so one
     exception kills the *entire* node context menu, not just our item. Wrap the body; return an
     empty array on error.
  2. **De-dupe the injection** with a reference-identity set. `CurrentContextChanged` fires per
     selection and `AddChild` appends unconditionally, so unguarded you get one duplicate menu
     item per click.
- **Tier C (cheap extra):** a T-SQL editor command — profile the table named under the cursor.

### Tool window UX

WPF `DataGrid`: sortable columns, filter box, colour-scaled Fill % bar, flag badges, row count
and elapsed time in the status strip, live **Cancel** during a run. Buttons: *Copy as
Markdown*, *Export CSV*, *Export Excel*, and **Script the query** — drops the generated T-SQL
into a new query window. That last one makes the whole extension auditable and is what will
win over DBAs.

---

## 5. Milestones

| # | Goal | Exit criterion |
|---|---|---|
| **M0** | *De-risk.* ~~Install the VS extension-development workload~~ (proved unnecessary — Amendment 5). Hello-world VSIX in SSMS 22. ~~Spike the Object Explorer API~~ **DONE — Tier A confirmed feasible.** | Remaining: VSIX packages and loads inside SSMS 22 |
| **M1** | `Core`: schema reader, pass 1 SQL builder, `DistinctPlanner` + exact pass 2, profiler orchestration, CLI front end | `analyze --server . --db X --table dbo.Y` prints a correct report; distinct counts verified against hand-written `COUNT(DISTINCT)` on a seeded table; unit tests green |
| **M2** | Tool window UI bound to Core, Tier B connection pickers, progress + cancel | Profile a real table end-to-end from inside SSMS |
| **M3** | Tier A Object Explorer context menu (feature-flagged), Tier C editor command | Right-click → Analyze Data works on SSMS 22 |
| **M4** | DateCreated resolution + override dropdown, cost preview + column checklist, progressive distinct fill-in, `MAX_GRANT_PERCENT` / batch-size options, type exclusions, wide-table batching, exports | Survives a 50M-row / 200-column table: grid usable in seconds, distinct pass cancellable, SSMS never freezes, server memory grant stays capped |
| **M5** | Options page, icon, README, signed VSIX, install docs; optional Marketplace listing | `VSIXInstaller.exe` install on a clean SSMS 22 works |

---

## 6. Build & debug loop

- Build the VSIX with `Microsoft.VSSDK.BuildTools`; reference SSMS assemblies straight from the
  IDE folder with `<Private>false</Private>` — never copy them into the VSIX.
- Debug by launching `SSMS.exe /rootsuffix Exp` as the external program, so the experimental
  hive keeps the daily-driver SSMS clean.
- Test-install: `VSIXInstaller.exe /instanceIds:<ssms22-id> SsmsDataAnalyzer.vsix`, or drop the
  folder into `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_*\Extensions`.

---

## 7. Open decisions

1. ~~Distinct default~~ — **decided: exact `COUNT(DISTINCT)` always.** No approximation, no
   sampled cardinality. The cost is paid for with the index-backed fast path, batching, a cost
   preview, per-column opt-out and progressive rendering.
2. ~~SSMS 21 support~~ — **decided: SSMS 22 only.**
3. **Distinct batch size** — default 8 is a guess; tune it in M4 against a real wide table.
   Larger batches mean fewer scans but a bigger memory grant and more spill risk.
4. **Database-level profiling** ("analyze every table") — worth having, but schedule it after
   M4: with exact distinct counts it needs its own queueing, throttling and result-caching
   design, since it is no longer a cheap operation to fan out.
