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
