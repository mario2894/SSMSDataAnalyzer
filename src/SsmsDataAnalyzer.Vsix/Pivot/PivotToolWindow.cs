using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Pivot;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// The "Pivot selected rows" tool window. A real <see cref="ToolWindowPane"/>, not a
    /// floating WPF Window — see GridFindToolWindow's doc comment for why: VS owns keyboard
    /// focus/routing for its own registered tool windows, which a floating Window could not
    /// reliably get inside the SSMS host.
    ///
    /// Multi-instance (docs/pivot-plan.md §4 item 12): PivotToolWindow's ProvideToolWindow
    /// attribute in DataAnalyzerPackage carries MultiInstances = true, and PivotRowsCommand
    /// creates/shows a fresh instance id per "Pivot selected rows..." invocation instead of
    /// reusing a single shared id 0. Each instance owns its own snapshot, its own
    /// PivotViewModel/PivotFkLinks resolve, and its own <see cref="Dispose"/> — closing one
    /// window cancels only that window's in-flight FK resolution.
    /// </summary>
    [Guid(PackageGuids.PivotToolWindowPersistenceGuidString)]
    public sealed class PivotToolWindow : ToolWindowPane
    {
        private readonly PivotView _view;

        public PivotToolWindow() : base(null)
        {
            Caption = "Pivot selected rows";
            _view = new PivotView();
            Content = _view;

            // Ctrl+C / Ctrl+A are global VS commands (Edit.Copy / Edit.SelectAll), so VS's
            // command routing claims them before WPF sees a KeyDown — the same mechanism that
            // swallowed F3 in Find in Results. Registering the standard command IDs on this
            // pane's OWN command service scopes them to "while this window has focus"; see
            // GridFindToolWindow's doc comment for the decompilation trail.
            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.CopyCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.SelectAllCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.SelectAll)));
            }
        }

        /// <summary>docs/pivot-plan.md §4 item 12: sets this instance's tab caption to
        /// "Pivot N" (1-based, from its multi-instance id), with an optional short suffix
        /// (the first pivoted column's name) so tabs are distinguishable at a glance. Called
        /// once, right after PivotRowsCommand creates/shows this instance.</summary>
        internal void SetCaption(int id, string firstColumnName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var caption = "Pivot " + (id + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(firstColumnName))
            {
                const int maxSuffixLength = 20;
                var suffix = firstColumnName.Length > maxSuffixLength
                    ? firstColumnName.Substring(0, maxSuffixLength) + "…"
                    : firstColumnName;
                caption += " (" + suffix + ")";
            }
            Caption = caption;
        }

        /// <summary>The single authoritative way to (re)target this window at a fresh pivot
        /// snapshot. Internal: the only caller is PivotRowsCommand in this same assembly.</summary>
        internal void Bind(PivotResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Bind(result, null);
        }

        /// <summary>docs/pivot-plan.md §12: same as <see cref="Bind(PivotResult)"/>, plus the
        /// FK link engine for this snapshot (null = no links; the pivot works exactly as
        /// before). The plain values are shown immediately either way.</summary>
        internal void Bind(PivotResult result, PivotFkLinks fkLinks)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FkLinks = fkLinks;
            _view.Bind(result, fkLinks);
        }

        /// <summary>The FK link engine of the currently bound snapshot, or null.</summary>
        internal PivotFkLinks FkLinks { get; private set; }

        /// <summary>docs/pivot-plan.md §12: cancels any in-flight FK resolution when this pane
        /// is closed/disposed, so a describe call for a window that's gone doesn't keep the
        /// connection open or try to touch UI that no longer exists.</summary>
        protected override void Dispose(bool disposing)
        {
            // Pane disposal happens on the UI thread (VS closes tool windows there).
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _view.CancelPendingResolve();
            }

            base.Dispose(disposing);
        }
    }
}
