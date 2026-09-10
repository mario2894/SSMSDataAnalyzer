using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Pivot;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>Code-behind for PivotView.xaml — owns the "Row N" DataGrid columns, whose
    /// count changes with every pivot, and (docs/pivot-plan.md §12) the FK "go to source" icon
    /// wired into each of those columns' cell templates plus the context-menu alternative.</summary>
    public partial class PivotView : UserControl
    {
        private static readonly PivotFkVisibilityConverter FkVisibilityConverter = new PivotFkVisibilityConverter();
        private static readonly PivotFkGoTooltipConverter FkGoTooltipConverter = new PivotFkGoTooltipConverter();
        private static readonly FontFamily FkIconFontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly PivotViewModel _viewModel;

        // §12 context-menu "Go to source…": set by PivotGridContextMenu_Opened, consumed by
        // GoToSourceMenuItem_Click. Never holds a cell value — only the row object + index.
        private PivotRowItem _contextMenuRow;
        private int _contextMenuRowIndex = -1;

        public PivotView()
        {
            InitializeComponent();
            _viewModel = new PivotViewModel();
            _viewModel.ColumnsChanged += (s, e) => RebuildRowColumns();
            // docs/pivot-plan.md §4 item 11: relabel existing headers in place — no column
            // rebuild, no data reload, FK bindings/icons untouched.
            _viewModel.HeaderOptionChanged += (s, e) => RefreshColumnHeaders();
            DataContext = _viewModel;
        }

        /// <summary>The single authoritative way PivotToolWindow (re)targets this view at a
        /// fresh pivot snapshot, with no FK engine.</summary>
        internal void Bind(PivotResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _viewModel.Load(result, null);
        }

        /// <summary>docs/pivot-plan.md §12: same, plus the FK link engine for this snapshot
        /// (null = no links).</summary>
        internal void Bind(PivotResult result, PivotFkLinks fkLinks)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _viewModel.Load(result, fkLinks);
        }

        /// <summary>Called from PivotToolWindow's close/dispose path (§12) to cancel any
        /// in-flight FK resolution for the currently bound snapshot.</summary>
        internal void CancelPendingResolve()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _viewModel.CancelPendingResolve();
        }

        /// <summary>Invoked by PivotToolWindow's pane-local Edit.Copy handler. Only acts when
        /// the grid itself has focus — in the filter TextBox Ctrl+C must copy the TextBox's
        /// own selected text instead.</summary>
        internal void CopyCommand()
        {
            var target = System.Windows.Input.Keyboard.FocusedElement as System.Windows.IInputElement ?? PivotGrid;
            if (System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, target))
                System.Windows.Input.ApplicationCommands.Copy.Execute(null, target);
        }

        /// <summary>Invoked by PivotToolWindow's pane-local Edit.SelectAll handler.</summary>
        internal void SelectAllCommand()
        {
            var target = System.Windows.Input.Keyboard.FocusedElement as System.Windows.IInputElement ?? PivotGrid;
            if (System.Windows.Input.ApplicationCommands.SelectAll.CanExecute(null, target))
                System.Windows.Input.ApplicationCommands.SelectAll.Execute(null, target);
        }

        /// <summary>
        /// Rebuilds the "Row N" columns (header = 1-based grid row, docs/pivot-plan.md
        /// section 1's "Row 3 / Row 7 / Row 12" picture) to match the newest pivot's row
        /// count. Keeps only the single statically-declared "Column" column from
        /// PivotView.xaml at index 0 — everything after it is regenerated here, since
        /// AutoGenerateColumns="False" plus a variable column count can't be expressed in
        /// XAML alone.
        /// </summary>
        private void RebuildRowColumns()
        {
            while (PivotGrid.Columns.Count > 1)
                PivotGrid.Columns.RemoveAt(PivotGrid.Columns.Count - 1);

            var rows = _viewModel.Rows;
            for (var i = 0; i < rows.Count; i++)
                PivotGrid.Columns.Add(BuildValueColumn(i, rows[i]));
        }

        /// <summary>docs/pivot-plan.md §4 item 11: relabels the existing "Row N"/value columns'
        /// headers in place (Header only — CellTemplate/bindings untouched) after the "Header:"
        /// chooser selection changes. No column rebuild, no PivotViewModel.Load.</summary>
        private void RefreshColumnHeaders()
        {
            var rows = _viewModel.Rows;
            for (var i = 1; i < PivotGrid.Columns.Count; i++)
            {
                var rowIndex = i - 1;
                if (rowIndex >= rows.Count) continue;
                PivotGrid.Columns[i].Header = _viewModel.GetHeaderInfo(rowIndex, rows[rowIndex]);
            }
        }

        /// <summary>
        /// Builds one "Row N" column as a DataGridTemplateColumn: a DockPanel with the value
        /// text filling the left (same TextTrimming/single-line look as the old
        /// DataGridTextColumn) and a small flat "go to source" icon docked right, visible only
        /// when PivotRowItem.CanGoFlags[rowIndex] is true (docs/pivot-plan.md §12 / §5 item 5).
        /// Built with FrameworkElementFactory, not a parsed XAML string, so the row index can
        /// be baked into each binding path ("Values[i]" / "CanGoFlags[i]") without needing a
        /// per-column converter — CanGoFlags is computed once per pivot in the view model, not
        /// re-evaluated on every layout pass.
        /// </summary>
        private DataGridTemplateColumn BuildValueColumn(int rowIndex, long gridRow)
        {
            var indexText = rowIndex.ToString(CultureInfo.InvariantCulture);
            var valuePath = "Values[" + indexText + "]";
            var canGoPath = "CanGoFlags[" + indexText + "]";

            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding(valuePath) { Mode = BindingMode.OneWay });
            textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            textFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));

            var glyphFactory = new FrameworkElementFactory(typeof(TextBlock));
            // The "go to source" icon (docs/pivot-plan.md §12 / §5 item 5): U+E8A7
            // "OpenInNewWindow" in Segoe Fluent Icons / Segoe MDL2 Assets, which is what a click
            // does. v0.12.0 used a right arrow, which at the cell edge read as pointing at the
            // next column's value (field screenshot, v0.12.1).
            glyphFactory.SetValue(TextBlock.TextProperty, "\uE8A7");
            glyphFactory.SetValue(TextBlock.FontFamilyProperty, FkIconFontFamily);
            glyphFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            glyphFactory.SetValue(TextBlock.ForegroundProperty, (System.Windows.Media.Brush)FindResource("FkIconBrush"));
            glyphFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyphFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconFactory = new FrameworkElementFactory(typeof(Button));
            iconFactory.SetValue(DockPanel.DockProperty, Dock.Right);
            iconFactory.SetValue(FrameworkElement.WidthProperty, 16.0);
            iconFactory.SetValue(FrameworkElement.HeightProperty, 16.0);
            // Gap on both sides: the right gap keeps the icon off the grid line, so it reads as
            // belonging to this cell's value rather than the next column's (v0.12.1).
            iconFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 4, 0));
            iconFactory.SetValue(UIElement.FocusableProperty, false);
            iconFactory.SetValue(KeyboardNavigation.IsTabStopProperty, false);
            iconFactory.SetValue(Control.CursorProperty, Cursors.Hand);
            iconFactory.SetValue(Control.StyleProperty, (Style)FindResource("FkIconButtonStyle"));
            iconFactory.SetValue(FrameworkElement.TagProperty, rowIndex);
            iconFactory.SetBinding(UIElement.VisibilityProperty,
                new Binding(canGoPath) { Mode = BindingMode.OneWay, Converter = FkVisibilityConverter });

            var tooltipBinding = new MultiBinding { Mode = BindingMode.OneWay, Converter = FkGoTooltipConverter };
            tooltipBinding.Bindings.Add(new Binding("TargetText") { Mode = BindingMode.OneWay });
            tooltipBinding.Bindings.Add(new Binding(valuePath) { Mode = BindingMode.OneWay });
            iconFactory.SetBinding(FrameworkElement.ToolTipProperty, tooltipBinding);

            iconFactory.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnFkIconClick));
            iconFactory.AppendChild(glyphFactory);

            var panelFactory = new FrameworkElementFactory(typeof(DockPanel));
            panelFactory.SetValue(DockPanel.LastChildFillProperty, true);
            // Icon appended first (docked Right, doesn't fill) so the text — appended last —
            // is the one DockPanel stretches to fill the remaining width.
            panelFactory.AppendChild(iconFactory);
            panelFactory.AppendChild(textFactory);

            return new DataGridTemplateColumn
            {
                // item 11: built via the view model so a freshly-created column already
                // reflects the current "Header:" selection (normally "Row number" right after
                // Load — see PivotViewModel.Load resetting SelectedHeaderOption).
                Header = _viewModel.GetHeaderInfo(rowIndex, gridRow),
                Width = 110,
                CellTemplate = new DataTemplate { VisualTree = panelFactory },
                // Ctrl+C pitfall (§12): DataGridTemplateColumn copies nothing by default — this
                // is what makes Ctrl+C still copy the plain value, exactly as the old
                // DataGridTextColumn did.
                ClipboardContentBinding = new Binding(valuePath) { Mode = BindingMode.OneWay }
            };
        }

        /// <summary>§12 icon click: same target as the context-menu item below, just read off
        /// the clicked Button's DataContext (the row) and Tag (the row index baked in above).
        /// Does not stop routing — the cell may become the selected cell as a result, which is
        /// acceptable per the plan.</summary>
        private void OnFkIconClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is FrameworkElement element && element.DataContext is PivotRowItem row && element.Tag is int rowIndex)
                _viewModel.GoToSource(row, rowIndex);
        }

        /// <summary>§12 keyboard/context-menu alternative: enable "Go to source…" only when the
        /// grid's current cell is a value cell (not the frozen "Column" cell) whose column+value
        /// CanGo.</summary>
        private void PivotGridContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _contextMenuRow = null;
            _contextMenuRowIndex = -1;

            var cell = PivotGrid.CurrentCell;
            if (cell.Column != null && cell.Item is PivotRowItem row)
            {
                var columnIndex = PivotGrid.Columns.IndexOf(cell.Column);
                var rowIndex = columnIndex - 1; // column 0 is the frozen "Column" column
                var map = _viewModel.FkLinkMap;
                if (rowIndex >= 0 && rowIndex < row.Values.Length && map != null &&
                    map.CanGo(row.GridOrdinal, row.Values[rowIndex]))
                {
                    _contextMenuRow = row;
                    _contextMenuRowIndex = rowIndex;
                }
            }

            GoToSourceMenuItem.IsEnabled = _contextMenuRow != null;
        }

        private void GoToSourceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_contextMenuRow != null)
                _viewModel.GoToSource(_contextMenuRow, _contextMenuRowIndex);
        }

        /// <summary>docs/pivot-plan.md §4 item 14: copies the currently VISIBLE pivot (post
        /// Show-only-differing / Hide-all-NULL / Filter, item 11's current header labels) as a
        /// Markdown table, via the pure Core formatter (PivotMarkdown.Build) so this method
        /// does no formatting of its own. Clipboard access can fail (another app holds the
        /// clipboard open) — caught, reported to the status bar, never an unhandled
        /// exception, and the failure is never logged with cell values (Token rules #8).</summary>
        private void CopyAsMarkdownMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _viewModel.GetVisibleMarkdownData(out var headers, out var rows);
                var markdown = PivotMarkdown.Build(headers, rows);
                System.Windows.Clipboard.SetText(markdown);
            }
            catch (System.Exception ex)
            {
                OeDiagnostics.Error("'Pivot selected rows': Copy as Markdown table failed (clipboard may be in use)", ex);
            }
        }
    }
}
