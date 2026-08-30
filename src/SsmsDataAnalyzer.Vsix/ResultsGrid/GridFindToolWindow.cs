using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// v0.7.2: the "Find in Results" tool window. Replaces the floating WPF <c>Window</c>
    /// used through v0.7.1, which field-testing showed could not reliably own keyboard focus
    /// or routing inside the SSMS host -- typing sometimes reached the box, but Backspace and
    /// Enter leaked into the SQL editor underneath, and Ctrl+F was intercepted by VS's own
    /// Find and Replace before it ever reached the results grid at all (confirmed by a
    /// screenshot of that dialog opening instead). Lead's explicit ruling: host this in a
    /// real <see cref="ToolWindowPane"/> instead, the same pattern already proven by
    /// ToolWindow.ProfileToolWindow -- VS owns focus, keyboard routing and message
    /// pre-translation for its own registered tool windows, which is exactly the thing that
    /// was broken about the floating Window.
    ///
    /// Single shared instance for the whole SSMS session (matching how VS's own Find and
    /// Replace behaves -- one window, re-targeted to whatever it was last invoked against)
    /// rather than one instance per GridControl: ResultsGridFindCommand always shows this
    /// same tool window and calls <see cref="GridFindView.Bind"/> to re-point it at whichever
    /// grid "Find..." was just invoked on. This avoids piling up one tool-window tab per
    /// results grid the user has ever searched, and matches the idiomatic VS "utility tool
    /// window" shape (Find Results, Error List, etc. are also singletons). The view's own
    /// Bind method is responsible for unsubscribing from and clearing/repainting whichever
    /// grid it was previously bound to before switching -- see its doc comment.
    ///
    /// Deliberately NOT bound to Ctrl+F: VS's own Find and Replace owns that accelerator
    /// everywhere in SSMS (confirmed above), and stealing it would break Find in Files for
    /// the user. The results-grid context menu's "Find..." item is the only entry point --
    /// see ResultsGridFindCommand's doc comment for the .vsct keybinding investigation.
    ///
    /// v0.7.3 field report: F3 did nothing (Shift+Enter worked). Root cause is the same
    /// shape as the Ctrl+F finding -- F3 is globally bound to Edit.FindNext, and VS's own
    /// command routing claims it before our WPF KeyDown handler ever sees it (see
    /// GridFindView.Root_KeyDown, which no longer handles F3/Shift+F3 for this reason).
    ///
    /// UNLIKE Ctrl+F, though, there IS a supported route: decompiling
    /// Microsoft.VisualStudio.Shell.15.0.dll and Microsoft.VisualStudio.Shell.Framework.dll
    /// confirms WindowPane's explicit IOleCommandTarget.Exec/QueryStatus implementations
    /// delegate straight to GetService(typeof(IOleCommandTarget)), which resolves (in
    /// WindowPane.GetService, both branches share one IsEquivalentTo check that returns the
    /// same field) to the exact same lazily-created OleMenuCommandService instance as
    /// GetService(typeof(IMenuCommandService)) -- i.e. a command registered on THIS pane's
    /// own local command service IS what VS's shell consults through this pane's
    /// IOleCommandTarget once it has focus. That is a fundamentally different mechanism from
    /// the raw WinForms KeyDown that never fired for the old floating window's Ctrl+F
    /// handler: this is the actual routing surface VS's own key-processing walks. Standard
    /// command IDs (VSStd97CmdID.FindNext/FindPrev under GUID_VSStandardCommandSet97,
    /// decompiled from Microsoft.VisualStudio.VSConstants in Shell.Framework.dll: FindNext =
    /// 370, FindPrev = 371) registered here are scoped to "while this pane has focus" the
    /// same way a tool window can locally claim Copy/Paste/Delete without stealing them
    /// elsewhere -- unlike Ctrl+F there is no global rebind and Find in Files/the editor's
    /// own F3 are unaffected when focus is anywhere else.
    ///
    /// What is NOT independently confirmed by decompilation (couldn't be -- this is host
    /// command-routing PRIORITY, not a static property of any one assembly): that VS's
    /// dispatcher actually consults a focused tool window's local IOleCommandTarget before
    /// falling back to the global F3 keybinding, for every focus state this pane can be in.
    /// That is exactly the class of host-integration behavior the lead flagged after Ctrl+F
    /// -- de-risked as far as static analysis allows, but the live host is still what proves
    /// it. If it does not work live, the fallback (Enter/Shift+Enter/the arrow buttons) is
    /// already fully functional and unaffected by this change either way.
    /// </summary>
    [Guid(PackageGuids.GridFindToolWindowPersistenceGuidString)]
    public sealed class GridFindToolWindow : ToolWindowPane
    {
        private readonly GridFindView _view;

        public GridFindToolWindow() : base(null)
        {
            Caption = "Find in Results";
            _view = new GridFindView();
            Content = _view;

            // See the class doc comment: registering these standard command IDs on this
            // pane's OWN command service (not the package's global one from
            // AsyncPackage.GetServiceAsync(typeof(IMenuCommandService)), which is a
            // different instance) is what lets VS route F3/Shift+F3 to us only while this
            // pane has focus, without touching the global Edit.FindNext/FindPrevious
            // keybinding anywhere else in SSMS.
            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.FindNextCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FindNext)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.FindPreviousCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FindPrev)));
            }
        }

        /// <summary>The single authoritative way to (re)target this window at a specific
        /// results grid -- see GridFindView.Bind for the unsubscribe/clear/rebind sequence.
        /// Internal (not public): GridFindState is itself internal, and the only caller is
        /// ResultsGridFindCommand in this same assembly.</summary>
        internal void Bind(GridFindState state) => _view.Bind(state);
    }
}
