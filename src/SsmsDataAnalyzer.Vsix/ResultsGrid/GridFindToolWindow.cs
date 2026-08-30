using System.Runtime.InteropServices;
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
        }

        /// <summary>The single authoritative way to (re)target this window at a specific
        /// results grid -- see GridFindView.Bind for the unsubscribe/clear/rebind sequence.
        /// Internal (not public): GridFindState is itself internal, and the only caller is
        /// ResultsGridFindCommand in this same assembly.</summary>
        internal void Bind(GridFindState state) => _view.Bind(state);
    }
}
