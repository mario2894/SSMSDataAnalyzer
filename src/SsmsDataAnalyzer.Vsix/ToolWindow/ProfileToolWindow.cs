using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ToolWindow
{
    /// <summary>
    /// The "Analyze Data" dockable tool window pane. Hosts <see cref="ProfileView"/> (WPF),
    /// which is bound to <see cref="ProfileViewModel"/>.
    ///
    /// Tier B: this window owns its own server/database/table pickers (seeded, where
    /// possible, from the active query window's connection) rather than depending on an
    /// Object Explorer selection — see PLAN.md section "Object Explorer integration".
    /// </summary>
    [Guid(PackageGuids.ToolWindowPersistenceGuidString)]
    public sealed class ProfileToolWindow : ToolWindowPane
    {
        // CONTRACT.md Amendment 13 hardening: keep our own reference to the view we created,
        // rather than making every caller re-derive it via "Content as ProfileView". Content
        // is a plain object-typed property on WindowPane, and re-deriving through it at
        // multiple call sites is exactly the kind of shape assumption that's fragile if VS
        // ever wraps or re-parents it. This field is set once, here, and never touched again.
        private readonly ProfileView _view;

        public ProfileToolWindow() : base(null)
        {
            Caption = "Analyze Data";

            // BitmapResourceID / BitmapIndex left at defaults; a moniker-based icon can be
            // wired up later via ToolBarIconMonikers, same style as the VSCT bitmap.
            _view = new ProfileView();
            Content = _view;
        }

        /// <summary>
        /// The single authoritative way to reach this window's ViewModel — used by
        /// DataAnalyzerPackage.OnAnalyzeObjectExplorerNode instead of re-deriving it through
        /// "pane.Content as ProfileView" + "view.DataContext as ProfileViewModel" at the call
        /// site, so there is exactly one place that shape assumption lives.
        /// </summary>
        public ProfileViewModel ViewModel => _view?.ViewModel;
    }
}
