using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// v0.7.2: the actual find UI, now hosted inside <see cref="GridFindToolWindow"/> instead
    /// of a floating WPF Window -- see that class and this control's XAML doc comment for why.
    /// One tool window instance for the whole SSMS session (matching how VS's own Find and
    /// Replace works -- a single shared window, not one per document); <see cref="Bind"/>
    /// re-targets it to a different GridControl each time the user picks "Find..." on a
    /// different results grid, the same way VS's Find and Replace re-targets itself to
    /// whichever document last had focus.
    /// </summary>
    internal partial class GridFindView : UserControl
    {
        private GridFindState _state;
        private CancellationTokenSource _searchCts;

        /// <summary>Same dual-role tracking Enter/F3's "search vs advance" behavior depends
        /// on: false after every keystroke, true only once a search for the CURRENT box text
        /// has completed.</summary>
        private bool _resultsCurrentForBoxText;

        // Created once, reused for every paint call, disposed with the control -- GDI brushes
        // are a scarce OS handle resource; allocating one per cell per repaint would churn
        // (and, if ever missed, leak) GDI handles instead.
        private readonly System.Drawing.SolidBrush _otherMatchBkBrush = new System.Drawing.SolidBrush(GridThemeColors.OtherMatchBackground());
        private readonly System.Drawing.SolidBrush _otherMatchTextBrush = new System.Drawing.SolidBrush(GridThemeColors.OtherMatchText);

        public GridFindView()
        {
            InitializeComponent();
            ShowNoGridBound();
        }

        /// <summary>
        /// Re-targets this (single, shared) find UI to a different results grid. Unsubscribes
        /// from and clears/repaints whatever grid it was PREVIOUSLY bound to first, so no
        /// stale highlights are left behind on a grid the user has moved away from -- the
        /// same "never leave orphaned state" rule the old floating window followed, just
        /// applied on rebind instead of on close (a persistent tool window is not "closed" by
        /// switching grids the way the old per-grid floating window was).
        /// </summary>
        public void Bind(GridFindState newState)
        {
            if (_state != null)
            {
                _state.Grid.Disposed -= Grid_Disposed;
                _state.Grid.CustomizeCellGDIObjects -= Grid_CustomizeCellGDIObjects;
                if (!_state.Grid.IsDisposed)
                {
                    _state.Clear();
                    _state.Grid.Invalidate();
                }
            }

            _state = newState ?? throw new ArgumentNullException(nameof(newState));
            _searchCts?.Cancel();
            _resultsCurrentForBoxText = false;

            _state.Grid.Disposed += Grid_Disposed;
            _state.Grid.CustomizeCellGDIObjects += Grid_CustomizeCellGDIObjects;

            SearchTextBox.Text = string.Empty;
            SearchTextBox.IsEnabled = true;
            FindButton.IsEnabled = true;
            PreviousButton.IsEnabled = true;
            NextButton.IsEnabled = true;
            ShowNotSearchedYet();

            // VS owns real keyboard focus/routing for its own tool windows -- this is an
            // ordinary WPF focus call, no WindowInteropHelper/Activate workaround needed
            // (that was specifically the floating-Window problem this design change avoids).
            Keyboard.Focus(SearchTextBox);
        }

        /// <summary>
        /// The grid this find UI was bound to went away (its tab closed, or SSMS disposed
        /// it). Per the lead's ruling this is a PERSISTENT tool window, not a floating popup
        /// that closes itself -- so rather than closing the pane, this clears state and
        /// disables the action buttons, leaving a plain "no results grid" placeholder until
        /// the user picks "Find..." on a (possibly different) grid again.
        /// </summary>
        private void Grid_Disposed(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Grid_Disposed(sender, e);
                }).FileAndForget("SsmsDataAnalyzer/GridFindView/GridDisposed");
                return;
            }

            if (_state != null)
            {
                _state.Grid.Disposed -= Grid_Disposed;
                _state.Grid.CustomizeCellGDIObjects -= Grid_CustomizeCellGDIObjects;
                _state = null;
            }
            ShowNoGridBound();
        }

        private void ShowNoGridBound()
        {
            SearchTextBox.Text = string.Empty;
            SearchTextBox.IsEnabled = false;
            FindButton.IsEnabled = false;
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            CancelButton.Visibility = System.Windows.Visibility.Collapsed;
            CounterText.Text = "right-click a query result grid and choose Find...";
        }

        /// <summary>
        /// v0.7.1/v0.7.2: typing must NOT trigger a search (a single keystroke used to launch
        /// a full chunked scan of the entire result set). Typing only invalidates the last
        /// search's results; a search only ever runs from an explicit Find click or Enter.
        /// </summary>
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_state == null) return;
            _resultsCurrentForBoxText = false;
            _state.Clear();
            _state.Grid.Invalidate();
            ShowNotSearchedYet();
        }

        /// <summary>Distinguishes "you haven't searched this text yet" from "you searched and
        /// found zero matches" -- those must not look the same.</summary>
        private void ShowNotSearchedYet()
        {
            if (_state == null) return;
            CounterText.Text = string.IsNullOrEmpty(SearchTextBox.Text) ? string.Empty : "Press Enter to search";
        }

        private void FindButton_Click(object sender, System.Windows.RoutedEventArgs e) => StartSearch();

        private void StartSearch()
        {
            if (_state == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => RunSearchAsync(SearchTextBox.Text))
                .FileAndForget("SsmsDataAnalyzer/GridFindView/Search");
        }

        private async Task RunSearchAsync(string text)
        {
            var state = _state;
            if (state == null) return;

            if (state.IsStale)
            {
                CounterText.Text = "results changed -- reopen from the right-click menu";
                return;
            }

            _resultsCurrentForBoxText = false;
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            CancelButton.Visibility = System.Windows.Visibility.Visible;
            try
            {
                await state.SearchAsync(
                    text,
                    onProgressRow: row => CounterText.Text = $"searching row {row:N0}", // already on the UI thread -- SearchAsync only ever calls back after its own await Task.Yield(), which resumes here via the same synchronization context
                    cancellationToken: token);
            }
            catch (OperationCanceledException)
            {
                // Expected when the user hit Cancel -- nothing to report.
            }
            finally
            {
                CancelButton.Visibility = System.Windows.Visibility.Collapsed;
            }

            if (state != _state) return; // rebound to a different grid while this search was running
            if (token.IsCancellationRequested)
            {
                CounterText.Text = "search cancelled";
                return;
            }

            _resultsCurrentForBoxText = true;
            UpdateCounterText();
            JumpToCurrent();
            state.Grid.Invalidate();
        }

        private void UpdateCounterText()
        {
            if (_state == null) return;
            if (_state.Matches.Count == 0)
            {
                CounterText.Text = "0 of 0";
                return;
            }

            // Never present a partial result as complete -- say so explicitly.
            var total = _state.Capped ? $"{GridFindState.MaxMatches}+ (search narrowed)" : _state.Matches.Count.ToString();
            CounterText.Text = $"{_state.CurrentIndex + 1} of {total}";
        }

        private void JumpToCurrent()
        {
            if (_state == null || !_state.TryGetCurrent(out var match)) return;
            try
            {
                _state.Grid.SelectedCells = new BlockOfCellsCollection(new[] { new BlockOfCells(match.Row, match.GridCol) });
                _state.Grid.EnsureCellIsVisible(match.Row, match.GridCol);
            }
            catch
            {
                // Selection/scroll is a convenience, not the core correctness of Find -- never
                // let a failure here take down the tool window.
            }
        }

        private void NextButton_Click(object sender, System.Windows.RoutedEventArgs e) => AdvanceOrSearch(forward: true);
        private void PreviousButton_Click(object sender, System.Windows.RoutedEventArgs e) => AdvanceOrSearch(forward: false);

        /// <summary>v0.7.3: called from GridFindToolWindow's VSStd97CmdID.FindNext/FindPrev
        /// command handlers, registered on the pane's own local IMenuCommandService -- see
        /// that class's doc comment for why this is the supported route for F3/Shift+F3
        /// (VS's global Edit.FindNext binding claims the raw key before WPF ever sees it,
        /// the same root cause as Ctrl+F, but command routing reaches us where the raw key
        /// does not).</summary>
        internal void FindNextCommand() => AdvanceOrSearch(forward: true);
        internal void FindPreviousCommand() => AdvanceOrSearch(forward: false);

        /// <summary>Enter/Shift+Enter's dual role (Notepad's own find behaves the same way):
        /// if the box has been edited since the last search, this means "search"; once
        /// results exist for the current text, it means "go to the next/previous match"
        /// instead.</summary>
        private void AdvanceOrSearch(bool forward)
        {
            if (_state == null) return;
            if (!_resultsCurrentForBoxText)
            {
                StartSearch();
                return;
            }
            if (_state.IsStale) { CounterText.Text = "results changed -- reopen from the right-click menu"; return; }

            if (forward) _state.MoveNext(); else _state.MovePrevious();
            UpdateCounterText();
            JumpToCurrent();
            _state.Grid.Invalidate();
        }

        private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e) => _searchCts?.Cancel();

        /// <summary>
        /// v0.7.2: this is now a REAL VS tool window control, so VS itself owns keyboard
        /// focus and routing -- no PreviewKeyDown-vs-KeyDown workaround, no leaking into the
        /// SQL editor, and Ctrl+F is deliberately NOT bound here (VS's own Find and Replace
        /// owns that accelerator everywhere in SSMS; stealing it would break Find in Files --
        /// see the lead's explicit ruling). Escape does not close this control -- a persistent
        /// tool window is dismissed the normal VS way (its own tab), not via a key inside it.
        ///
        /// v0.7.3: F3/Shift+F3 are deliberately NOT handled here -- same reason as Ctrl+F.
        /// F3 is globally bound to Edit.FindNext, and VS's command routing claims it before
        /// this WPF KeyDown handler would ever see it, so a case here would be unreachable
        /// dead code (confirmed by the v0.7.2 field report: F3 did nothing, Shift+Enter did).
        /// F3/Shift+F3 are now handled the supported way, via VSStd97CmdID commands
        /// registered on GridFindToolWindow's own local command service -- see that class's
        /// doc comment and FindNextCommand/FindPreviousCommand above.
        /// </summary>
        private void Root_KeyDown(object sender, KeyEventArgs e)
        {
            if (_state == null) return;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (e.Key == Key.Enter)
            {
                AdvanceOrSearch(forward: !shift);
                e.Handled = true;
            }
        }

        /// <summary>
        /// The actual per-cell paint hook (docs finding, lead-endorsed): GridControl calls
        /// this for every cell it is ABOUT TO DRAW, letting us override the background/text
        /// brush. SSMS's own selection highlight is applied AFTER this event for whichever
        /// cell is currently selected -- so the CURRENT match needs no special-casing here at
        /// all; it is simply the selected cell (see JumpToCurrent), and SSMS paints it.
        ///
        /// The ColumnIndex self-check: this convention was never independently confirmed
        /// against GetCellData's (the same "index trap" that has bitten this codebase
        /// before). The FIRST time this fires for a cell this code believes is a match, it
        /// reads that exact cell back via GetCellDataAsString (the same method/convention the
        /// search itself used) and confirms the search text is actually IN there. If it is
        /// not, ColumnIndexConventionVerified latches to false PERMANENTLY for this binding
        /// and no further painting is attempted -- a find that highlights the wrong cell is
        /// worse than one that only selects/scrolls, which keeps working regardless (it never
        /// depended on this event at all).
        /// </summary>
        private void Grid_CustomizeCellGDIObjects(object sender, CustomizeCellGDIObjectsEventArgs e)
        {
            var state = _state;
            if (state == null) return;
            if (state.ColumnIndexConventionVerified == false) return;
            if (state.IsStale) return;

            bool isMatch;
            try
            {
                isMatch = state.IsMatch(e.RowIndex, e.ColumnIndex);
            }
            catch
            {
                return;
            }
            if (!isMatch) return;

            if (state.ColumnIndexConventionVerified == null)
            {
                try
                {
                    var text = state.Grid.GridStorage is Microsoft.SqlServer.Management.UI.Grid.IGridStorage rs
                        ? rs.GetCellDataAsString(e.RowIndex, e.ColumnIndex)
                        : null;
                    var searchText = SearchTextBox.Text;
                    bool verified = !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(searchText)
                        && text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                    state.ColumnIndexConventionVerified = verified;
                    if (!verified)
                    {
                        ObjectExplorer.OeDiagnostics.Warn(
                            $"Results-grid Find: CustomizeCellGDIObjects.ColumnIndex ({e.ColumnIndex}) does not agree with the search's own GetCellData convention for row {e.RowIndex} -- disabling custom cell painting for this binding (selection/scroll navigation is unaffected).");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    state.ColumnIndexConventionVerified = false;
                    ObjectExplorer.OeDiagnostics.Error("Results-grid Find: self-check of the CustomizeCellGDIObjects column convention failed", ex);
                    return;
                }
            }

            // Current match: leave SSMS's own selection highlight alone (it is already the
            // selected cell -- see JumpToCurrent). Any OTHER match gets our lighter tint.
            if (state.IsCurrent(e.RowIndex, e.ColumnIndex)) return;

            e.BKBrush = _otherMatchBkBrush;
            e.TextBrush = _otherMatchTextBrush;
        }
    }
}
