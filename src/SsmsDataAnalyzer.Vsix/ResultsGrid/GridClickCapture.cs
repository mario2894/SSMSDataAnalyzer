using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>Everything needed to act on a right-clicked results-grid cell, captured once
    /// in BeforeQueryStatus (docs/resultsgrid-api.md section 4.3 — by Invoke time the mouse
    /// has moved to the menu item, so HitTest must not be repeated there).
    ///
    /// v0.8.0 ("build against the older API" decision): <see cref="Value"/> is now the
    /// grid's DISPLAY TEXT (string, from IGridStorage.GetCellDataAsString), not a typed
    /// value — see docs/newer-grid-api.md for what the previous IGridResultSet-based
    /// implementation gave us and how to restore it on a newer SSMS 22 build.</summary>
    internal sealed class ClickedGridCell
    {
        public GridControl Grid;
        public long Row;
        /// <summary>GRID column index (1..N; 0 is the row-number gutter) — the convention
        /// <see cref="IGridStorage.GetCellDataAsString"/>, <see cref="IGridControl.GetHeaderInfo"/>
        /// and the DM's column_ordinal all use (re-verified live for the portable API — see
        /// GridClickCapture's class doc comment).</summary>
        public int GridCol;
        public string ColumnName;
        /// <summary>Display text (was a typed value pre-v0.8.0 — see docs/newer-grid-api.md).</summary>
        public object Value;
        public int NumberOfDataColumns;
        /// <summary>Every data column's name, in grid order (index 0 = grid column 1) —
        /// needed to full-shape-match a described batch against the WHOLE grid, not just the
        /// clicked column (v0.7.4, see ResultsGridGoToSourceResolver).</summary>
        public string[] AllColumnNames;
        public SqlScriptEditorControl Editor;
    }

    /// <summary>
    /// CONTRACT.md Amendment 16, docs/resultsgrid-api.md sections 4 and 6.4. Every member used
    /// here is public (no reflection into internal SSMS types).
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
    ///
    /// v0.8.0 ("build against the older API" decision — user's call, lead's investigation:
    /// Microsoft.SqlServer.GridControl.dll is byte-for-byte identical between SSMS 21 and
    /// every SSMS 22 build; only SQLEditors.dll's IGridResultSet is version-specific and
    /// missing from the user's 22.3): this class now reads cells and column metadata through
    /// IGridStorage/IGridControl only. The previous IGridResultSet-based implementation
    /// (typed values, no display-text round-trip) is preserved verbatim, with restore
    /// instructions, in docs/newer-grid-api.md — also recoverable from git history at the
    /// v0.7.6 commit that removed it.
    ///
    /// INDEX CONVENTION, re-verified live rather than assumed (the previous convention does
    /// NOT carry over automatically — "do not assume they match the old ones" was the
    /// explicit brief, since this exact class of assumption has bitten this codebase twice
    /// already): a real GridControl was built, populated and hit-tested at known pixel
    /// coordinates in a throwaway WinForms harness (PortableGridHarness, scratchpad-only).
    /// HitTest.ColumnIndex, IGridStorage.GetCellDataAsString's column parameter, and
    /// IGridControl.GetHeaderInfo's index parameter are confirmed to share ONE index space
    /// (IL-traced: HitTest's public return value and GetHeaderInfo both resolve through the
    /// same GridControl.m_Columns collection via GetUIColumnIndexByStorageIndex/
    /// GetStorageColumnIndexByUIIndex — they are not independent conventions). Combined with
    /// this project's own long-established, live-verified fact that HitTest.ColumnIndex is
    /// 0 for the row-number gutter and 1..N for real columns in an ACTUAL SSMS results grid,
    /// this means: GetCellDataAsString and GetHeaderInfo both take that SAME 0-gutter/1..N
    /// index DIRECTLY, with NO extra +1/-1 translation of their own — a real change from the
    /// old IGridResultSet.ColumnNames/GetSchemaRow, which needed "grid index - 1" because
    /// they lived in a genuinely different, data-only index space. IGridControl.ColumnsNumber
    /// therefore counts the gutter too, so NumberOfDataColumns = grid.ColumnsNumber - 1, not
    /// ColumnsNumber directly.
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

                // Index convention (re-verified live for the portable API — see the class doc
                // comment): HitTest/GetCellDataAsString/GetHeaderInfo all take the SAME GRID
                // column index; 0 is the row-number gutter.
                var p = focused.PointToClient(Control.MousePosition);
                var hit = focused.HitTest(p.X, p.Y);
                if (hit == null || hit.ColumnIndex < 1 || hit.RowIndex < 0)
                {
                    declineReason = "not over a data cell";
                    return false;
                }

                var storage = focused.GridStorage;
                if (storage == null)
                {
                    declineReason = "grid has no readable result set";
                    return false;
                }

                // ColumnsNumber counts the gutter too (see class doc comment) — 1..N are the
                // real data columns.
                int numberOfDataColumns = focused.ColumnsNumber - 1;
                if (hit.ColumnIndex > numberOfDataColumns)
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

                // GetHeaderInfo shares HitTest's index space directly (see class doc comment)
                // — column 1..N, no -1 translation, unlike the old ColumnNames[dataIndex].
                string clickedColumnName = GetHeaderText(focused, hit.ColumnIndex);
                var allColumnNames = new string[numberOfDataColumns];
                for (int i = 0; i < numberOfDataColumns; i++)
                {
                    allColumnNames[i] = GetHeaderText(focused, i + 1);
                }

                cell = new ClickedGridCell
                {
                    Grid = focused,
                    Row = hit.RowIndex,
                    GridCol = hit.ColumnIndex,
                    ColumnName = clickedColumnName,
                    AllColumnNames = allColumnNames,
                    Value = storage.GetCellDataAsString(hit.RowIndex, hit.ColumnIndex), // display text, not a typed value — see docs/newer-grid-api.md
                    NumberOfDataColumns = numberOfDataColumns,
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

        /// <summary>IGridControl.GetHeaderInfo's real signature takes `out` params (confirmed
        /// by the compiler, not just the interop dump — its listing shows `ref` but that
        /// doesn't distinguish IL's `out` parameter flag) — a thin wrapper so call sites
        /// don't repeat the out-locals dance.</summary>
        private static string GetHeaderText(GridControl grid, int gridCol)
        {
            grid.GetHeaderInfo(gridCol, out string text, out System.Drawing.Bitmap bmp);
            return text;
        }
    }
}
