using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.Peek;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// "Peek source for this value" — the second choice next to "Go to source for this value"
    /// (<see cref="ResultsGridSourceCommand"/>), which this class deliberately does not touch:
    /// same right-click cell, same capture/precondition gates, same
    /// <see cref="ResultsGridGoToSourceResolver"/> call, but on success it shows the referenced
    /// record in a small floating tool window (<see cref="PeekRunner"/>/PeekToolWindow) instead
    /// of opening a new query tab. Both choices stay on the menu; nothing about Go to source's
    /// behavior, wording or messages changes.
    ///
    /// Shell/Core split, JIT-guard reasoning, and the "offered implies works" NULL pre-check
    /// are copied verbatim from ResultsGridSourceCommand — see that class's doc comment for
    /// why the split (not just an `if`) is what actually keeps an unsupported SSMS build from
    /// ever resolving a missing grid type.
    /// </summary>
    internal sealed class ResultsGridPeekCommand
    {
        private readonly AsyncPackage _package;

        /// <summary>A ClickedGridCell when ResultsGridCapability.IsSupported, cast back to
        /// that type only inside *Core methods — see ResultsGridSourceCommand's doc comment
        /// for why this field is `object`, not `ClickedGridCell`.</summary>
        private object _cached;

        private ResultsGridPeekCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.PeekSourceForValueCommandId);
            var command = new OleMenuCommand(Execute, commandId);
            command.BeforeQueryStatus += OnBeforeQueryStatus;
            // Starts invisible — only a right-click that actually lands on a results-grid cell
            // (BeforeQueryStatus) ever makes it visible, same rule as Go to source.
            command.Visible = false;
            commandService.AddCommand(command);
        }

        public static ResultsGridPeekCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                OeDiagnostics.Error("Results-grid 'Peek source for this value' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new ResultsGridPeekCommand(package, commandService);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;

            if (!ResultsGridCapability.IsSupported)
            {
                command.Visible = false;
                _cached = null;
                return;
            }

            try
            {
                OnBeforeQueryStatusCore(command);
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Peek source");
                if (compat == null) OeDiagnostics.Error("Results-grid 'Peek source' BeforeQueryStatus failed", ex);
                _cached = null;
                command.Visible = false;
            }
        }

        /// <summary>CORE — only entered when ResultsGridCapability.IsSupported. Same capture
        /// and NULL-cell pre-check as ResultsGridSourceCommand.OnBeforeQueryStatusCore — see
        /// that method's doc comment for why the NULL case alone is worth pre-checking here
        /// (cheap, already have the value) while the rest of the precondition check stays
        /// deferred to Execute (needs the describe round trip).</summary>
        private void OnBeforeQueryStatusCore(OleMenuCommand command)
        {
            if (GridClickCapture.TryCapture(out var cell, out _))
            {
                _cached = cell;
                command.Visible = true;

                if (string.Equals(cell.Value as string, "NULL", StringComparison.Ordinal))
                {
                    command.Enabled = false;
                    command.Text = $"Peek source for this value ([{cell.ColumnName}] is NULL)";
                }
                else
                {
                    command.Enabled = true;
                    command.Text = "Peek source for this value";
                }
            }
            else
            {
                _cached = null;
                command.Visible = false;
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!ResultsGridCapability.IsSupported)
            {
                FireAndForgetStatus(ResultsGridCapability.UserFacingMessage("Peek source"));
                return;
            }

            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Peek source");
                if (compat != null) { FireAndForgetStatus(compat); return; }
                OeDiagnostics.Error("Results-grid 'Peek source' Execute failed", ex);
                FireAndForgetStatus("Peek source: " + ex.Message);
            }
        }

        private void ExecuteCore()
        {
            var cell = _cached as ClickedGridCell;
            if (cell == null) return;

            _package.JoinableTaskFactory.RunAsync(() => ExecuteAsync(cell))
                .FileAndForget("SsmsDataAnalyzer/ResultsGrid/PeekSource");
        }

        private void FireAndForgetStatus(string message)
        {
            _package.JoinableTaskFactory.RunAsync(() => ShowStatusAsync(message))
                .FileAndForget("SsmsDataAnalyzer/ResultsGrid/PeekSource/Status");
        }

        /// <summary>
        /// CORE (reachable only from ExecuteCore). Identical setup to
        /// ResultsGridSourceCommand.ExecuteAsync through the resolver call — same request, same
        /// decline handling and wording. Only what happens AFTER a successful resolve differs:
        /// instead of QueryWindowAccessor.TryOpenAsync (a new query tab), this runs
        /// <see cref="PeekRunner.RunAsync"/> (a small floating tool window).
        /// </summary>
        private async Task ExecuteAsync(ClickedGridCell cell)
        {
            try
            {
                var ci = cell.Editor?.Connection;
                if (ci == null) { await ShowStatusAsync("Peek source: no connection available for this editor."); return; }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                string tsql = ExecutedQueryTextTracker.TryGetForActiveDocument() ?? ResultsGridSourceCommand.GetSelectionOrFullText(cell.Editor);

                if (!GridConnectionInfo.TryBuild(ci, null, out var editorConnectionString))
                {
                    await ShowStatusAsync("Peek source: could not determine this editor's connection (Entra/token-based sign-ins aren't supported here — see docs/oe-api.md).");
                    return;
                }

                var request = new ResultsGridGoToSourceResolver.Request
                {
                    EditorConnectionString = editorConnectionString,
                    EditorText = tsql,
                    GridColumnOrdinal = cell.GridCol,
                    GridColumnNames = cell.AllColumnNames,
                    GridColumnName = cell.ColumnName,
                    CellValue = cell.Value,
                    NumberOfDataColumns = cell.NumberOfDataColumns,
                    BuildConnectionStringForDatabase = db =>
                        GridConnectionInfo.TryBuild(ci, db, out var cs) ? cs : null
                };

                var result = await ResultsGridGoToSourceResolver.ResolveAsync(request, 15, CancellationToken.None).ConfigureAwait(true);

                if (!result.Success)
                {
                    // Same rule as Go to source: a decline can quote the clicked cell, so it is
                    // never logged verbatim — status bar only.
                    OeDiagnostics.Info("Peek source: declined (reason shown on the status bar only; it may contain a cell value).");
                    await ShowStatusAsync(result.StatusMessage, log: false);
                    return;
                }

                var peekRequest = new PeekRunner.Request
                {
                    Sql = result.GeneratedSql,
                    TargetConnectionString = result.TargetConnectionString,
                    TargetQualifiedName = result.TargetQualifiedName,
                    FilterDescription = $"[{cell.ColumnName}] = {cell.Value}",
                    ChainConnection = ci
                };

                var error = await PeekRunner.RunAsync(_package, peekRequest, CancellationToken.None).ConfigureAwait(true);
                if (error != null)
                {
                    await ShowStatusAsync("Peek source: " + error);
                    return;
                }

                OeDiagnostics.Info("Peek source: opened a peek window for the referenced row.");
                string rowNote = cell.SelectionHasMultipleCells
                    ? " (value from row " + (cell.Row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
                    : string.Empty;
                await ShowStatusAsync(result.StatusMessage + rowNote, log: false);
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Peek source");
                if (compat != null) { await ShowStatusAsync(compat); return; }
                OeDiagnostics.Error("Results-grid 'Peek source' failed", ex);
                await ShowStatusAsync("Peek source: " + ex.Message);
            }
        }

        /// <summary>Same shape as ResultsGridSourceCommand.ShowStatusAsync — see its doc
        /// comment. No risky type reference of its own, so it's safe from the shell path too.</summary>
        private async Task ShowStatusAsync(string message, bool log = true)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (log) OeDiagnostics.Info(message);
            try
            {
                var statusBar = ((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText(message);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Results-grid 'Peek source': could not set the status bar text", ex);
            }
        }
    }
}
