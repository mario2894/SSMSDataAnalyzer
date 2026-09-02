using System;
using System.ComponentModel.Design;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
    ///
    /// v0.7.6 field report (SSMS 22.3 vs our 22.9 dev build): a type this whole feature needs
    /// -- IGridResultSet -- does not exist in an older 22.x build of SqlEditors.dll, and
    /// clicking a results-grid feature there surfaced a raw .NET "Could not load type" modal
    /// dialog. Every method here that touches GridControl/IGridResultSet is now a "Core"
    /// method, reached ONLY from a same-named shell method that checks
    /// ResultsGridCapability.IsSupported FIRST and contains no reference of its own to any
    /// risky type -- see ResultsGridCapability's doc comment for exactly why that separation
    /// (not just an `if` inside one method) is what actually prevents the JIT from resolving
    /// the missing type before the guard can run. _cachedGrid is `object`, not `GridControl`,
    /// for the same reason -- a field of a risky type is itself a compile-time reference this
    /// class must not have.
    /// </summary>
    internal sealed class ResultsGridFindCommand
    {
        private AsyncPackage _package;

        /// <summary>A GridControl when ResultsGridCapability.IsSupported, cast back to that
        /// type only inside *Core methods -- see the class doc comment for why this can't be
        /// typed as GridControl at the field level.</summary>
        private object _cachedGrid;

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

        /// <summary>
        /// SHELL -- no reference to GridControl or any other results-grid type. Lead's
        /// explicit ask: "no menu item is better than one that errors" on an unsupported
        /// build. This method's own JIT compilation can never fail for that reason, so this
        /// check always runs, even on the SSMS build that started this whole investigation.
        /// </summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;

            if (!ResultsGridCapability.IsSupported)
            {
                command.Visible = false;
                _cachedGrid = null;
                return;
            }

            try
            {
                OnBeforeQueryStatusCore(command);
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Find in Results");
                if (compat == null) OeDiagnostics.Error("Results-grid 'Find' BeforeQueryStatus failed", ex);
                _cachedGrid = null;
                command.Visible = false;
            }
        }

        /// <summary>CORE -- only ever entered when ResultsGridCapability.IsSupported, and
        /// always behind the shell's try/catch. Safe to reference GridControl freely here.</summary>
        private void OnBeforeQueryStatusCore(OleMenuCommand command)
        {
            var grid = GridClickCapture.TryGetFocusedGrid();
            _cachedGrid = grid;
            command.Visible = grid != null;
        }

        /// <summary>SHELL -- see OnBeforeQueryStatus's doc comment; same reasoning.</summary>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!ResultsGridCapability.IsSupported)
            {
                ShowStatusBarMessage(ResultsGridCapability.UserFacingMessage("Find in Results"));
                return;
            }

            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                var compat = ResultsGridCapability.DescribeIfCompatibilityException(ex, "Find in Results");
                if (compat != null) { ShowStatusBarMessage(compat); return; }
                OeDiagnostics.Error("Results-grid 'Find' Execute failed", ex);
                ShowStatusBarMessage("Find in Results: " + ex.Message);
            }
        }

        /// <summary>CORE -- see OnBeforeQueryStatusCore's doc comment; same reasoning.</summary>
        private void ExecuteCore()
        {
            var grid = _cachedGrid as Microsoft.SqlServer.Management.UI.Grid.GridControl;
            if (grid == null) return;

            var resultSet = grid.GridStorage as Microsoft.SqlServer.Management.QueryExecution.IGridResultSet;
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

        /// <summary>
        /// v0.7.6: Find previously had no status-bar reporter at all (OeDiagnostics only) --
        /// added so an unsupported-build decline is visible where the lead's ergonomics rule
        /// requires it, not just in the ActivityLog. Same VS status-bar service
        /// ResultsGridSourceCommand.ShowStatusAsync already uses.
        /// </summary>
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
                OeDiagnostics.Error("Results-grid 'Find': could not set the status bar text", ex);
            }
        }
    }
}
