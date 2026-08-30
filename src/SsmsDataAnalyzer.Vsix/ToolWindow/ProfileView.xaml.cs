using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SsmsDataAnalyzer.Core;

namespace SsmsDataAnalyzer.Vsix.ToolWindow
{
    public partial class ProfileView : UserControl
    {
        /// <summary>
        /// Every element ProfileGrid_ContextMenuOpening itself inserted into the ContextMenu
        /// last time it ran (the "Go to source ..." MenuItems AND the Separator ahead of
        /// them). Removed by object reference on the next open before anything new is added —
        /// bug fix: an earlier version matched only "is MenuItem" with a Tag check, which
        /// never matched the Separator, so a fresh Separator got inserted on every right-click
        /// while the old one was never removed (the leak the user saw). Reference-based removal
        /// from a list this method fully owns makes that class of leak structurally impossible:
        /// there is no filter to get wrong, because everything in the list came from here.
        /// </summary>
        private readonly List<FrameworkElement> _dynamicGoToSourceItems = new List<FrameworkElement>();

        public ProfileView()
        {
            InitializeComponent();

            // VSTHRD012: prefer the overload that takes an explicit JoinableTaskFactory over
            // the parameterless constructor (which just forwards the ambient
            // ThreadHelper.JoinableTaskFactory internally) — same instance either way in this
            // VS-hosted UserControl, but this keeps the "always provide an instance" discipline
            // visible at every call site rather than only inside ProfileViewModel itself.
            DataContext = new ProfileViewModel(new TableProfiler(), ThreadHelper.JoinableTaskFactory);

            // Find-in-grid wiring: scroll the current match into view (bound state drives the
            // highlight itself — see GridSearchViewModel/ColumnProfileRow — this is the one
            // genuine view-side-effect, since "scroll into view" isn't something a binding can
            // express), and focus the search box the moment the panel opens.
            ViewModel.GridSearch.ScrollToRowRequested += (s, row) => ProfileGrid.ScrollIntoView(row);
            ViewModel.GridSearch.PropertyChanged += GridSearch_PropertyChanged;
        }

        /// <summary>
        /// Typed accessor for DataContext, so callers (ProfileToolWindow.ViewModel) don't
        /// repeat the "as ProfileViewModel" cast at every use site.
        /// </summary>
        public ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        private void GridSearch_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GridSearchViewModel.IsOpen) && ViewModel.GridSearch.IsOpen)
            {
                // Switch back onto the UI thread via ViewModel's own instance
                // JoinableTaskFactory (VSTHRD012: never the ambient ThreadHelper.JoinableTaskFactory
                // when an instance is available; VSTHRD001/VSTHRD110: never
                // Dispatcher.BeginInvoke, and observe the fire-and-forget RunAsync via
                // FileAndForget rather than leaving it unobserved) before calling Focus() — the
                // popup's Visibility binding needs to have actually applied (making the TextBox
                // part of the live visual tree with Focusable/IsVisible true) first, which
                // SwitchToMainThreadAsync's post back through the Dispatcher queue still
                // guarantees, same as BeginInvoke did.
                ViewModel.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ViewModel.JoinableTaskFactory.SwitchToMainThreadAsync();
                    FindTextBox.Focus();
                }).FileAndForget("SsmsDataAnalyzer/ProfileView/FocusFindTextBox");
            }
        }

        /// <summary>
        /// CONTRACT.md Amendment 14/15 "Go to source" UI: builds the FK-aware part of the grid's
        /// context menu fresh on every open, based on which cell/row was right-clicked. Never
        /// touches the DataGrid's own selection or focus, and re-validates gating from the bound
        /// ColumnProfileRow rather than caching anything — the same "read only bound state"
        /// discipline as CellMatchStateConverter above, so virtualization/recycling can't leave a
        /// stale menu item pointing at the wrong row.
        /// </summary>
        private void ProfileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var contextMenu = ProfileGrid.ContextMenu;
            if (contextMenu == null) return;

            // Remove exactly what this method added last time — by reference, not by
            // type/tag matching (see _dynamicGoToSourceItems' doc comment for why that
            // leaked). Safe even the very first time (list starts empty).
            foreach (var element in _dynamicGoToSourceItems)
            {
                contextMenu.Items.Remove(element);
            }
            _dynamicGoToSourceItems.Clear();

            var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            var row = cell?.DataContext as ColumnProfileRow;
            if (row == null) return;

            var header = cell.Column?.Header as string;
            var itemsToAdd = new List<MenuItem>();

            if (row.CanGoToSourceTable)
            {
                var item = new MenuItem { Header = "Go to source table" };
                item.Click += (s, args) => ViewModel.GoToSourceTableAsyncFireAndForget(row);
                itemsToAdd.Add(item);
            }

            if (string.Equals(header, "Min", StringComparison.Ordinal) && row.CanGoToSourceForMin)
            {
                var item = new MenuItem { Header = "Go to source for this value" };
                item.Click += (s, args) => ViewModel.GoToSourceForValueAsyncFireAndForget(row, isMin: true);
                itemsToAdd.Add(item);
            }
            else if (string.Equals(header, "Max", StringComparison.Ordinal) && row.CanGoToSourceForMax)
            {
                var item = new MenuItem { Header = "Go to source for this value" };
                item.Click += (s, args) => ViewModel.GoToSourceForValueAsyncFireAndForget(row, isMin: false);
                itemsToAdd.Add(item);
            }

            if (itemsToAdd.Count == 0) return;

            // Separator + new items, ahead of the static "Find..." entry so the row/cell-specific
            // actions read first. Every element inserted here is recorded in
            // _dynamicGoToSourceItems so the NEXT open removes exactly these, and only these.
            var separator = new Separator();
            contextMenu.Items.Insert(0, separator);
            _dynamicGoToSourceItems.Add(separator);
            for (int i = itemsToAdd.Count - 1; i >= 0; i--)
            {
                contextMenu.Items.Insert(0, itemsToAdd[i]);
                _dynamicGoToSourceItems.Add(itemsToAdd[i]);
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && !(current is T))
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        // CONTRACT.md Amendment 13, latest revision: the manual-entry / explicit-auth-picker
        // UI (including the PasswordBox and its PasswordChanged wiring that used to live here)
        // was removed at the user's explicit request — Object Explorer is now the only entry
        // point, so there is no UI anywhere in this control that can ever collect a password.
        // Do not reintroduce a PasswordBox/SetPassword pairing here casually; if a standalone
        // entry point is ever brought back, it needs its own credential-handling review
        // against CONTRACT.md Amendment 13's rules first.
    }

    /// <summary>Renders null/absent numeric or date values as an em-dash instead of blank/0.</summary>
    public sealed class NullDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "—";
            if (parameter is string format)
            {
                return string.Format(culture, "{0:" + format + "}", value);
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    /// <summary>
    /// Find-in-grid cell highlighting (CONTRACT.md Amendment 13's find-in-grid task):
    /// values[0] = ColumnProfileRow.MatchedColumns (ISet&lt;string&gt;), values[1] =
    /// ColumnProfileRow.CurrentMatchColumn (string), parameter = this cell's column key
    /// (e.g. "Column", "Distinct"). Returns a string state ("None"/"Match"/"Current") rather
    /// than a Brush directly, so the actual colours stay in XAML as DynamicResource lookups
    /// (see UserControl.Resources) — that is what makes them live-update if the user switches
    /// VS theme, which returning a captured Brush from C# would not.
    ///
    /// Deliberately reads ONLY bound state passed through the binding — never reaches into a
    /// DataGridCell/DataGridRow container. Containers are recycled by row/cell virtualization
    /// as the user scrolls; because this converter (via the CellStyle's DataTrigger binding)
    /// re-evaluates against whatever ColumnProfileRow a recycled container is CURRENTLY bound
    /// to, recycling can never smear a stale highlight onto the wrong row — the same class of
    /// bug (reaching into the visual tree instead of driving from bound state) that cost four
    /// rounds on the grid's own rendering earlier in this project.
    /// </summary>
    public sealed class CellMatchStateConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var key = parameter as string;
            if (string.IsNullOrEmpty(key) || values == null || values.Length < 2) return "None";

            var matched = values[0] as ISet<string>;
            var current = values[1] as string;

            if (string.Equals(current, key, StringComparison.Ordinal)) return "Current";
            if (matched != null && matched.Contains(key)) return "Match";
            return "None";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
