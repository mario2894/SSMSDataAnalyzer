# Query results grid integration on SSMS 22 — spike report (Agent C)

Target verified: **SSMS 22.9.12105.275**,
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`.

All binary findings come from `spikes/OeProbe` (pure `System.Reflection.Metadata`; nothing was
loaded or executed) against the shipped SSMS assemblies and the two third-party extensions
installed on this machine. All SQL findings come from **live queries against the local
`MSSQLSERVER` instance** (read-only; Agent D's `SsmsDataAnalyzerTest` was read, never modified).
Where something is inferred rather than observed, it says so.

---

## 1. VERDICT

> ## (a) Adding a menu item: **FEASIBLE.** Low risk, standard `.vsct`.
> The results-grid context menu is a **real VS `ctmenu` menu**, not a WinForms
> `ContextMenuStrip`. It is shown by
> `IVsUIShell.ShowContextMenu(0, {33F13AC3-80BB-4ECB-85BC-225435603A5E}, 0x0070, …)`.
> A `.vsct` `<Group>` parented to that guid:id merges in exactly like any VS context menu
> extension. **Red Gate SQL Prompt 11 does precisely this on this machine today.**
> Note this is the *opposite* of the Object Explorer finding in `docs/oe-api.md`: there
> `.vsct` was dead code, here `.vsct` is the correct and proven mechanism.
>
> ## (b) Getting the cell value + column name: **FEASIBLE**, public API, no reflection.
> `GridControl` (public) → `.GridStorage` (public) → cast to
> `Microsoft.SqlServer.Management.QueryExecution.IGridResultSet` (**public interface**) →
> `GetCellData(row, col)`, `ColumnNames`, `GetSchemaRow(col)`.
>
> ## (b) Mapping a result column back to a BASE TABLE / BASE COLUMN: **FEASIBLE-BUT-PARTIAL.**
> **The grid does not retain it and cannot be made to.** SSMS executes with
> `CommandBehavior.Default` — I read the IL and then proved by live test that this leaves
> `BaseTableName`/`BaseSchemaName` **empty** and `BaseColumnName` equal to the *alias*, not the
> base column. The only working route is to re-describe the query text server-side with
> **`sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)`** — and the `1` (browse
> information) is load-bearing: with `0`, every `source_*` column is NULL.
> That route resolves plain and aliased column references correctly and returns NULL for
> expressions, aggregates and UNIONs — i.e. it declines exactly where it should. It fails
> outright on temp tables in multi-statement batches, and it only describes the **first**
> result set.
>
> **Overall: FEASIBLE-BUT-FRAGILE**, and the fragility is concentrated entirely in "which
> query text produced this grid", not in the menu or the cell access.

Recommended posture: ship the menu item and the value/column-name extraction (both solid);
treat base-table resolution as a **best-effort enrichment that must decline loudly** when the
preconditions in §6.4 are not all met. Never guess a table.

---

## 2. What renders the results-grid context menu (the key discovery)

`SQLEditors.dll` → `Microsoft.SqlServer.Management.UI.VSIntegration.Editors.DisplaySqlResultsTabControl::WndProc`,
IL, verbatim decode:

```
IL_0000: ldarg.1 ; Message::get_Msg
IL_0006: ldc.i4.s     123                       // WM_CONTEXTMENU (0x7B)
IL_000A: ldarg.0 ; Control::GetContainerControl ; IContainerControl::get_ActiveControl
IL_0017: isinst       Microsoft.SqlServer.Management.UI.Grid.GridControl   // <-- only for a grid
IL_0024: call         CommonUtils::GetCoordinatesForPopupMenuFromWM_Context
IL_0031: ldtoken      Microsoft.VisualStudio.Shell.Interop.IVsUIShell ; GetService
IL_0045: ldc.i4.s     112                       // <-- IDM_SQLWB_SQLRESGRID_CONTEXT (0x0070)
IL_004A: TabControl::get_SelectedTab ; isinst IOleCommandTarget
IL_0054: call         CommonUtils::DisplayPopupMenu
```

and `CommonUtils::DisplayPopupMenu` (3-arg overload → 6-arg overload):

```
IL_0006: ldsflda  SQLWorkbenchCommands::GUID_SQLEditorGroup
...
IL_0035: callvirt Microsoft.VisualStudio.Shell.Interop.IVsUIShell::ShowContextMenu
```

`SQLWorkbenchCommands::.cctor` gives the guid, and the constant table gives the id
(both read from metadata, not guessed):

```
GUID_SQLEditorCommandSet = "52692960-56BC-4989-B5D3-94C47A513E8D"
GUID_SQLEditorGroup      = "33F13AC3-80BB-4ecb-85BC-225435603A5E"    // <-- the menu's guid

IDM_SQLWB_SQLRESGRID_CONTEXT = 112   (0x0070)   // <-- the results GRID menu
IDM_SQLWB_SQLRESMSG_CONTEXT  =  96   (0x0060)   // messages pane
IDM_SQLWB_SQLSCRIPT_CONTEXT  =  80   (0x0050)   // T-SQL editor (useful for Tier C)
IDM_SQLWB_EXECPLAN_CONTEXT   = 128   (0x0080)
IDM_SQLWB_ASRESGRID_CONTEXT  =  83               // Analysis Services grid — NOT ours
```

Built-in command ids in `GUID_SQLEditorCommandSet`, for reference / neighbouring placement:
`cmdidWBSelectAll = 100`, `cmdidWBSaveAs = 102`, `cmdidWBPrintPreview = 103`,
`cmdidDisplayCellProp = 60`, `cmdidShowFormattedValues = 61`.

**Consequence, and it is the mirror image of the OE spike:** for the results grid, a `.vsct`
group **does** work. There is no `IWinformsMenuHandler`-style hook here at all.

---

## 3. Evidence from the two shipping extensions

### 3.1 Which menu items are whose (asked explicitly — answered by binary search)

| Menu item | Owner | Evidence |
|---|---|---|
| Copy / Copy with Headers / Select All / Save Results As… / Print… | **SSMS built-in** | `SQLWorkbenchCommands.cmdidWBSelectAll/WBSaveAs/WBPrintPreview`; `GridResultsTabPageBase::OnCopy`, `::OnCopyWithHeaders`, `::OnPrint` |
| **Script as INSERT** | **Red Gate SQL Prompt 11** | `RedGate.SqlPrompt.CommonUI.dll` → `…Commands.Common.Execution.ResultsGrid.ScriptResultsAsInsertCommand` |
| **Copy as IN clause** | **Red Gate SQL Prompt 11** | `…ResultsGrid.CopyAsInClauseCommand` |
| **Open in Excel** | **Red Gate SQL Prompt 11** | `…ResultsGrid.OpenInExcelCommand` |
| **Show Aggregate Results** | **Red Gate SQL Prompt 11** | `…ResultsGrid.ShowAggregateResultsCommand` |
| **SQL Lizard ▸** submenu | **SQL Lizard 2.0** | `SSMSLizardDataGrid.dll` → `SSMSLizardCore.Integration.ActiveWindow.ResultsGridContextMenuInjector` |

Nothing in the user's grid menu except the first row is a Microsoft item. A binary grep of the
whole SSMS tree for "Open in Excel" / "Show Aggregate Results" / "Script as INSERT" hits **only**
`RedGate.SqlPrompt.CommonUI.dll`.

### 3.2 Red Gate SQL Prompt — the `.vsct` route (this is the one to copy)

`Extensions\SQLPrompt\RedGate.SQLPrompt.SsmsPackage22.pkgdef`, verbatim, last two lines:

```
[$RootKey$\AutoLoadPackages\{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}]
"{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}"=dword:00000002
[$RootKey$\Menus]
"{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}"=", Menus.ctmenu, 1"
```

and their compiled command table really is there — `RedGate.SqlPrompt.SsmsPackage22.dll`
carries a managed resource set whose **resource name is literally `Menus.ctmenu`**, holding a
`CFCT` v5 command-table blob (31,652 bytes at file offset `0x35BA`). SSMS's own
`SQLEditors.dll` stores its command table the same way, inside
`Microsoft.SqlServer.Management.UI.VSIntegration.Editors.Resources.resources`, also named
`Menus.ctmenu`. Both blobs are the compressed CFCT v5 format, so I could **not** decompress
them to read the group placements — that one link is inferred, not read.

Command wiring, from `RedGate.SqlPrompt.ShellAbstraction.22.dll::MenuCommandsRegistry::AddCommand`:

```
IL_001F: newobj   PromptOleMenuCommand::.ctor          // : OleMenuCommand
IL_002C: callvirt System.ComponentModel.Design.IMenuCommandService::AddCommand
```

Their `MenuCommandInfo(int id, Guid guid, ICommand)` builds the `CommandID`.
A byte scan of the entire Red Gate install finds **no** use of
`Microsoft.VisualStudio.CommandBars`, `AddNamedCommand`, `IVsProfferCommands`, or any
results-grid `ContextMenuStrip`. So the only mechanism left that can put their four items on
this menu is the ctmenu/`.vsct` one. **That is the empirical proof that `.vsct` works here** —
and it also proves that VS's command routing reaches a package-level
`OleMenuCommandService` even though `ShowContextMenu` is called with the
`GridResultsTabPage` as `pCmdTrgtActive`.

### 3.3 SQL Lizard — the DTE `CommandBars` route (works, but don't copy it)

`SSMSLizardDataGrid.dll` → `SSMSLizardCore.Integration.ActiveWindow.ResultsGridContextMenuInjector.InitializeAsync`,
decoded from the async state machine IL:

```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
var bars = (await package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.CommandBars
           as Microsoft.VisualStudio.CommandBars.CommandBars;
CommandBar bar = null;
foreach (var name in new[] { "SQL Results Grid Tab Context", "Results Grid",
                             "SQL Results Grid", "Query Results", "SQL Results", "Result Grid" })
    if ((bar = TryGetCommandBar(bars, name)) != null) break;      // bars[name], swallow on throw
bar ??= FindLikelyResultsGridMenu(bars);                          // heuristic, see below
if (bar == null) return;

RemoveExisting(bar, "SQL Lizard");                                // de-dupe by caption
_rootPopup = (CommandBarPopup)bar.Controls.Add(
                 MsoControlType.msoControlPopup, Missing, Missing, Missing, /*Temporary*/ true);
_rootPopup.Caption = "SQL Lizard";
_btnShowInGrid = AddButton(_rootPopup.CommandBar, "Show Results in Data Grid",
                           new _CommandBarButtonEvents_ClickEventHandler(OnShowInGrid), beginGroup: false);
// … + Pivot Table, Create Dashboard, Apply Dashboard Layout, Copy Results as HTML,
//     Copy Selection as HTML, Add Results to Notes, Add Selection to Notes, Export to Excel
```

`AddButton` = `bar.Controls.Add(msoControlButton, …, Temporary: true)` then sets
`Caption`/`Enabled`/`Visible`/`BeginGroup` and `+= Click`.

`FindLikelyResultsGridMenu` scans every `CommandBar` with `Type == 2` (`msoBarTypePopup`) and
picks the first whose control captions (with `&` stripped) contain **all** of
`"Copy"`, `"Save"`, `"Copy with Headers"`, `"Select All"`, `"Results"`. That caption
heuristic is a nice independent confirmation of *which* menu we are talking about — and it is
also why we should not copy this approach: it breaks on any localised SSMS.

Other weaknesses of the CommandBars route: items are added once at package load and are
**static** (no per-cell `BeforeQueryStatus` enable/disable), and `Temporary:true` items are
rebuilt every session. Use it only as a fallback if the `.vsct` group ever stops merging.

(For completeness: SQL Lizard's *editor* commands — Lint, Format, etc. — use the normal
`OleMenuCommandService` + `CommandID` route, in `QueryEditorMenuCommands`, ids 256–260.)

---

## 4. Getting to the grid, the cell, the value and the column name

### 4.1 The object graph (verified from IL)

```
SqlScriptEditorControl                      PUBLIC   SqlEditors.dll  (the DocView)
  └ m_sqlResultsControl : DisplaySQLResultsControl        private field, internal type
      └ m_gridResultsPage : GridResultsTabPage            private field, internal type
          ├ FocusedGrid : GridControl                     public prop on internal type
          ├ m_gridContainers : List<ResultSetAndGridContainer>
          └ GetGridResultSet(GridControl) : QEResultSet   private method
```

`ResultSetAndGridContainer::Initialize` does
`IGridControl::set_GridStorage(m_rs)` — **so `grid.GridStorage` *is* the `QEResultSet`.**
That single fact removes the need for most of Red Gate's reflection:

```
QEResultSet : IGridStorage, IGridResultSet          // internal class, PUBLIC interfaces
```

| Type | Assembly | Visibility |
|---|---|---|
| `Microsoft.SqlServer.Management.UI.Grid.GridControl` | `Microsoft.SqlServer.GridControl.dll` (22.200.0.0) | **public** |
| `…UI.Grid.IGridControl` / `IGridStorage` / `HitTestInfo` / `BlockOfCells(Collection)` / `GridColumnInfo` | same | **public** |
| `Microsoft.SqlServer.Management.QueryExecution.IGridResultSet` | `SQLEditors.dll` — assembly name **`SqlEditors`**, 22.200.0.0 | **public** |
| `…Editors.SqlScriptEditorControl` / `ScriptAndResultsEditorControl` / `ScriptEditorControl` | `SqlEditors` | **public** |
| `…Editors.GridResultsGrid` / `GridResultsTabPage` / `DisplaySQLResultsControl` / `QEResultSet` / `ResultSetAndGridContainer` | `SqlEditors` | internal |

`IGridResultSet` in full (the whole interface — it is small and it is exactly what we need):

```csharp
namespace Microsoft.SqlServer.Management.QueryExecution
{
    public interface IGridResultSet
    {
        int    NumberOfDataColumns { get; }
        long   TotalNumberOfRows   { get; }
        System.Collections.Specialized.StringCollection ColumnNames { get; }
        string    GetCellDataAsString(long row, int col);
        object    GetCellData(long row, int col);
        System.Data.DataRow GetSchemaRow(int col);
    }
}
```

### 4.2 Index conventions — get these wrong and you read the neighbouring column

Read out of `QEResultSet::GetCellData` / `GetCellDataAsString` / `GetSchemaRow` IL:

* In grid mode (`m_gridMode == true`, always true for a results grid) `GetCellData(row, col)`
  and `GetCellDataAsString(row, col)` do `m_view.GetCellData(row, col - 1)`.
  → **pass the GRID column index. Grid column 0 is the row-number gutter; data columns are 1..N.**
* `GetSchemaRow(int i)` is a bare `m_schemaTable.Rows[i]` — **no −1**.
  → **pass `gridCol - 1`.**
* `ColumnNames` is a 0-based `StringCollection` of the data columns.
  → **`ColumnNames[gridCol - 1]`.**
* `TotalNumberOfColumns == NumberOfDataColumns + 1` in grid mode; `NumberOfDataColumns` is the
  count you want.

Red Gate's code carries the same convention (`ResultsGridColumn.ColumnNumber = schemaRowIndex + 1`,
then `GetCellData(row, ColumnNumber)`), which is an independent confirmation.

### 4.3 Which cell was right-clicked

`GridResultsGrid::OnMouseDown` (verified IL):

```
if (e.Button == MouseButtons.Right && !ContainsFocus) Focus();
base.OnMouseDown(e);
```

So a right-click **focuses the grid but does not move the selection or the current cell**.
Two public ways to identify the clicked cell, in order of preference:

1. **Subscribe to `IGridControl.MouseButtonClicking`** (public event). Its
   `MouseButtonClickingEventArgs` carries `RowIndex`, `ColumnIndex`, `CellRect`, `Modifiers`,
   `Button`. Record the last `Button == Right` hit per grid. This is exact and does not depend
   on mouse position at command time.
2. **`grid.HitTest(x, y)`** (public, returns `HitTestInfo { HitTestResult, RowIndex, ColumnIndex }`)
   called from `OleMenuCommand.BeforeQueryStatus`, which VS raises while the menu is being
   built and the pointer is still on the clicked cell. Simpler, slightly more fragile — by the
   time the *invoke* handler runs the pointer has moved to the menu item, so **do not HitTest
   in the invoke handler**.

`grid.SelectedCells` (public `BlockOfCellsCollection`) remains the right source when the action
should apply to a multi-cell selection; that is what Red Gate uses for its bulk commands.

---

## 5. Connection and query text

Both are available from **public** members of the DocView, which is a
`SqlScriptEditorControl`:

```csharp
public class ScriptEditorControl : ShellWindowPaneUserControl   // public
{ public string EditorText { get; set; } … }

public class ScriptAndResultsEditorControl : ScriptEditorControl,
             ISqlScriptWindowWithConnection, ISqlToolsWindowWithConnectionState   // public
{
    public Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo Connection { get; }
    public UIConnectionGroupInfo ConnectionInfoList { get; }
    public bool IsConnected { get; set; }
    public event EventHandler<ConnectionChangeEventArgs> ConnectionChanged;
}
```

`UIConnectionInfo` is the same public type `docs/oe-api.md` §8.5 already documents for Tier B:
`ServerName`, `UserName`, `Password`, `AuthenticationType`, `AdvancedOptions["DATABASE"]`.
**Amendment 13's rule applies unchanged: inherit this, never invent a connection string.**

### 5.1 A supported alternative worth knowing about — SSMS 22 brokered services

`Microsoft.SqlServer.Management.UI.VSIntegration.SqlEditor.BrokeredContracts.dll`
(22.200.0.0, in the IDE root) is a **public, versioned contract assembly** — added for Copilot,
but usable by anyone:

```csharp
// moniker "Microsoft.SqlServer.Management.UI.VSIntegration.SqlEditorService" v1.0
public interface ISqlEditorServiceBrokered {
    ValueTask<SqlEditorConnectionDetails> GetCurrentConnectionAsync(CancellationToken ct);
    ValueTask<IList<SqlEditorConnectionDetails>> GetOpenEditorsAsync(CancellationToken ct);
    ValueTask<bool> ChangeCurrentDatabase(string db, CancellationToken ct);
}
public sealed class SqlEditorConnectionDetails
{ public string ConnectionString { get; set; } public string EditorMoniker { get; set; }
  public string EditorCaption { get; set; } public bool IsActive { get; set; } }

// moniker "Microsoft.SqlServer.Management.UI.VSIntegration.QueryEditorTabDataService" v1.0
public interface IQueryEditorTabDataServiceBrokered {
    ValueTask<List<QueryResultsPaneInfo>> GetAvailablePanesAsync(string moniker, CancellationToken ct);
    ValueTask<GridResultsSegment> GetGridResultsSegmentAsync(
        string moniker, int gridIndex, int startCol, int cols,
        long startRow, int maxRows, int maxCellText, CancellationToken ct);
    ValueTask<TextContentSegment> GetTextResultsSegmentAsync(…);
    ValueTask<TextContentSegment> GetMessagesTabSegmentAsync(…);
    ValueTask<QueryPlanXmlSegment> GetQueryPlanXmlSegmentAsync(…);
    ValueTask<ClientStatisticsResult> GetClientStatisticsAsync(…);
}
public sealed class GridResultsSegment
{ public int GridIndex, TotalGridCount, TotalColumnCount, StartColumn;
  public long TotalRowCount, StartRow;
  public List<string> ColumnNames; public List<List<string>> Rows; }
```

Both descriptors are `ServiceJsonRpcDescriptor`s exposed via
`QueryEditorTabDataServiceDescriptors.QueryEditorTabDataService` and
`SqlEditorBrokeredServiceDescriptors.SqlEditorService` (guids/monikers and versions read from
the `.cctor` IL). Get them from `IBrokeredServiceContainer` /
`ServiceBrokerAggregator`, as with any VS brokered service.

**Limits:** cell values come back as **strings only** (no `object`, no type), and there is no
notion of a right-clicked cell. It gives us `ConnectionString` and column names and values on
a supported contract, which is a genuinely better fallback than reflection if the internal
graph ever changes. It does **not** help with base tables. I did **not** call these services —
their existence and shapes are read from metadata; the RPC behaviour is unverified.

---

## 6. The hard part: mapping a result column to a base table/column

### 6.1 The grid retains nothing usable — proven twice

`QESQLBatch::DoBatchExecution` IL, the actual execute:

```
IL_02CE: call     QESQLBatch::IsConnectionAlwaysEncryptedEnabled
IL_02D3: brtrue.s +4
IL_02D5: ldc.i4.s 16        // CommandBehavior.SequentialAccess   (Always Encrypted path)
IL_02D9: ldc.i4.0           // CommandBehavior.Default            (normal path)
IL_02E4: callvirt System.Data.IDbCommand::ExecuteReader
```

`CommandBehavior.KeyInfo` is `4`. It is **never** passed. `QEResultSet::Initialize` then stores
`reader.GetSchemaTable()` in `m_schemaTable`, which `GetSchemaRow(i)` hands back.

Live proof on this machine (`SsmsDataAnalyzerTest`, `System.Data.SqlClient`):

```
query: SELECT TOP 3 c.Id, c.PlainCol AS Alias1, c.SingleFkCol,
              c.PlainCol + 1 AS ExprCol, GETDATE() AS NowCol
       FROM dbo.FkChild c

=== CommandBehavior.Default  (what SSMS actually uses) ===
Id           base=[].[].[]         BaseColumnName='Id'
Alias1       base=[].[].[]         BaseColumnName='Alias1'      <-- the ALIAS, not PlainCol
SingleFkCol  base=[].[].[]         BaseColumnName='SingleFkCol'
ExprCol      base=[].[].[]         BaseColumnName='ExprCol'
NowCol       base=[].[].[]         BaseColumnName='NowCol'

=== CommandBehavior.KeyInfo  (what SSMS does NOT use) ===
Id           base=[].[dbo].[FkChild]  BaseColumnName='Id'        IsKey=True
Alias1       base=[].[dbo].[FkChild]  BaseColumnName='PlainCol'  IsExpression=False
SingleFkCol  base=[].[dbo].[FkChild]  BaseColumnName='SingleFkCol'
ExprCol      base=[].[].[]            BaseColumnName='ExprCol'   IsExpression=True
NowCol       base=[].[].[]            BaseColumnName='NowCol'    IsExpression=True
```

So under SSMS's real behaviour the schema table gives us **no base table at all**, and
`BaseColumnName` is merely a duplicate of the display name. This is exactly why Red Gate reads
only `BaseColumnName`, `DataTypeName`, `ColumnSize`, `NumericPrecision`, `NumericScale` from it
(`ResultsGridColumn..ctor`, `GetColumnTypeName`) — they use it as *the column name*, falling
back to the literal string `"No column name"`. They never attempt base-table resolution.
**Do not repeat the mistake of reading `BaseTableName` from `GetSchemaRow` and believing it.**

`m_schemaTable` is still worth having for **types**: `DataTypeName`, `ColumnSize`,
`NumericPrecision`, `NumericScale`, `AllowDBNull` are all populated under `Default` and are what
Amendment 14's literal-formatting rule needs.

### 6.2 The route that works: `sys.dm_exec_describe_first_result_set`

```sql
SELECT column_ordinal, name, system_type_name, is_nullable,
       source_server, source_database, source_schema, source_table, source_column,
       error_number, error_message
FROM sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)   -- 1 = include browse information
WHERE is_hidden = 0
ORDER BY column_ordinal;
```

**The third argument must be `1`.** With `0` every `source_*` column comes back NULL — I hit
this first and it looks exactly like "the feature doesn't work". Verified both ways.

It **compiles but does not execute** the batch, so it is safe to run against production and
costs a parse/bind only. It takes only the query text plus the connection's database context.

Live results (all from the local instance, `is_hidden = 0` rows only):

| Query | Result |
|---|---|
| `SELECT c.Id, c.PlainCol AS Alias1, c.SingleFkCol, c.PlainCol+1 AS ExprCol, GETDATE() AS NowCol FROM dbo.FkChild c` | Id → `[dbo].[FkChild].[Id]`; **Alias1 → `[dbo].[FkChild].[PlainCol]`** (alias resolved); SingleFkCol → resolved; **ExprCol → NULL**; **NowCol → NULL** |
| `SELECT * FROM dbo.FkChild ORDER BY Id` | all 9 columns resolved |
| self-join `SELECT a.Id, b.PlainCol FROM FkChild a JOIN FkChild b …` | both resolved; one extra `is_hidden=1` row appended |
| `SELECT Id FROM FkChild UNION ALL SELECT Id FROM FkChild` | **NULL** — correct, there is no single source |
| `SELECT COUNT(*) AS n, PlainCol FROM FkChild GROUP BY PlainCol` | `n` → **NULL**; `PlainCol` → resolved |
| `SELECT TOP 1 c.Id, c.PlainCol FROM SsmsDataAnalyzerTest.dbo.FkChild c` *(run from `master`)* | resolved, **with `source_database = SsmsDataAnalyzerTest`** |
| `SELECT name, type_desc FROM sys.all_objects` | resolves **through the view to the underlying base tables** (`sys.sysschobjs`, `sys.syspalnames`) |
| `DECLARE @t TABLE(x int); SELECT x FROM @t` | NULL |
| `SELECT Id INTO #t FROM FkChild; SELECT Id FROM #t` | **error 11525** — "Metadata discovery only supports temp tables when analyzing a single-statement batch" |
| `SELECT Id FROM #nope` | errors 208 + 11529 returned as rows (not an exception) |
| `EXEC sp_who` | columns described, all sources NULL |
| `SELECT Id+0, PlainCol, PlainCol FROM FkChild` | ordinal 1 `name` is **NULL** (unnamed expression); duplicate names are fine |

Requirements/caveats worth stating: SQL Server 2012+ and Azure SQL (present on every server we
target); the caller needs permission to compile the batch (the user just ran it, so they do);
errors are returned as **rows with `error_number`**, not thrown — check for them.

### 6.3 Ordinal alignment (the thing that could silently jump to the wrong table)

With browse information on, the DM appends extra `is_hidden = 1` rows (the key columns SQL
Server would need for a browsable cursor). Verified: **the `is_hidden = 0` rows keep
`column_ordinal` 1..N in the same order as the visible result columns**, and the hidden ones
come after. So `WHERE is_hidden = 0 ORDER BY column_ordinal` maps 1:1 onto grid data columns
`1..NumberOfDataColumns`.

`sys.all_objects` produced 2 visible rows and **39 hidden** ones — so this filter is not a
nicety.

### 6.4 The honest limits, and the required fallback

`sys.dm_exec_describe_first_result_set` describes the **first result set of the text you give
it**. Two independent problems follow, and neither is solvable from the grid:

1. **Which text ran?** `ScriptEditorControl.EditorText` is the *whole document*. SSMS executes
   the **selection** when there is one (`QESQLExec.Execute(ITextSpan …)`), and splits on `GO`
   into `QESQLBatch`es, each with its own `Text`. `QESQLBatch` is internal and its instances are
   disposed after execution, so recovering the exact batch text per grid is not reliably
   possible. We only ever have a *candidate* text.
2. **Which result set?** One editor tab can hold many grids
   (`GridResultsTabPage.m_gridContainers`). The DM only describes the first.

**Binding rule for the implementation — never ship inference disguised as fact:**

Offer the base-table action **only** when *all* of these hold, and disable it (with a tooltip
saying why) otherwise:

* the clicked grid is **grid index 0** in its tab (`m_gridContainers[0]`, or
  `GridResultsSegment.GridIndex == 0` via the brokered service);
* `TotalGridCount == 1` — more than one grid means we cannot know which one the DM described;
* the DM returned **no `error_number` rows**;
* the count of `is_hidden = 0` rows **equals** `IGridResultSet.NumberOfDataColumns`;
* the DM's `name` for that ordinal **string-equals** `IGridResultSet.ColumnNames[ordinal-1]`
  (ordinal comparison; both NULL/`(No column name)` counts as a match);
* `source_table` for that ordinal is non-NULL.

The last three together are the safety net: if the text we described is not the text that ran,
the shapes will almost always disagree and we decline rather than jump somewhere plausible and
wrong. Amendment 14's principle applies verbatim — *a wrong jump is worse than no jump*.

**Recommended fallback ladder**, in order:

1. All preconditions met → offer **"Analyze `[schema].[table]`"** / **"Go to source for this
   value"**, naming the resolved table in the menu caption so the user can see what we
   concluded before clicking.
2. DM resolved nothing for that column (expression / aggregate / UNION / `EXEC`) → menu item
   present but **disabled**, tooltip: *"This column is a computed expression — it has no base
   table."* This is the common, correct, honest outcome.
3. Preconditions not met (multiple grids, selection executed, temp tables, count mismatch) →
   disabled, tooltip: *"Couldn't determine which query produced this grid."*
4. Always available regardless: **"Analyze table…"** opening our existing tool window with the
   connection inherited and the table picker focused, and **"Copy cell value"**. These need no
   base-table knowledge at all and should be the item the user reaches for when 1–3 fail.

Explicitly rejected: name-based heuristics (matching `CustomerId` → `Customer.Id`). Same ruling
as Amendment 14's closing paragraph. If we ever want it, it is a separate, clearly-labelled,
opt-in feature — never a silent fallback.

One more honest caveat: for a **view**, the DM resolves through to the *underlying base table*,
not the view (`sys.all_objects` → `sys.sysschobjs`). For a profiling tool that is arguably the
right answer, but the user clicked a column of a view and we will name a table they did not
mention. Show the resolved name in the menu caption so this is never a surprise.

---

## 7. Minimal code sketch

Everything below uses **public** types except the two clearly-marked reflection hops.
It is a sketch read off the IL; it has not been compiled or run.

### 7.1 `.vsct` — the menu placement

```xml
<Commands package="guidDataAnalyzerPkg">
  <Groups>
    <Group guid="guidDataAnalyzerCmdSet" id="grpResultsGrid" priority="0x0600">
      <!-- SSMS results-grid context menu: SQLWorkbenchCommands.GUID_SQLEditorGroup
           : IDM_SQLWB_SQLRESGRID_CONTEXT (112 = 0x0070) -->
      <Parent guid="guidSqlEditorGroup" id="IDM_SQLWB_SQLRESGRID_CONTEXT"/>
    </Group>
  </Groups>
  <Buttons>
    <Button guid="guidDataAnalyzerCmdSet" id="cmdAnalyzeSourceTable" priority="0x0100" type="Button">
      <Parent guid="guidDataAnalyzerCmdSet" id="grpResultsGrid"/>
      <CommandFlag>DynamicVisibility</CommandFlag>
      <CommandFlag>DefaultDisabled</CommandFlag>
      <CommandFlag>TextChanges</CommandFlag>   <!-- so we can put the table name in the caption -->
      <Strings><ButtonText>Analyze source table…</ButtonText></Strings>
    </Button>
  </Buttons>
</Commands>

<Symbols>
  <GuidSymbol name="guidSqlEditorGroup" value="{33F13AC3-80BB-4ECB-85BC-225435603A5E}">
    <IDSymbol name="IDM_SQLWB_SQLRESGRID_CONTEXT" value="0x0070"/>
  </GuidSymbol>
  <GuidSymbol name="guidDataAnalyzerCmdSet" value="{…ours…}">
    <IDSymbol name="grpResultsGrid"         value="0x1100"/>
    <IDSymbol name="cmdAnalyzeSourceTable"  value="0x0101"/>
  </GuidSymbol>
</Symbols>
```

Package must already be loaded when the user right-clicks — the same
`[ProvideAutoLoad(ShellInitialized, BackgroundLoad)]` `docs/oe-api.md` §5 established.

### 7.2 Finding the grid and reading the clicked cell

```csharp
using Microsoft.SqlServer.Management.UI.Grid;                        // GridControl, IGridControl…
using Microsoft.SqlServer.Management.QueryExecution;                 // IGridResultSet
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;       // SqlScriptEditorControl

internal sealed class ClickedCell
{
    public GridControl Grid; public long Row; public int GridCol;    // GridCol: 1..N, 0 = gutter
    public string ColumnName; public object Value; public DataRow Schema;
    public SqlScriptEditorControl Editor;
}

// Called from OleMenuCommand.BeforeQueryStatus — the pointer is still on the clicked cell.
private static ClickedCell Capture()
{
    // The right-clicked grid focuses itself: GridResultsGrid.OnMouseDown does
    //   if (e.Button == Right && !ContainsFocus) Focus();
    var focused = Control.FromHandle(NativeMethods.GetFocus()) as GridControl;
    if (focused == null) return null;

    var p   = focused.PointToClient(Control.MousePosition);
    var hit = focused.HitTest(p.X, p.Y);                             // public
    if (hit == null || hit.ColumnIndex < 1 || hit.RowIndex < 0) return null;

    var rs = focused.GridStorage as IGridResultSet;                  // public prop, public iface
    if (rs == null) return null;

    int data = hit.ColumnIndex - 1;                                  // see §4.2
    if (data < 0 || data >= rs.NumberOfDataColumns) return null;

    // walk up the WinForms parent chain to the (public) editor control
    Control c = focused;
    while (c != null && !(c is SqlScriptEditorControl)) c = c.Parent;

    return new ClickedCell {
        Grid = focused, Row = hit.RowIndex, GridCol = hit.ColumnIndex,
        ColumnName = rs.ColumnNames[data],
        Value      = rs.GetCellData(hit.RowIndex, hit.ColumnIndex),  // NOTE: grid index, not data
        Schema     = rs.GetSchemaRow(data),                          // NOTE: data index, not grid
        Editor     = c as SqlScriptEditorControl
    };
}
```

If `Control.FromHandle(GetFocus())` proves unreliable, the alternative — and the one I would
actually build — is to enumerate `GridControl` descendants of the editor control once per
execution and subscribe to `IGridControl.MouseButtonClicking`, caching the last
`Button == MouseButtons.Right` `(grid, RowIndex, ColumnIndex)`. Both APIs are public.

**Prefer `GetCellData` over `GetCellDataAsString`**: the string form is truncated to the user's
"Maximum characters retrieved" setting (`IGridControl2.NumberOfCharsToShow`), and we need the
real typed value for Amendment 14's literal formatting.

### 7.3 Reaching the result set without `GridStorage` (fallback only)

If `grid.GridStorage` ever stops being the `QEResultSet`, Red Gate's chain still works — it is
pure reflection and is the fragile path, quoted here so nobody has to re-derive it:

```csharp
// SsmsResultsGrid.GetResultsPage(docView):
var results  = GetField(docView,  "m_sqlResultsControl");   // DisplaySQLResultsControl
var gridPage = GetField(results,  "m_gridResultsPage");     // GridResultsTabPage
// ResultsWindow.GetResultsGrid / GetSelections:
var grid     = GetProperty(gridPage, "FocusedGrid");        // GridControl
var blocks   = (IList)GetProperty(grid, "SelectedCells");   // BlockOfCellsCollection
var rs       = Invoke(gridPage, "GetGridResultSet", grid);  // QEResultSet
var schema   = GetField(rs, "m_schemaTable");               // DataTable
var cell     = Invoke(rs, "GetCellData", row, gridCol);
```

### 7.4 Connection + base-column resolution

```csharp
// ---- connection: inherit, never invent (Amendment 13) --------------------------------
UIConnectionInfo ci = clicked.Editor.Connection;                 // public property
string server   = ci.ServerName;
string database = ci.AdvancedOptions["DATABASE"];                // NOT ci.DatabaseName
// build our own SqlConnection from this, exactly as docs/oe-api.md §4.1 Option A;
// if AuthenticationType is token-based, degrade to the Tier B picker.

// ---- base table/column: describe the candidate text ----------------------------------
const string Describe = @"
SELECT column_ordinal, name, system_type_name,
       source_database, source_schema, source_table, source_column,
       error_number
FROM sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)      -- 1 IS REQUIRED
WHERE is_hidden = 0
ORDER BY column_ordinal;";

// @tsql = the CANDIDATE text. Whole-document EditorText is the only public source;
// treat everything it yields as unverified until the §6.4 preconditions all pass.
using (var cmd = new SqlCommand(Describe, conn))
{
    cmd.Parameters.Add("@tsql", SqlDbType.NVarChar, -1).Value = candidateSql;
    cmd.CommandTimeout = 15;
    var rows = Read(cmd);                                        // List<DescribedColumn>

    if (rows.Any(r => r.ErrorNumber != null)) return Decline("query could not be described");
    if (rows.Count != rs.NumberOfDataColumns)  return Decline("result shape does not match");

    var d = rows[clicked.GridCol - 1];                            // ordinals align, see §6.3
    if (!NamesMatch(d.Name, clicked.ColumnName)) return Decline("column names do not match");
    if (d.SourceTable == null)
        return Decline($"'{clicked.ColumnName}' is a computed expression — no base table");

    // resolved: d.SourceDatabase (may be a different db), d.SourceSchema, d.SourceTable,
    //           d.SourceColumn, d.SystemTypeName
    var target = new TableRef { Server = server,
                               Database = d.SourceDatabase ?? database,
                               Schema = d.SourceSchema, Name = d.SourceTable };
}
```

`Decline(reason)` = leave the command visible but `Enabled = false` with `reason` as the
tooltip. It must never throw and never guess.

---

## 8. Risks, and what an SSMS update would break

| # | Risk | Likelihood | Blast radius | Mitigation |
|---|---|---|---|---|
| 1 | `IDM_SQLWB_SQLRESGRID_CONTEXT` (0x0070) or `GUID_SQLEditorGroup` changes | low — stable since SSMS 2005-era ids, and Red Gate has forked per shell without changing this | item vanishes silently | our item simply never appears; detectable at test time only. Keep the constants in one file with this document referenced |
| 2 | SSMS moves the results grid off WinForms / off `ShowContextMenu` | low-medium — same VS-18-migration pressure as OE | (a) dies | feature flag + the CommandBars fallback SQL Lizard proves works |
| 3 | `grid.GridStorage` stops being the `QEResultSet` | low — it is how the grid is fed | cell/value read dies | fall back to §7.3 reflection chain, then to the brokered `GetGridResultsSegmentAsync` |
| 4 | `IGridResultSet` / `IGridControl` / `SqlScriptEditorControl` change shape | low — public, and `IGridResultSet` exists precisely as an extension seam | `MissingMethodException` at the guarded call | one try/catch at the capture boundary |
| 5 | Assembly roll `22.200.0.0 → 23.x` | **certain** on SSMS 23 | binding failure | already pinned `InstallationTarget [22.0,)`; `<Private>false</Private>` refs |
| 6 | **Ordinal misalignment → wrong base table** | **certain if unguarded** | *silently jumps to the wrong table* — the worst failure this feature can have | the five §6.4 preconditions are **non-optional** |
| 7 | User executed a selection, or the tab has several grids | **very common** | described text ≠ executed text | preconditions catch it; decline with a clear reason |
| 8 | Temp tables in a multi-statement batch | common in real work | DM returns error 11525 | error rows checked → decline |
| 9 | `sys.dm_exec_describe_first_result_set` blocked by permissions / unsupported target | low | no resolution | it returns error rows, not an exception; decline |
| 10 | Right-click in a `text`/XML/JSON cell, or on the row-number gutter (`ColumnIndex == 0`) | medium | index underflow | guard `ColumnIndex >= 1` (already in the sketch) |
| 11 | Describing a long document on every right-click | medium | menu feels slow | run the describe **on invoke**, not in `BeforeQueryStatus`; in `BeforeQueryStatus` only check the cheap local preconditions. Cache per (document, text hash) |
| 12 | `BeforeQueryStatus` throws | medium during development | unlike the OE case this does **not** kill the whole menu (VS isolates command targets), but our item disappears | wrap it anyway |

Risk 6 is the one to put in the contract. Everything else degrades to "the item is greyed out".

---

## 9. Recommendation to the lead

1. **Build the menu item with `.vsct`.** `{33F13AC3-80BB-4ECB-85BC-225435603A5E}:0x0070`.
   No reflection, no CommandBars, proven by Red Gate on this exact install. Note this
   contradicts Amendment 6 §2's "drop `.vsct`" — that ruling was about the **Object Explorer**
   menu only and remains correct there. The two menus use opposite mechanisms; the codebase
   will end up with both, and that is correct, not an inconsistency.
2. **Read cells through `IGridResultSet`.** Public interface, three members, no reflection.
   Mind the ±1 index convention in §4.2 — that is the detail most likely to cost someone an
   afternoon.
3. **Treat base-table resolution as an enrichment, not a feature.** The DM route works and is
   correct where it works. Ship the §6.4 precondition gate from day one, not as a hardening
   pass — without it the feature is a silent-wrong-answer generator, which is exactly what
   Amendment 14 forbids.
4. **The genuinely valuable, always-available action is "Analyze table…" with the connection
   inherited.** It needs none of §6, it directly answers Amendment 13, and it is the entry
   point a DBA staring at a result set actually wants. Base-table resolution just lets us
   pre-fill the table name when we are certain.
5. **Register the brokered contracts as the strategic fallback.**
   `Microsoft.SqlServer.Management.UI.VSIntegration.SqlEditor.BrokeredContracts.dll` is the
   only *supported* surface in this whole area. If Microsoft keeps investing there (they added
   it for Copilot), it is where this integration should eventually live. Worth a follow-up
   spike to actually call it — I read its metadata but did not exercise it.

---

## Appendix — reproducing these findings

`spikes/OeProbe` (extended in this spike: `il --grep <text> [--headonly]` to find the method
that calls something, and constant values now printed for literal fields in `members`).

```
cd spikes/OeProbe && dotnet build -c Release
set IDE=C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE
set P=bin\Release\net8.0\OeProbe.exe

:: the menu
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --grep ShowContextMenu --headonly
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type DisplaySqlResultsTabControl --method WndProc
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type SQLWorkbenchCommands --method .cctor
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type SQLWorkbenchCommands      :: IDM_* values

:: the grid / result set
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type QueryExecution.IGridResultSet
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type QueryExecution.QEResultSet --all
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type ResultSetAndGridContainer --method Initialize
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type QEResultSet --method GetCellData
%P% members "%IDE%\Microsoft.SqlServer.GridControl.dll"   --type Grid.IGridControl
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type Editors.ScriptAndResultsEditorControl

:: CommandBehavior
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type QESQLBatch --method DoBatchExecution

:: brokered contracts
%P% types   "%IDE%\Microsoft.SqlServer.Management.UI.VSIntegration.SqlEditor.BrokeredContracts.dll" --ns "" --all
%P% il      "%IDE%\...BrokeredContracts.dll" --type Descriptors --method .cctor

:: SQL Lizard
set LZ=%IDE%\Extensions\SqlLizard
%P% types   "%LZ%\SSMSLizardDataGrid.dll" --ns "" --all
%P% il      "%LZ%\SSMSLizardDataGrid.dll" --type "<InitializeAsync>d__13" --method MoveNext
%P% il      "%LZ%\SSMSLizardDataGrid.dll" --type ResultsGridContextMenuInjector --method FindLikelyResultsGridMenu

:: Red Gate
set RG=C:\Program Files (x86)\Red Gate\SQL Prompt 11
%P% types   "%RG%\RedGate.SqlPrompt.CommonUI.dll" --ns "Editor.ResultsGrid" --all
%P% il      "%RG%\RedGate.SqlPrompt.CommonUI.dll" --type Editor.ResultsGrid.ResultsWindow --method GetSelections
%P% il      "%RG%\RedGate.SqlPrompt.CommonUI.dll" --type ResultsGrid.ResultsGridColumn --method .ctor
%P% il      "%RG%\RedGate.SqlPrompt.ShellAbstraction.22.dll" --type MenuCommandsRegistry
```

The `Menus.ctmenu` finding: dump managed resources with `%P% res <asm> --name "" --out <dir>`
and search the extracted `.resources` files for the ASCII marker `CFCT` and the UTF-16 string
`Menus.ctmenu`. SSMS's is in
`Microsoft.SqlServer.Management.UI.VSIntegration.Editors.Resources.resources`; Red Gate's is in
`_EmptyResource.resources`. Both blobs are CFCT **version 5**, which is compressed — I could
not read the group placements out of either.

SQL-side checks (live instance, read-only):

```sql
-- the load-bearing third argument
SELECT column_ordinal, name, source_schema, source_table, source_column, is_hidden,
       error_number, error_message
FROM sys.dm_exec_describe_first_result_set(N'<your query>', NULL, 1);

-- compare against 0 and watch every source_* go NULL
SELECT source_schema, source_table, source_column
FROM sys.dm_exec_describe_first_result_set(N'<your query>', NULL, 0);
```

and the `CommandBehavior` comparison (PowerShell, `System.Data.SqlClient`):

```powershell
$r = $cmd.ExecuteReader([System.Data.CommandBehavior]::Default)   # what SSMS uses
$r.GetSchemaTable() | ForEach-Object { $_["BaseTableName"], $_["BaseColumnName"] }
$r = $cmd.ExecuteReader([System.Data.CommandBehavior]::KeyInfo)   # what SSMS does NOT use
```
