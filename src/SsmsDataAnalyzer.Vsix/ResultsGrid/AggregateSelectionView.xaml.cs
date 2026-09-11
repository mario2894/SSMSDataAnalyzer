using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Aggregate;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>Code-behind for AggregateSelectionView.xaml — see that file's comments for the
    /// row-hover/copy UX. All clipboard access is guarded: Clipboard.SetText can throw if
    /// another process holds the clipboard open, and that must never surface as an unhandled
    /// exception in a popup meant to be glanced at and closed.</summary>
    public partial class AggregateSelectionView : UserControl
    {
        internal AggregateSelectionViewModel ViewModel { get; } = new AggregateSelectionViewModel();

        public AggregateSelectionView()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        internal void Bind(SelectionAggregationResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.Bind(result);
        }

        /// <summary>Pane-local Ctrl+C (registered by AggregateSelectionToolWindow, same shape
        /// as PivotView.CopyCommand) — copies every row as "Label\tValue" lines.</summary>
        internal void CopyCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CopyToClipboard(ViewModel.CopyAllText);
        }

        private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.ClickCount != 2) return;
            if (sender is FrameworkElement fe && fe.DataContext is AggregateRow row)
                CopyToClipboard(row.Value);
        }

        private void CopyValueMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is MenuItem menuItem &&
                menuItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is FrameworkElement fe &&
                fe.DataContext is AggregateRow row)
            {
                CopyToClipboard(row.Value);
            }
        }

        private static void CopyToClipboard(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (text == null) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("'Aggregate selection': could not copy to clipboard", ex);
            }
        }
    }
}
