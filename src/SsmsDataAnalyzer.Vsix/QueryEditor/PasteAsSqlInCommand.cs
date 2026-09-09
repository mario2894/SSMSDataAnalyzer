using System;
using System.ComponentModel.Design;
using System.Windows;
using EnvDTE;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.QueryEditor
{
    /// <summary>
    /// "Paste as SQL IN (...)" and "Paste as numeric SQL IN (...)" on the T-SQL editor's
    /// right-click menu. Takes the clipboard's lines and inserts them at the caret as an
    /// IN list.
    ///
    /// The menu group is parented to the STANDARD VS code-window context menu
    /// (guidSHLMainMenu:IDM_VS_CTXT_CODEWIN), not an SSMS-specific one. Confirmed from
    /// SQLEditors.dll's own VSStandardMenus.VsSHLMainMenu
    /// ({d309f791-903f-11d0-9efc-00a0c911004f}) and VSContextMenus.ContextMenu_CODEWIN — SSMS's
    /// query editor is a VS code window, so this is the same long-lived menu VS itself uses.
    /// That matters for version portability: unlike the results-grid work, nothing here
    /// depends on an SSMS-build-specific API.
    ///
    /// All formatting rules live in <see cref="SqlInListFormatter"/> so they are testable
    /// without a live host; this class only does clipboard, editor and status-bar plumbing.
    /// </summary>
    internal sealed class PasteAsSqlInCommand
    {
        private readonly AsyncPackage _package;

        private PasteAsSqlInCommand(AsyncPackage package)
        {
            _package = package;
        }

        public static void Register(AsyncPackage package, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || commandService == null) return;

            var instance = new PasteAsSqlInCommand(package);

            Add(commandService, instance, PackageIds.PasteAsSqlInCommandId, numeric: false);
            Add(commandService, instance, PackageIds.PasteAsNumericSqlInCommandId, numeric: true);
        }

        private static void Add(OleMenuCommandService commandService, PasteAsSqlInCommand instance, int commandId, bool numeric)
        {
            var id = new CommandID(PackageGuids.CommandSetGuid, commandId);
            var command = new OleMenuCommand(
                (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); instance.Execute(numeric); }, id);
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                instance.OnBeforeQueryStatus(s as OleMenuCommand);
            };
            commandService.AddCommand(command);
        }

        /// <summary>
        /// Visible only where it makes sense: an open text document to paste into. Kept cheap —
        /// this runs every time the editor's context menu opens, so it must not touch the
        /// clipboard or do any real work.
        /// </summary>
        private void OnBeforeQueryStatus(OleMenuCommand command)
        {
            if (command == null) return;
            ThreadHelper.ThrowIfNotOnUIThread();

            bool available = false;
            try
            {
                available = TryGetTextSelection(out _);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("PasteAsSqlIn BeforeQueryStatus failed", ex);
            }

            // Enable/disable only — never hide. Visibility is already scoped by the menu the
            // command sits in; hiding here as well would make a missing item ambiguous between
            // "wrong menu", "package not loaded" and "no document", which is exactly the
            // guessing game v0.9.0 cost us.
            command.Visible = true;
            command.Enabled = available;
        }

        private void Execute(bool numeric)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!TryGetTextSelection(out TextSelection selection))
                {
                    SetStatus("Paste as SQL IN: no active query editor to paste into.");
                    return;
                }

                string clipboard = TryGetClipboardText();

                SqlInListFormatter.Result result = SqlInListFormatter.Format(clipboard, numeric);
                if (!result.Success)
                {
                    // Same rule as everywhere else in this extension: say what actually went
                    // wrong, in the status bar, rather than failing silently or pointing at a log.
                    SetStatus(result.Error);
                    return;
                }

                // Insert replaces the current selection, which is what a paste should do.
                selection.Insert(result.Text, (int)vsInsertFlags.vsInsertFlagsContainNewText);

                string duplicates = result.DuplicatesRemoved > 0
                    ? $" ({result.DuplicatesRemoved} duplicate{(result.DuplicatesRemoved == 1 ? "" : "s")} removed)"
                    : string.Empty;
                SetStatus($"Pasted {result.ValueCount} value{(result.ValueCount == 1 ? "" : "s")} as {(numeric ? "a numeric " : "an ")}IN list{duplicates}.");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Paste as SQL IN failed", ex);
                SetStatus("Paste as SQL IN failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The clipboard can legitimately throw (another process holding it open, or a format
        /// that isn't text). Treat that as "no text" rather than an error dialog — the
        /// formatter's own empty-clipboard message is the right thing for the user to see.
        /// </summary>
        private static string TryGetClipboardText()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Reading the clipboard failed", ex);
                return null;
            }
        }

        private bool TryGetTextSelection(out TextSelection selection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            selection = null;

            // Cast to the interface explicitly: AsyncPackage also carries a generic
            // GetService<TService, TInterface> extension, which makes the typeof() form
            // ambiguous to the compiler.
            var serviceProvider = (System.IServiceProvider)_package;
            var dte = serviceProvider.GetService(typeof(DTE)) as DTE;
            Document document = dte?.ActiveDocument;
            if (document == null) return false;

            selection = document.Selection as TextSelection;
            return selection != null;
        }

        private void SetStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var serviceProvider = (System.IServiceProvider)_package;
                var statusBar = serviceProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                if (statusBar == null) return;
                statusBar.FreezeOutput(0);
                statusBar.SetText(message);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Setting the status bar text failed", ex);
            }
        }
    }
}
