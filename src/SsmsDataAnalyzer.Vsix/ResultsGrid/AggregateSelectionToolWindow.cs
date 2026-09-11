using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.Aggregate;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// "Aggregate selection…" — a small floating, TRANSIENT tool window (id 0, re-targeted on
    /// every invocation) showing COUNT/DISTINCT/SUM/AVERAGE/MIN/MAX for the currently selected
    /// results-grid cells. Same "glance at it, close it" shape as PeekToolWindow/
    /// GridFindToolWindow — see PeekToolWindow's doc comment for the decompilation trail behind
    /// the pane-local Copy/Esc wiring copied here verbatim.
    /// </summary>
    [Guid(PackageGuids.AggregateSelectionToolWindowPersistenceGuidString)]
    public sealed class AggregateSelectionToolWindow : ToolWindowPane
    {
        private readonly AggregateSelectionView _view;

        public AggregateSelectionToolWindow() : base(null)
        {
            Caption = "Aggregate selection";
            _view = new AggregateSelectionView();
            Content = _view;
            _view.PreviewKeyDown += OnViewPreviewKeyDown;

            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                // Pane-local Ctrl+C — see PeekToolWindow's doc comment for why this must be
                // registered on THIS pane's own command service rather than globally.
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.CopyCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));

                // Esc closes the pane immediately, same PaneActivateDocWindow + Escape
                // double-binding as PeekToolWindow (VS routes Esc to Window.ActivateDocumentWindow
                // before WPF ever sees the key inside a tool window).
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.PaneActivateDocWindow)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Escape)));
            }
        }

        /// <summary>Fallback for the (unlikely) case Esc does reach WPF directly — the
        /// pane-local commands above are what VS actually routes it to.</summary>
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

        /// <summary>The single authoritative way AggregateSelectionCommand (re)targets this
        /// window at a freshly computed aggregation.</summary>
        internal void Bind(SelectionAggregationResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.Bind(result);
        }
    }
}
