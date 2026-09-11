using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.Pivot;
using SsmsDataAnalyzer.Vsix.Pivot;

namespace SsmsDataAnalyzer.Vsix.Peek
{
    /// <summary>
    /// "Peek source for this value" / pivot's "Peek source…" — a small floating tool window
    /// (docs' explicit ask: "like Find in Results", closable immediately) that shows the
    /// referenced record right away instead of opening a new query tab. "Go to source"
    /// (the new-query-tab choice) is completely unchanged; this is a second, additional choice.
    ///
    /// Reuses <see cref="PivotView"/>/PivotViewModel wholesale rather than a new view: a
    /// peeked record is exactly a one-range "pivot" of the rows the peek query returned — same
    /// transposed grid, same FK link icons (chained navigation via a fresh <see cref="PivotFkLinks"/>
    /// per peek), same Ctrl+C / Copy as Markdown. See <see cref="PeekRunner"/> for how the
    /// query is run and formatted before it ever reaches this window.
    ///
    /// Single, floating, TRANSIENT instance (id 0; MultiInstances stays at its ToolWindowPane
    /// default of false) re-targeted on every peek — the same "one shared window, re-pointed"
    /// shape as GridFindToolWindow, deliberately NOT PivotToolWindow's per-invocation
    /// multi-instance tabs: a peek is meant to be glanced at and closed, not accumulated.
    /// </summary>
    [Guid(PackageGuids.PeekToolWindowPersistenceGuidString)]
    public sealed class PeekToolWindow : ToolWindowPane
    {
        private readonly PivotView _view;

        public PeekToolWindow() : base(null)
        {
            Caption = "Peek";
            _view = new PivotView();
            Content = _view;
            _view.PreviewKeyDown += OnViewPreviewKeyDown;

            // Same pane-local Copy/SelectAll shape as PivotToolWindow (see its doc comment for
            // the decompilation trail): Ctrl+C/Ctrl+A are global VS commands, so registering
            // these standard IDs on this pane's OWN command service is what scopes them to
            // "while this window has focus" instead of stealing them everywhere in SSMS.
            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.CopyCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.SelectAllCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.SelectAll)));

                // v0.14.1 field report: Esc only returned focus to SSMS. In a tool window VS
                // binds Esc to Window.ActivateDocumentWindow (VSStd97 PaneActivateDocWindow,
                // 289) and runs it before WPF ever sees the key — the same mechanism that
                // swallowed F3 in Find in Results. Claiming it (and the generic Escape, 743) on
                // this pane's own command service scopes the override to the peek window only.
                commandService.AddCommand(new MenuCommand(
                    (s, e) => CloseWindow(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.PaneActivateDocWindow)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => CloseWindow(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Escape)));
            }
        }

        /// <summary>Esc closes the peek window immediately — the point of "closable in a few
        /// seconds" (the window's own titlebar X always works regardless). Kept as a fallback
        /// for the case the key does reach WPF; the pane-local commands above are what VS
        /// actually routes Esc to.</summary>
        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseWindow();
        }

        private void CloseWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Frame is IVsWindowFrame frame)
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        /// <summary>The single authoritative way <see cref="PeekRunner"/> (re)targets this
        /// window at a freshly peeked record.</summary>
        internal void Bind(PivotResult result, PivotFkLinks fkLinks, string bannerText, string caption)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Caption = caption;
            _view.Bind(result, fkLinks, bannerText);
        }

        /// <summary>Cancels any in-flight FK resolution when this pane is closed — same
        /// reasoning as PivotToolWindow.Dispose.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _view.CancelPendingResolve();
            }
            base.Dispose(disposing);
        }
    }
}
