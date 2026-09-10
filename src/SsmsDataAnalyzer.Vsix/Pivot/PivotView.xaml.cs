using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using SsmsDataAnalyzer.Core.Pivot;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>Code-behind for PivotView.xaml — owns the one thing that can't be done
    /// declaratively: the "Row N" DataGrid columns, whose count changes with every pivot.</summary>
    public partial class PivotView : UserControl
    {
        private readonly PivotViewModel _viewModel;

        public PivotView()
        {
            InitializeComponent();
            _viewModel = new PivotViewModel();
            _viewModel.ColumnsChanged += (s, e) => RebuildRowColumns();
            DataContext = _viewModel;
        }

        /// <summary>The single authoritative way PivotToolWindow (re)targets this view at a
        /// fresh pivot snapshot.</summary>
        internal void Bind(PivotResult result) => _viewModel.Load(result);

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
            {
                var column = new DataGridTextColumn
                {
                    Header = "Row " + (rows[i] + 1).ToString(CultureInfo.InvariantCulture),
                    // Mode=OneWay explicit: PivotRowItem.Values is a read-only array property
                    // — a TwoWay default against a read-only property once blanked a whole
                    // DataGrid elsewhere in this codebase (pitfalls list / ProfileView
                    // Amendment 13, Bug 1). There is no editing here to justify TwoWay anyway.
                    Binding = new Binding("Values[" + i.ToString(CultureInfo.InvariantCulture) + "]") { Mode = BindingMode.OneWay },
                    Width = 110
                };
                PivotGrid.Columns.Add(column);
            }
        }
    }
}
