using System;
using System.ComponentModel.Design;
using System.Threading;
using Microsoft.SqlServer.Management.QueryExecution;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// User request: "Find... on right click in result grid of SSMS, we have it already in
    /// tool and it works great." Menu item on the same results-grid context menu as "Go to
    /// source for this value" (docs/resultsgrid-api.md / CONTRACT.md Amendment 16's group).
    ///
    /// v0.7.2: this is now the ONLY entry point. Through v0.7.1 there was also a Ctrl+F
    /// wired directly to the focused GridControl's own WinForms KeyDown event -- removed
    /// entirely per the lead's explicit ruling after the user's field report showed pressing
    /// Ctrl+F with the grid focused opened VS's own "Find and Replace" (Find in Files)
    /// dialog: VS intercepts that accelerator through its own command routing BEFORE any
    /// WinForms KeyDown fires on a hosted control, so that subscriber could never actually
    /// see the key. Re-adding it would only relitigate a dead end -- VS owns Ctrl+F
    /// everywhere in SSMS, and stealing it back would break Find in Files for the user, which
    /// the lead judged a far worse trade than losing a shortcut.
    ///
    /// A genuinely free alternative .vsct &lt;KeyBinding&gt; was considered (the lead's
    /// explicit "offer it as a bonus if you can VERIFY it's unbound, do not assume"). This
    /// could not be verified: whether a given chord is free in SSMS 22 is a property of the
    /// live Tools &gt; Options &gt; Keyboard scheme (and whatever the SQL/Object Explorer/
    /// editor command tables bind at runtime), which is not observable from static
    /// decompilation the way the ColumnIndex/GetCellData conventions were -- there is no
    /// binary artifact to grep that lists "every bound chord in the live scheme." Per the
    /// same "verify, do not assume" discipline, no keybinding was added; right-click is the
    /// only entry point, and this is called out explicitly to the user/lead rather than
    /// silently guessing at a chord that might turn out to collide with something else.
    ///
    /// Opens a single shared "Find in Results" tool window (GridFindToolWindow) and re-binds
    /// it to whichever GridControl "Find..." was just invoked on -- see that class's doc
    /// comment for why this is one shared instance rather than one per grid.
    /// </summary>
    internal sealed class ResultsGridFindCommand
    {
        private AsyncPackage _package;
        private GridControl _cachedGrid;

        public static ResultsGridFindCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                OeDiagnostics.Error("Results-grid 'Find' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new ResultsGridFindCommand(package, commandService);
        }

        private ResultsGridFindCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.GridFindCommandId);
            var command = new OleMenuCommand(Execute, commandId);
            command.BeforeQueryStatus += OnBeforeQueryStatus;
            command.Visible = false;
            commandService.AddCommand(command);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            try
            {
                var grid = GridClickCapture.TryGetFocusedGrid();
                _cachedGrid = grid;
                command.Visible = grid != null;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Results-grid 'Find' BeforeQueryStatus failed", ex);
                _cachedGrid = null;
                command.Visible = false;
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var grid = _cachedGrid;
            if (grid == null) return;

            var resultSet = grid.GridStorage as IGridResultSet;
            if (resultSet == null)
            {
                OeDiagnostics.Warn("Results-grid 'Find': the focused grid has no readable IGridResultSet -- nothing to search.");
                return;
            }

            var state = new GridFindState(grid, resultSet);

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowAndBindAsync(state);
            }).FileAndForget("SsmsDataAnalyzer/ResultsGridFindCommand/Execute");
        }

        private async Task ShowAndBindAsync(GridFindState state)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // id: 0 -- single shared instance, see GridFindToolWindow's doc comment.
            var pane = await _package.ShowToolWindowAsync(
                typeof(GridFindToolWindow),
                id: 0,
                create: true,
                cancellationToken: CancellationToken.None) as GridFindToolWindow;

            if (pane == null)
            {
                OeDiagnostics.Error("Results-grid 'Find' could not create/show its tool window.");
                return;
            }

            pane.Bind(state);
        }
    }
}
