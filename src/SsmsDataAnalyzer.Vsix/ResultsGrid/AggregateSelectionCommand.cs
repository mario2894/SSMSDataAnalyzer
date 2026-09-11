using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.Aggregate;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// "Aggregate selection…" — user request: select cells in a results grid, right-click, and
    /// see COUNT/DISTINCT/SUM/AVERAGE/MIN/MAX for the selection in a small floating popup, the
    /// same idea as Redgate SQL Prompt's status-bar aggregates.
    ///
    /// Same shell/core split as PivotRowsCommand/ResultsGridFindCommand: every method that
    /// references GridControl/IGridStorage is reached only through a same-named "shell" method
    /// that checks ResultsGridCapability.IsSupported first — see ResultsGridCapability's doc
    /// comment for why the split (not just an `if`) is what prevents the JIT from resolving a
    /// missing type before the guard runs.
    ///
    /// Deliberately NOT DefaultInvisible/DynamicVisibility — same "cold session" reasoning as
    /// PivotRowsCommand's class doc comment / the cmdidPasteAsSqlIn NOTE in VSCommandTable.vsct.
    /// </summary>
    internal sealed class AggregateSelectionCommand
    {
        /// <summary>Selections larger than this are refused outright (status-bar message, no
        /// read at all) — the lead's explicit cap. Computed from block sizes BEFORE reading
        /// anything, so a huge selection never even starts a scan.</summary>
        internal const int MaxCells = 1_000_000;

        private AsyncPackage _package;

        public static AggregateSelectionCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                OeDiagnostics.Error("'Aggregate selection' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new AggregateSelectionCommand(package, commandService);
        }

        private AggregateSelectionCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.AggregateSelectionCommandId);
            var command = new OleMenuCommand(Execute, commandId);
            command.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        /// <summary>SHELL. Always visible/enabled (see class doc comment) — nothing to vote on.</summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand command)
            {
                command.Visible = true;
                command.Enabled = true;
            }
        }

        /// <summary>SHELL — no reference to GridControl or any other results-grid type.</summary>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!ResultsGridCapability.IsSupported)
            {
                ShowStatusBarMessage(ResultsGridCapability.UserFacingMessage("Aggregate selection"));
                return;
            }

            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Aggregate selection");
                if (compat != null) { ShowStatusBarMessage(compat); return; }
                OeDiagnostics.Error("'Aggregate selection' Execute failed", ex);
                // Never put a cell value in an error message — may be sensitive business data.
                ShowStatusBarMessage("Aggregate selection: " + ex.Message);
            }
        }

        /// <summary>CORE — only ever entered when ResultsGridCapability.IsSupported, and
        /// always behind Execute's try/catch. Safe to reference GridControl freely here.</summary>
        private void ExecuteCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var grid = GridClickCapture.TryGetFocusedGrid();
            if (grid == null)
            {
                ShowStatusBarMessage("Aggregate selection: no results grid is focused.");
                return;
            }

            var storage = grid.GridStorage; // IGridStorage — see docs/newer-grid-api.md
            if (storage == null)
            {
                ShowStatusBarMessage("Aggregate selection: the focused grid has no readable result set.");
                return;
            }

            // ColumnsNumber counts the row-number gutter too (GridClickCapture's class doc
            // comment) — 1..N are the real data columns.
            int lastDataCol = grid.ColumnsNumber - 1;
            if (lastDataCol < 1)
            {
                ShowStatusBarMessage("Aggregate selection: no data columns.");
                return;
            }

            var selectedCells = grid.SelectedCells;
            if (selectedCells == null || selectedCells.Count == 0)
            {
                ShowStatusBarMessage("Aggregate selection: no cells selected.");
                return;
            }

            // Pass 1: collect every non-empty block and compute the total cell count from its
            // SIZE alone — the 1,000,000 cap must be checked before anything is read, never by
            // enumerating and counting as we go.
            var blocks = new List<Microsoft.SqlServer.Management.UI.Grid.BlockOfCells>();
            long totalCellsUpperBound = 0;
            foreach (Microsoft.SqlServer.Management.UI.Grid.BlockOfCells block in selectedCells)
            {
                // BlockOfCells.IsEmpty is the X=Y=Right=Bottom=-1 sentinel (same convention
                // PivotRowsCommand already relies on) — never a real range.
                if (block == null || block.IsEmpty) continue;

                int colFrom = Math.Max(block.X, 1);
                int colTo = Math.Min(block.Right, lastDataCol);
                if (colTo < colFrom || block.Bottom < block.Y) continue;

                blocks.Add(block);
                long rows = block.Bottom - block.Y + 1L;
                long cols = colTo - colFrom + 1L;
                totalCellsUpperBound += rows * cols;
            }

            if (blocks.Count == 0)
            {
                ShowStatusBarMessage("Aggregate selection: no cells selected.");
                return;
            }

            if (totalCellsUpperBound > MaxCells)
            {
                ShowStatusBarMessage(
                    "Aggregate selection: " + totalCellsUpperBound.ToString("N0", CultureInfo.CurrentCulture) +
                    " cells selected — select at most " + MaxCells.ToString("N0", CultureInfo.CurrentCulture) + ".");
                return;
            }

            // Pass 2: read display text for every cell, on the UI thread in one synchronous
            // pass. The 1,000,000 cap above keeps this bounded (comparable in size to what
            // PivotRowsCommand/GridFindState already read synchronously) so this never freezes
            // SSMS for more than a moment on a normal selection. De-duplication only matters
            // when more than one block could overlap (Ctrl-click of multiple ranges).
            var values = new List<string>();
            HashSet<(long, int)> seen = blocks.Count > 1 ? new HashSet<(long, int)>() : null;

            foreach (var block in blocks)
            {
                int colFrom = Math.Max(block.X, 1);
                int colTo = Math.Min(block.Right, lastDataCol);
                for (long row = block.Y; row <= block.Bottom; row++)
                {
                    for (int col = colFrom; col <= colTo; col++)
                    {
                        if (seen != null && !seen.Add((row, col))) continue;
                        string text;
                        try
                        {
                            text = storage.GetCellDataAsString(row, col);
                        }
                        catch
                        {
                            continue; // an unreadable cell should not abort the whole aggregation
                        }
                        values.Add(text ?? string.Empty);
                    }
                }
            }

            var result = SelectionAggregator.Aggregate(values, CultureInfo.CurrentCulture);

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowResultAsync(result);
            }).FileAndForget("SsmsDataAnalyzer/ResultsGrid/AggregateSelectionCommand/Execute");
        }

        private async Task ShowResultAsync(SelectionAggregationResult result)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // id: 0 — single shared, floating, transient instance re-targeted on every
            // invocation, same shape as PeekToolWindow/GridFindToolWindow.
            var pane = await _package.ShowToolWindowAsync(
                typeof(AggregateSelectionToolWindow),
                id: 0,
                create: true,
                cancellationToken: CancellationToken.None) as AggregateSelectionToolWindow;

            if (pane == null)
            {
                OeDiagnostics.Error("'Aggregate selection' could not create/show its tool window.");
                return;
            }

            pane.Bind(result);
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
                OeDiagnostics.Error("'Aggregate selection': could not set the status bar text", ex);
            }
        }
    }
}
