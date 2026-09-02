# The newer (IGridResultSet) grid API — what it gave us, and how to bring it back

**Status: not currently used.** As of v0.8.0, the results-grid features (Find in Results, Go
to source for this value) are built entirely on `Microsoft.SqlServer.GridControl.dll`'s
`IGridStorage`/`IGridControl` — a portable API, confirmed byte-for-byte identical between SSMS
21 and every SSMS 22 build, including the 22.3 build that first surfaced this whole issue.

This file exists because the user asked to keep the previous, `IGridResultSet`-based
implementation "in comments" — the lead's call was to honor that intent without the letter:
scattered commented-out code rots silently, is never compiled, and nobody trusts it after a
few months. Instead, the full previous implementation of every changed method is preserved
here, verbatim, in fenced code blocks that are easy to diff back in.

**The code is also recoverable a second, independent way**: it is the working tree as of
commit `f986371` ("Degrade gracefully on SSMS 22 builds without the grid API") in this
repository's git history — e.g. `git show f986371:src/SsmsDataAnalyzer.Vsix/ResultsGrid/GridClickCapture.cs`,
or `git diff f986371 HEAD -- src/SsmsDataAnalyzer.Vsix/` to see every file this swap touched at once.

## Why anyone would want to switch back

The two APIs are not equivalent. `IGridResultSet.GetCellData` (SQLEditors.dll) hands back the
cell's real, PROVIDER-TYPED value (`SqlInt32`, `SqlDateTime`, `byte[]`, ...); the portable
`IGridStorage.GetCellDataAsString` (GridControl.dll) hands back only the grid's DISPLAY TEXT —
whatever string SSMS chose to render, after its own rounding/hex-formatting/truncation rules.

Concretely, on the newer API:

- **No display-text round-trip is needed at all.** The typed value goes straight into a SQL
  literal (`SqlLiteralFormatter.TryFormat`) — there is no parsing, and therefore nothing to
  get wrong by parsing.
- **No declines for float/real, varbinary/binary, or long text/xml.** These are exactly the
  categories `TryFormatDisplayText` (the v0.8.0 replacement) has to refuse on the portable
  API, because the DISPLAY text for those types is lossy (rounded floats), re-encoded (hex for
  binary), or possibly truncated (MAX/LOB text) — the typed value has none of these problems.
- **No NULL-vs-the-string-"NULL" ambiguity.** `IGridResultSet.GetCellData` returns a real null
  (or a `SqlTypes` struct whose own `.IsNull` is true) for a database NULL — never the literal
  4-character string "NULL", so there is nothing to confuse it with a text column that
  actually stores the word "NULL".
- Everything else (the five gates, the batch-agreement logic, GO-batch splitting, the
  status-bar/ActivityLog messaging) is IDENTICAL either way — none of that changed in the
  v0.8.0 swap, and switching back does not touch it.

## Which SSMS builds this needs

`IGridResultSet` is defined in `SQLEditors.dll`. Confirmed present (public) in SSMS
**22.9.12105.275** (this project's dev machine). Confirmed **absent** from SSMS
**22.3.2+25.11520.95** (the user's build that started this investigation) — same assembly
*identity* (`SqlEditors, Version=22.200.0.0, ...`), different contents. The exact version where
the type was introduced is not known; treat anything before 22.9 as unconfirmed until tested.

## Exactly what to swap, file by file

### 1. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/GridClickCapture.cs`

Replace the whole file with the version below (or `git show f986371:...` it back). This is
the biggest behavioural difference: `ClickedGridCell.Value` becomes the real typed value
again (not display text), `ClickedGridCell.SchemaRow` comes back (a `DataRow` from
`IGridResultSet.GetSchemaRow`, unused by anything currently but was part of the old capture),
and the index convention for `ColumnNames`/`GetSchemaRow`/`NumberOfDataColumns` reverts to the
DATA-index convention (`grid index - 1`) — genuinely different from the portable API's
`GetHeaderInfo`, which (re-verified live for v0.8.0) shares HitTest's own gutter-inclusive
index space directly. **Do not assume the two conventions match** — that mistake is exactly
what this whole project's "index trap" running theme is about.

```csharp
using System;
using System.Collections.Specialized;
using System.Data;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.QueryExecution;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>Everything needed to act on a right-clicked results-grid cell, captured once
    /// in BeforeQueryStatus (docs/resultsgrid-api.md section 4.3 — by Invoke time the mouse
    /// has moved to the menu item, so HitTest must not be repeated there).</summary>
    internal sealed class ClickedGridCell
    {
        public GridControl Grid;
        public long Row;
        /// <summary>GRID column index (1..N; 0 is the row-number gutter) — the convention
        /// <see cref="IGridResultSet.GetCellData"/> and the DM's column_ordinal both use.</summary>
        public int GridCol;
        public string ColumnName;
        public object Value;
        public DataRow SchemaRow;
        public int NumberOfDataColumns;
        /// <summary>Every data column's name, in grid order (index 0 = grid column 1) —
        /// needed to full-shape-match a described batch against the WHOLE grid, not just the
        /// clicked column (v0.7.4, see ResultsGridGoToSourceResolver).</summary>
        public string[] AllColumnNames;
        public SqlScriptEditorControl Editor;
    }

    /// <summary>
    /// CONTRACT.md Amendment 16, docs/resultsgrid-api.md sections 4 and 6.4. Every member used
    /// here is public (no reflection into internal SSMS types) — see the doc's confirmation
    /// that GridControl/IGridControl/IGridResultSet/SqlScriptEditorControl are all public.
    ///
    /// v0.7.4 amendment (supersedes Amendment 16's original gates 1+2): this class no longer
    /// checks how many result grids share the clicked grid's tab. A user's ordinary "two
    /// near-identical SELECTs, differing only in a WHERE value, run together" script has two
    /// grids in one tab and was being refused entirely — the original "tab holds exactly one
    /// grid" rule was a proxy for "we can't tell which query produced this," but HitTest
    /// already answers that precisely regardless of how many sibling grids exist. The real
    /// safety property (never resolve to the wrong table) is now enforced downstream, in
    /// ResultsGridGoToSourceResolver, by describing every GO-separated batch and requiring
    /// every shape-matching candidate to agree on the clicked column's actual source — a
    /// direct test of what makes an answer ambiguous, not a same-tab-count proxy for it.
    /// </summary>
    internal static class GridClickCapture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>Just "which GridControl (if any) currently has keyboard focus" — no cell,
        /// no gates. Used by Find (which does not need TryCapture's Go-to-source-specific
        /// safety gates — it never resolves a base table, only searches what's on screen).</summary>
        public static GridControl TryGetFocusedGrid()
        {
            try
            {
                return Control.FromHandle(GetFocus()) as GridControl;
            }
            catch
            {
                return null;
            }
        }

        public static bool TryCapture(out ClickedGridCell cell, out string declineReason)
        {
            cell = null;
            declineReason = null;
            try
            {
                var focused = Control.FromHandle(GetFocus()) as GridControl;
                if (focused == null)
                {
                    declineReason = "no results grid is focused";
                    return false;
                }

                // Index convention (docs/resultsgrid-api.md section 4.2 — the "index trap"):
                // HitTest/GetCellData take the GRID column index; 0 is the row-number gutter.
                var p = focused.PointToClient(Control.MousePosition);
                var hit = focused.HitTest(p.X, p.Y);
                if (hit == null || hit.ColumnIndex < 1 || hit.RowIndex < 0)
                {
                    declineReason = "not over a data cell";
                    return false;
                }

                var rs = focused.GridStorage as IGridResultSet;
                if (rs == null)
                {
                    declineReason = "grid has no readable result set";
                    return false;
                }

                // GetSchemaRow/ColumnNames take the DATA index (grid index - 1); GetCellData
                // takes the GRID index, unmodified. Do not mix these up (docs section 4.2).
                int dataIndex = hit.ColumnIndex - 1;
                if (dataIndex < 0 || dataIndex >= rs.NumberOfDataColumns)
                {
                    declineReason = "column index out of range";
                    return false;
                }

                // v0.7.4 amendment (lead's ruling, supersedes Amendment 16's original gates
                // 1+2): NOT checked anymore whether this grid is alone in its tab. HitTest
                // above already identifies the CLICKED grid precisely and unambiguously —
                // "which grid is this" was never actually in doubt, multiple grids or not.
                // The real question ("what is the source table/column for the clicked
                // column") is answered downstream by describing every candidate batch and
                // requiring them to AGREE on that answer (ResultsGridGoToSourceResolver) —
                // a direct test of the thing that actually makes an answer ambiguous, rather
                // than a same-tab-grid-count proxy that also blocked the completely ordinary
                // case of two near-identical SELECTs (e.g. differing only in a WHERE value)
                // in one script.
                Control editorCursor = focused;
                SqlScriptEditorControl editor = null;
                while (editorCursor != null)
                {
                    if (editorCursor is SqlScriptEditorControl sse) { editor = sse; break; }
                    editorCursor = editorCursor.Parent;
                }
                if (editor == null)
                {
                    declineReason = "couldn't locate the query editor";
                    return false;
                }

                cell = new ClickedGridCell
                {
                    Grid = focused,
                    Row = hit.RowIndex,
                    GridCol = hit.ColumnIndex,
                    ColumnName = rs.ColumnNames[dataIndex],
                    AllColumnNames = ToArray(rs.ColumnNames),
                    Value = rs.GetCellData(hit.RowIndex, hit.ColumnIndex), // NOTE: grid index
                    SchemaRow = rs.GetSchemaRow(dataIndex),                // NOTE: data index
                    NumberOfDataColumns = rs.NumberOfDataColumns,
                    Editor = editor
                };
                return true;
            }
            catch (Exception ex)
            {
                // Never let a shape surprise in SSMS's UI graph take down the whole context
                // menu (docs/resultsgrid-api.md risk #12) — decline instead.
                declineReason = "internal error: " + ex.Message;
                return false;
            }
        }

        /// <summary><see cref="IGridResultSet.ColumnNames"/> is a <see cref="StringCollection"/>
        /// (decompilation-verified — not a string[]), so a defensive copy needs an explicit
        /// loop rather than a cast off ICloneable.Clone() (which returns another
        /// StringCollection, not a string[]).</summary>
        private static string[] ToArray(StringCollection names)
        {
            var array = new string[names.Count];
            names.CopyTo(array, 0);
            return array;
        }
    }
}
```

### 2. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/GridFindState.cs`

Replace the whole file. `_resultSet` goes back to `IGridResultSet` (from the portable
`IGridStorage`), and `NumberOfDataColumns`/`TotalNumberOfRows` come from the interface's own
properties again instead of `Grid.ColumnsNumber - 1` / `IGridStorage.NumRows()`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.QueryExecution;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>One matched cell — GRID column index (same convention as IGridResultSet.GetCellData).</summary>
    internal struct GridFindMatch
    {
        public long Row;
        public int GridCol;
    }

    /// <summary>
    /// User request: "Find... on right click in result grid of SSMS, we have it already in
    /// tool and it works great." Same UX as the tool window's find (case-insensitive
    /// substring, all columns, N-of-M, wrap-around) — but the DATA and PAINT model are both
    /// completely different here, so this is a fresh implementation, not a port.
    ///
    /// Search corpus: docs/resultsgrid-api.md + this project's own spike established that
    /// SSMS's grid storage (QEDiskStorageView / QEStorageViewOnReader) spools the FULL result
    /// set for random access, not just what has been scrolled into view — so unlike a naive
    /// "search the rendered rows" approach, this genuinely searches every row up to
    /// TotalNumberOfRows. It reads via GetCellDataAsString (the same truncated-for-display
    /// text the user is actually looking at on screen — this is a find-what-I-see feature,
    /// not a SQL-literal-generation one, so the display formatter's truncation is an
    /// acceptable, honestly-scoped limitation here, unlike the "Go to source" value jump
    /// where the same truncation would be a correctness bug).
    ///
    /// Chunked, not fire-and-forget: IGridResultSet has no async surface and this project has
    /// no live-host evidence that IGridStorage is safe to read from a background thread while
    /// the UI paints concurrently. So the scan runs ON the UI thread, yielding periodically
    /// via Task.Yield() so a million-row scan does not freeze SSMS — slower than a background
    /// thread would be, but provably safe without a live host to test against.
    /// </summary>
    internal sealed class GridFindState
    {
        /// <summary>Lead's refinement: cap the tracked match set so a wildcard-ish search
        /// against a huge result set cannot grow without bound. When hit, the counter must
        /// say so honestly rather than silently presenting a partial result as complete.</summary>
        public const int MaxMatches = 10_000;

        public readonly GridControl Grid;
        private readonly IGridResultSet _resultSet;

        public List<GridFindMatch> Matches { get; } = new List<GridFindMatch>();
        public int CurrentIndex { get; private set; } = -1;
        public bool Capped { get; private set; }
        public bool IsSearching { get; private set; }

        /// <summary>
        /// Whether CustomizeCellGDIObjectsEventArgs.ColumnIndex actually uses the same GRID
        /// column-index convention as GetCellData — the "highest-risk unknown" the lead
        /// flagged. Null until the first self-check runs (on the first repaint of a known
        /// match cell); the paint handler must not trust ColumnIndex for anything until this
        /// is true, and must permanently stop trying if it comes back false.
        /// </summary>
        public bool? ColumnIndexConventionVerified { get; set; }

        public GridFindState(GridControl grid, IGridResultSet resultSet)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _resultSet = resultSet ?? throw new ArgumentNullException(nameof(resultSet));
        }

        /// <summary>True if the grid's OWN result set has been replaced since this state was
        /// built (a re-run reusing the same GridControl instance) — the lead's "multiple
        /// grids/tabs" note: rather than silently searching stale data, callers must check
        /// this and treat it the same as the grid being gone.</summary>
        public bool IsStale => !ReferenceEquals(Grid.GridStorage, _resultSet);

        public async Task SearchAsync(string searchText, Action<int> onProgressRow, CancellationToken cancellationToken)
        {
            Matches.Clear();
            CurrentIndex = -1;
            Capped = false;
            ColumnIndexConventionVerified = null;

            if (string.IsNullOrEmpty(searchText)) return;

            IsSearching = true;
            try
            {
                long totalRows = _resultSet.TotalNumberOfRows;
                int dataCols = _resultSet.NumberOfDataColumns;

                for (long row = 0; row < totalRows; row++)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (IsStale) return; // grid was re-populated mid-search — stop, do not report matches against the old data

                    for (int dataCol = 0; dataCol < dataCols; dataCol++)
                    {
                        int gridCol = dataCol + 1; // GetCellData/GetCellDataAsString convention: grid index, 0 = gutter
                        string text;
                        try
                        {
                            text = _resultSet.GetCellDataAsString(row, gridCol);
                        }
                        catch
                        {
                            continue; // an unreadable cell should not abort the whole search
                        }

                        if (text != null && text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Matches.Add(new GridFindMatch { Row = row, GridCol = gridCol });
                            if (Matches.Count >= MaxMatches)
                            {
                                Capped = true;
                                if (Matches.Count > 0) CurrentIndex = 0;
                                return;
                            }
                        }
                    }

                    if (row % 25 == 0)
                    {
                        onProgressRow?.Invoke((int)row);
                        await Task.Yield();
                    }
                }

                if (Matches.Count > 0) CurrentIndex = 0;
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>
        /// v0.7.1: typing must NOT trigger a search (a single keystroke used to launch a
        /// full chunked scan of the entire result set, making the box unusable — see
        /// SearchAsync's own doc comment). Called on every text change instead, to drop the
        /// now-stale match set and let the UI show an explicit "not searched yet" state
        /// rather than leaving old highlights/counts that no longer describe the current box
        /// contents.
        /// </summary>
        public void Clear()
        {
            Matches.Clear();
            CurrentIndex = -1;
            Capped = false;
            ColumnIndexConventionVerified = null;
        }

        public bool TryGetCurrent(out GridFindMatch match)
        {
            if (CurrentIndex < 0 || CurrentIndex >= Matches.Count) { match = default; return false; }
            match = Matches[CurrentIndex];
            return true;
        }

        public void MoveNext()
        {
            if (Matches.Count == 0) return;
            CurrentIndex = (CurrentIndex + 1) % Matches.Count; // wrap-around
        }

        public void MovePrevious()
        {
            if (Matches.Count == 0) return;
            CurrentIndex = (CurrentIndex - 1 + Matches.Count) % Matches.Count; // wrap-around
        }

        /// <summary>Cheap, non-allocating membership check for the paint handler — called once
        /// per visible cell per repaint, so this must stay fast (a List scan is fine at
        /// MaxMatches scale for a grid that only paints ~40 visible rows at a time).</summary>
        public bool IsMatch(long row, int gridCol)
        {
            for (int i = 0; i < Matches.Count; i++)
            {
                var m = Matches[i];
                if (m.Row == row && m.GridCol == gridCol) return true;
            }
            return false;
        }

        public bool IsCurrent(long row, int gridCol)
        {
            if (CurrentIndex < 0 || CurrentIndex >= Matches.Count) return false;
            var m = Matches[CurrentIndex];
            return m.Row == row && m.GridCol == gridCol;
        }
    }
}
```

### 3. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/GridFindView.xaml.cs`

One line, inside `Grid_CustomizeCellGDIObjects`'s self-check (search for
`ColumnIndexConventionVerified == null`):

```csharp
// v0.8.0 (portable API):
var text = state.Grid.GridStorage is Microsoft.SqlServer.Management.UI.Grid.IGridStorage rs
    ? rs.GetCellDataAsString(e.RowIndex, e.ColumnIndex)
    : null;

// Newer API (restore this):
var text = state.Grid.GridStorage is Microsoft.SqlServer.Management.QueryExecution.IGridResultSet rs
    ? rs.GetCellDataAsString(e.RowIndex, e.ColumnIndex)
    : null;
```

### 4. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/ResultsGridFindCommand.cs`

One block, inside `ExecuteCore()`:

```csharp
// v0.8.0 (portable API):
var resultSet = grid.GridStorage; // IGridStorage
if (resultSet == null)
{
    OeDiagnostics.Warn("Results-grid 'Find': the focused grid has no readable GridStorage -- nothing to search.");
    return;
}

// Newer API (restore this):
var resultSet = grid.GridStorage as Microsoft.SqlServer.Management.QueryExecution.IGridResultSet;
if (resultSet == null)
{
    OeDiagnostics.Warn("Results-grid 'Find': the focused grid has no readable IGridResultSet -- nothing to search.");
    return;
}
```

### 5. `src/SsmsDataAnalyzer.Vsix/GoToSource/SqlLiteralFormatter.cs`

**Not a swap — `TryFormat` (the typed-value formatter, below) was never removed.** It is
still live code, still used by the tool window's own "Go to source" (`ProfileViewModel`,
which reads `ColumnProfile.MinValue`/`MaxValue` — real typed values from Core, nothing to do
with the grid at all). The v0.8.0 change only ADDED a second method, `TryFormatDisplayText`,
for the results-grid path specifically. To restore the newer API,
`ResultsGridGoToSourceResolver.ResolveAsync` (section 6 below) goes back to calling
`TryFormat` instead of `TryFormatDisplayText` — `TryFormat` itself needs no changes at all.
It is included here for completeness/reference only.

```csharp
using System;
using System.Data.SqlTypes;
using System.Globalization;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Vsix.GoToSource
{
    /// <summary>
    /// CONTRACT.md Amendment 14/15/16: builds the SQL literal for a "Go to source for this
    /// value" WHERE clause from a column's real value (tool window: MinValue/MaxValue; results
    /// grid: the clicked cell).
    ///
    /// Two rules, both binding, from Agent A's review:
    /// - ColumnProfile.MinValue/MaxValue are object holding the RAW provider type (int,
    ///   DateTime, Guid, byte[], ...), not strings — this switches on the runtime type, it
    ///   never parses text.
    /// - Core's ProfileFormat.Value is display-only and LOSSY (truncates strings at 60 chars,
    ///   hex-clips byte[] at 16 bytes). It must never be used to build a SQL literal — that
    ///   would silently produce a wrong (truncated) filter. This class reads only the raw
    ///   value, never anything already formatted for display.
    ///
    /// If a type cannot be safely rendered as a literal, TryFormat returns false —
    /// CONTRACT.md is explicit that withholding the value jump beats guessing.
    ///
    /// v0.5.2 field report: the results-grid path (IGridResultSet.GetCellData, IL-verified
    /// against QEStorageViewOnReader) surfaces PROVIDER-SPECIFIC System.Data.SqlTypes structs
    /// for ordinary cells — SqlInt32, not System.Int32 — which this switch had no case for,
    /// so it correctly (if unhelpfully) refused every one of them. Every
    /// System.Data.SqlTypes struct implements INullable; the fix lives here, once, rather
    /// than at every call site: unwrap to the underlying CLR value and re-dispatch through
    /// the same switch below.
    /// </summary>
    internal static class SqlLiteralFormatter
    {
        /// <summary>
        /// True if value represents "no value" — plain null, DBNull, or a
        /// System.Data.SqlTypes struct whose IsNull is true (a null SqlInt32 is a real,
        /// non-null .NET object — value == null and value is DBNull both miss it, which is
        /// exactly how "is NULL" got misreported as "unsupported type" before this fix).
        /// Callers use this for their own NULL-vs-unsupported-type message, so the
        /// null-detection logic lives in this one place rather than being duplicated (and
        /// re-drifted) at every call site.
        /// </summary>
        public static bool IsEffectivelyNull(object value)
        {
            if (value == null || value is DBNull) return true;
            if (value is INullable nullable) return nullable.IsNull;
            return false;
        }

        /// <summary>
        /// Formats value (a column's real value) as a T-SQL literal suitable for a WHERE
        /// clause. sourceColumnType is the PROFILED/SOURCE column's ColumnMeta (not the
        /// referenced column's) — the value was read from it, and per the FK constraint the
        /// referenced column must accept the same literal shape, so its type name (e.g.
        /// distinguishing nvarchar from varchar for the N-prefix) is what decides formatting.
        /// </summary>
        public static bool TryFormat(object value, ColumnMeta sourceColumnType, out string literal)
        {
            literal = null;

            if (IsEffectivelyNull(value)) return false;

            if (value is INullable)
            {
                switch (value)
                {
                    // Reference types backed by streams — decline explicitly rather than
                    // unwrap, consistent with how byte[]/xml are already treated below.
                    case SqlXml _:
                    case SqlBytes _:
                    case SqlChars _:
                        return false;

                    // SqlDecimal holds up to 38 digits; C# decimal holds 28–29 — .Value THROWS
                    // OverflowException outside decimal's range (verified empirically). Its
                    // ToString() is confirmed culture-invariant (period decimal separator
                    // regardless of thread culture, e.g. de-DE) and covers the full 38-digit
                    // range, so use it directly instead of unwrapping.
                    case SqlDecimal sqlDecimal:
                        literal = sqlDecimal.ToString();
                        return true;

                    // SqlMoney's range fits comfortably inside decimal (.Value never overflows
                    // here), but SqlMoney.ToString() is CULTURE-DEPENDENT — verified to render
                    // "1234,56" under de-DE, which is not valid T-SQL. Unwrap to decimal and
                    // format invariant ourselves; never call SqlMoney.ToString().
                    case SqlMoney sqlMoney:
                        literal = sqlMoney.Value.ToString(CultureInfo.InvariantCulture);
                        return true;

                    // SqlDateTime.ToString() is likewise culture-dependent (verified:
                    // "07.03.2026 13:45:09" under de-DE — not valid T-SQL). Unwrap to DateTime
                    // and let the existing DateTime case below apply OUR OWN ISO-8601
                    // formatting; SqlDateTime's narrower range/precision is not a formatting
                    // concern once unwrapped (DateTime covers it fully).
                    case SqlDateTime sqlDateTime:
                        return TryFormat(sqlDateTime.Value, sourceColumnType, out literal);

                    case SqlBoolean sqlBoolean: return TryFormat(sqlBoolean.Value, sourceColumnType, out literal);
                    case SqlByte sqlByte: return TryFormat(sqlByte.Value, sourceColumnType, out literal);
                    case SqlInt16 sqlInt16: return TryFormat(sqlInt16.Value, sourceColumnType, out literal);
                    case SqlInt32 sqlInt32: return TryFormat(sqlInt32.Value, sourceColumnType, out literal);
                    case SqlInt64 sqlInt64: return TryFormat(sqlInt64.Value, sourceColumnType, out literal);
                    case SqlSingle sqlSingle: return TryFormat(sqlSingle.Value, sourceColumnType, out literal);
                    case SqlDouble sqlDouble: return TryFormat(sqlDouble.Value, sourceColumnType, out literal);
                    case SqlString sqlString: return TryFormat(sqlString.Value, sourceColumnType, out literal);
                    case SqlGuid sqlGuid: return TryFormat(sqlGuid.Value, sourceColumnType, out literal);
                    case SqlBinary sqlBinary: return TryFormat(sqlBinary.Value, sourceColumnType, out literal);

                    default:
                        // Unrecognized INullable (a future SqlTypes addition, or a 3rd-party
                        // provider type) — withhold rather than guess at an unwrap shape.
                        return false;
                }
            }

            switch (value)
            {
                case string s:
                    literal = (IsUnicodeStringType(sourceColumnType) ? "N'" : "'") + s.Replace("'", "''") + "'";
                    return true;

                case bool b:
                    literal = b ? "1" : "0";
                    return true;

                case byte[] bytes:
                    // Full, lossless hex literal — never the display formatter's 16-byte-clipped preview.
                    literal = "0x" + BitConverter.ToString(bytes).Replace("-", "");
                    return true;

                case Guid g:
                    // uniqueidentifier: quoted string form.
                    literal = "'" + g.ToString() + "'";
                    return true;

                case DateTimeOffset dto:
                    // Unambiguous ISO-8601 with offset, regardless of session DATEFORMAT/LANGUAGE.
                    literal = "'" + dto.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + "'";
                    return true;

                case DateTime dt:
                    literal = "'" + dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";
                    return true;

                case TimeSpan ts:
                    // SQL Server 'time' maps to TimeSpan in ADO.NET.
                    literal = "'" + ts.ToString("hh\\:mm\\:ss\\.fffffff", CultureInfo.InvariantCulture) + "'";
                    return true;

                case byte n8: literal = n8.ToString(CultureInfo.InvariantCulture); return true;
                case sbyte n8s: literal = n8s.ToString(CultureInfo.InvariantCulture); return true;
                case short n16: literal = n16.ToString(CultureInfo.InvariantCulture); return true;
                case ushort n16u: literal = n16u.ToString(CultureInfo.InvariantCulture); return true;
                case int n32: literal = n32.ToString(CultureInfo.InvariantCulture); return true;
                case uint n32u: literal = n32u.ToString(CultureInfo.InvariantCulture); return true;
                case long n64: literal = n64.ToString(CultureInfo.InvariantCulture); return true;
                case ulong n64u: literal = n64u.ToString(CultureInfo.InvariantCulture); return true;
                case decimal dec: literal = dec.ToString(CultureInfo.InvariantCulture); return true;
                case float f: literal = f.ToString("R", CultureInfo.InvariantCulture); return true;
                case double d: literal = d.ToString("R", CultureInfo.InvariantCulture); return true;

                default:
                    // Unknown / exotic provider type (e.g. a CLR UDT, SqlGeography, ...):
                    // withhold rather than guess at a literal shape that might not round-trip.
                    return false;
            }
        }

        /// <summary>nvarchar/nchar/ntext/sysname are Unicode and need the N-prefix; char/varchar/text do not.</summary>
        private static bool IsUnicodeStringType(ColumnMeta meta)
        {
            var typeName = meta?.TypeName;
            if (string.IsNullOrEmpty(typeName)) return true; // unknown -> safer to over-prefix than mangle Unicode data

            return typeName.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "sysname", StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

### 6. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/ResultsGridGoToSourceResolver.cs`

One block, near the end of `ResolveAsync` (search for `columnMeta.ReferencedColumn == null`
and look just below it):

```csharp
// v0.8.0 (portable API) -- request.CellValue is display text (string), described.SystemTypeName/
// MaxLength come from the DM's own describe row for the clicked column:
string cellDisplayText = request.CellValue as string;
if (!SqlLiteralFormatter.TryFormatDisplayText(cellDisplayText, described.SystemTypeName, described.MaxLength, out var literal, out var declineReason))
    return Decline($"Go to source: [{request.GridColumnName}] {declineReason}.");

// Newer API (restore this) -- request.CellValue is the real typed value again:
if (SqlLiteralFormatter.IsEffectivelyNull(request.CellValue))
    return Decline($"Go to source: [{request.GridColumnName}] is NULL — there's no value to filter by.");

if (!SqlLiteralFormatter.TryFormat(request.CellValue, columnMeta, out var literal))
    return Decline($"Go to source: [{request.GridColumnName}] has type {request.CellValue.GetType().Name} which can't be rendered as a SQL literal.");
```

`DescribeFirstResultSetService.cs`'s `system_type_name`/`max_length` columns (added in v0.8.0
for `TryFormatDisplayText`) are harmless to leave in place even after reverting this block —
nothing else needs to change there.

### 7. `src/SsmsDataAnalyzer.Vsix/ResultsGrid/ResultsGridCapability.cs`

Not required to change at all — it is a general safety net (shell/core split + friendly
decline message) for whatever future surprise the next unverified SSMS build turns up, not
something specific to `IGridResultSet`. If you do want it to specifically probe for
`IGridResultSet` again, add
`("Microsoft.SqlServer.Management.QueryExecution.IGridResultSet", "SqlEditors")` back to
`RequiredTypes`.

## What was declined on the portable API, in plain terms

For the record — these are the conversions `TryFormatDisplayText` refuses on the portable API
that `TryFormat` (typed values) never had to:

- **float / real** columns — SQL Server's grid display rounds these; the exact stored value
  can't be recovered from the rounded text.
- **binary / varbinary / image / timestamp / rowversion** columns — shown as a hex string,
  with no way to confirm the display wasn't truncated.
- **text / ntext / xml**, and any `(n)varchar`/`(n)char` declared `MAX` — unbounded types are
  exactly where SSMS's own "Maximum Characters Retrieved" display option can silently clip
  what's on screen.
- A cell whose display text is exactly **"NULL"** — indistinguishable from a real NULL and
  from the literal 4-character string "NULL" stored in a text column.

Every other type (`int`/`bigint`/`smallint`/`tinyint`, `bit`, `decimal`/`numeric`/`money`/
`smallmoney`, `date`/`datetime`/`datetime2`/`smalldatetime`/`datetimeoffset`/`time`,
`uniqueidentifier`, and bounded `char`/`varchar`/`nchar`/`nvarchar`) round-trips through
display text safely and is not declined for this reason.
