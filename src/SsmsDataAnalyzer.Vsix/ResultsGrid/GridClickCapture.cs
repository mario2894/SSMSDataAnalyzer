using System;
using System.Collections.Generic;
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
        public SqlScriptEditorControl Editor;
    }

    /// <summary>
    /// CONTRACT.md Amendment 16, docs/resultsgrid-api.md sections 4 and 6.4. Every member used
    /// here is public (no reflection into internal SSMS types) — see the doc's confirmation
    /// that GridControl/IGridControl/IGridResultSet/SqlScriptEditorControl are all public.
    ///
    /// Gates 1+2 ("grid index 0 in its tab" / "tab holds exactly one grid") are implemented by
    /// counting <see cref="GridControl"/> descendants of the nearest enclosing WinForms
    /// TabPage, rather than reflecting into the internal GridResultsTabPage/m_gridContainers
    /// SSMS uses internally — TabPage and Control.Controls are both plain BCL/public-API,
    /// so this stays robust across SSMS servicing updates that might rename or restructure
    /// those internal fields (the exact kind of drift the tool-window "Go to source" bug hunt
    /// showed can happen silently).
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

                // Gates 1+2: the tab this grid lives in must hold exactly this one grid.
                Control tabCursor = focused;
                TabPage tabPage = null;
                while (tabCursor != null)
                {
                    if (tabCursor is TabPage tp) { tabPage = tp; break; }
                    tabCursor = tabCursor.Parent;
                }
                if (tabPage == null)
                {
                    declineReason = "couldn't locate the results tab";
                    return false;
                }

                var gridsInTab = new List<GridControl>();
                CollectDescendants(tabPage, gridsInTab);
                if (gridsInTab.Count != 1 || !ReferenceEquals(gridsInTab[0], focused))
                {
                    declineReason = "this tab holds more than one result grid — can't tell which query produced it";
                    return false;
                }

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

        private static void CollectDescendants(Control root, List<GridControl> into)
        {
            foreach (Control child in root.Controls)
            {
                if (child is GridControl gc) into.Add(gc);
                CollectDescendants(child, into);
            }
        }
    }
}
