using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.Pivot;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.Options;
using SsmsDataAnalyzer.Vsix.ResultsGrid;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// "Pivot selected rows..." — docs/pivot-plan.md sections 1-4. Right-click command on the
    /// results grid (same GUID_SQLEditorGroup:0x0070 group as Find.../Go to source) that turns
    /// the selected rows sideways into a dockable tool window.
    ///
    /// Deliberately statically visible (no DefaultInvisible/DynamicVisibility), unlike
    /// ResultsGridFindCommand/ResultsGridSourceCommand — see the lead's NOTE in
    /// VSCommandTable.vsct next to cmdidPasteAsSqlIn: those flags keep a command hidden until
    /// the package has loaded and BeforeQueryStatus can vote, which hid the paste commands
    /// entirely in a cold session. Execute degrades to a status-bar message when there is no
    /// grid/selection, the same "click loads the package on demand" shape as the paste
    /// commands use.
    ///
    /// Same shell/core split as ResultsGridFindCommand: every method that references
    /// GridControl/IGridStorage is reached only through a same-named "shell" method that
    /// checks ResultsGridCapability.IsSupported first and contains no reference of its own to
    /// any risky type — see ResultsGridCapability's doc comment for why the split (not just an
    /// `if`) is what prevents the JIT from resolving the missing type before the guard runs.
    /// </summary>
    internal sealed class PivotRowsCommand
    {
        private AsyncPackage _package;

        public static PivotRowsCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                OeDiagnostics.Error("'Pivot selected rows' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new PivotRowsCommand(package, commandService);
        }

        private PivotRowsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.PivotRowsCommandId);
            var command = new OleMenuCommand(Execute, commandId);
            commandService.AddCommand(command);
        }

        /// <summary>SHELL — no reference to GridControl or any other results-grid type; see
        /// the class doc comment for why this always runs, even on a build missing the risky
        /// type.</summary>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!ResultsGridCapability.IsSupported)
            {
                ShowStatusBarMessage(ResultsGridCapability.UserFacingMessage("Pivot selected rows"));
                return;
            }

            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Pivot selected rows");
                if (compat != null) { ShowStatusBarMessage(compat); return; }
                OeDiagnostics.Error("'Pivot selected rows' Execute failed", ex);
                // Never put a cell value in an error message — may be sensitive business data
                // (docs/pivot-plan.md Token rules #8). ex.Message here is a .NET exception
                // string, not grid content.
                ShowStatusBarMessage("Pivot selected rows: " + ex.Message);
            }
        }

        /// <summary>CORE — only ever entered when ResultsGridCapability.IsSupported, and
        /// always behind Execute's try/catch. Safe to reference GridControl freely here.
        /// `grid`/`storage` are locals only — dropped when this method returns, per the
        /// snapshot rule: PivotBuilder.Build reads every pivoted cell synchronously before
        /// this method's stack frame unwinds, so nothing keeps a live reference to the grid
        /// afterward.</summary>
        private void ExecuteCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var grid = GridClickCapture.TryGetFocusedGrid();
            if (grid == null)
            {
                ShowStatusBarMessage("Pivot selected rows: no results grid is focused.");
                return;
            }

            var storage = grid.GridStorage; // IGridStorage — see docs/newer-grid-api.md
            if (storage == null)
            {
                ShowStatusBarMessage("Pivot selected rows: the focused grid has no readable result set.");
                return;
            }

            // ColumnsNumber counts the row-number gutter too (GridClickCapture's class doc
            // comment) — 1..N are the real data columns.
            int numberOfDataColumns = grid.ColumnsNumber - 1;
            if (numberOfDataColumns < 1)
            {
                ShowStatusBarMessage("Pivot selected rows: no data columns.");
                return;
            }

            var ranges = new List<RowRange>();
            var selectedCells = grid.SelectedCells;
            if (selectedCells != null)
            {
                foreach (Microsoft.SqlServer.Management.UI.Grid.BlockOfCells block in selectedCells)
                {
                    // BlockOfCells.IsEmpty is the X=Y=Right=Bottom=-1 sentinel (spike S1,
                    // docs/resultsgrid-api.md "Pivot selection" section) — never a real range.
                    if (block == null || block.IsEmpty) continue;
                    ranges.Add(new RowRange(block.Y, block.Bottom));
                }
            }

            if (ranges.Count == 0)
            {
                ShowStatusBarMessage("Pivot selected rows: no rows selected.");
                return;
            }

            var columnNames = new string[numberOfDataColumns];
            for (int i = 0; i < numberOfDataColumns; i++)
                columnNames[i] = GetHeaderText(grid, i + 1);

            int rowLimit = OptionsAccessor.GetPivotRowLimit();

            var result = PivotBuilder.Build(
                columnNames,
                ranges,
                rowLimit,
                (row, gridOrdinal) => storage.GetCellDataAsString(row, gridOrdinal));

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowResultAsync(result);
            }).FileAndForget("SsmsDataAnalyzer/Pivot/PivotRowsCommand/Execute");
        }

        private async Task ShowResultAsync(PivotResult result)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // id: 0 — single shared instance for now (Phase 3 item 12 is multi-instance).
            var pane = await _package.ShowToolWindowAsync(
                typeof(PivotToolWindow),
                id: 0,
                create: true,
                cancellationToken: CancellationToken.None) as PivotToolWindow;

            if (pane == null)
            {
                OeDiagnostics.Error("'Pivot selected rows' could not create/show its tool window.");
                return;
            }

            pane.Bind(result);
        }

        /// <summary>IGridControl.GetHeaderInfo's real signature takes `out` params — mirrors
        /// GridClickCapture.GetHeaderText (private there, so re-declared here rather than
        /// widening that class's surface for one extra caller).</summary>
        private static string GetHeaderText(Microsoft.SqlServer.Management.UI.Grid.GridControl grid, int gridCol)
        {
            grid.GetHeaderInfo(gridCol, out string text, out System.Drawing.Bitmap bmp);
            return text;
        }

        private void ShowStatusBarMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OeDiagnostics.Info(message);
            try
            {
                var statusBar = ((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText(message);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("'Pivot selected rows': could not set the status bar text", ex);
            }
        }
    }
}
