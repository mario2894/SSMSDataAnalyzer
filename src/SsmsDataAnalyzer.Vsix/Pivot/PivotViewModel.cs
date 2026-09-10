using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using SsmsDataAnalyzer.Core.Pivot;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// Backs PivotView: one PivotResult snapshot (docs/pivot-plan.md section 3) turned into
    /// the transposed row list, the banner text, and the three Phase 2 filters (differing-only,
    /// hide-all-NULL, name substring). Filtering is done with a WPF ICollectionView predicate
    /// rather than rebuilding the list, so the toggles/filter box don't re-touch PivotResult at
    /// all after <see cref="Load"/> — the whole point of the snapshot.
    /// </summary>
    internal sealed class PivotViewModel : INotifyPropertyChanged
    {
        private readonly List<PivotRowItem> _allItems = new List<PivotRowItem>();

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Filtered view PivotView's DataGrid binds ItemsSource to.</summary>
        public ICollectionView Items { get; }

        /// <summary>Pivoted grid rows (0-based), in DataGrid column order — PivotView uses
        /// this to build the "Row N" (1-based) column headers. Raised via ColumnsChanged
        /// whenever a new pivot is loaded, since the column COUNT itself changes per pivot.</summary>
        public IReadOnlyList<long> Rows { get; private set; } = Array.Empty<long>();

        /// <summary>Fired after Load — PivotView.xaml.cs rebuilds its dynamic "Row N" DataGrid
        /// columns in response, since AutoGenerateColumns=False means nothing else would.</summary>
        public event EventHandler ColumnsChanged;

        private string _banner = string.Empty;
        public string Banner
        {
            get => _banner;
            private set { _banner = value; OnPropertyChanged(); }
        }

        private bool _truncated;
        /// <summary>True when the selection had more rows than the pivot row limit allowed —
        /// PivotView makes the banner visually noticeable (bold, VS alert-ish theme brush) when this is set.</summary>
        public bool Truncated
        {
            get => _truncated;
            private set { _truncated = value; OnPropertyChanged(); }
        }

        private bool _showOnlyDiffering;
        public bool ShowOnlyDiffering
        {
            get => _showOnlyDiffering;
            set { _showOnlyDiffering = value; OnPropertyChanged(); Items.Refresh(); }
        }

        private bool _hideAllNull;
        public bool HideAllNull
        {
            get => _hideAllNull;
            set { _hideAllNull = value; OnPropertyChanged(); Items.Refresh(); }
        }

        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set { _filterText = value ?? string.Empty; OnPropertyChanged(); Items.Refresh(); }
        }

        public PivotViewModel()
        {
            Items = CollectionViewSource.GetDefaultView(_allItems);
            Items.Filter = FilterPredicate;
        }

        /// <summary>(Re)points this view model at a fresh pivot snapshot — the whole reason
        /// PivotToolWindow can be reused for the next "Pivot selected rows..." click.</summary>
        public void Load(PivotResult result)
        {
            _allItems.Clear();
            Rows = result.Rows;

            for (var c = 0; c < result.Columns.Count; c++)
            {
                var column = result.Columns[c];
                // result.Values is [columnIndex][rowIndex] (frozen interface, plan §10) — this
                // row's Values IS the array for that column, no copying needed.
                _allItems.Add(new PivotRowItem(column.DisplayName, column.Differs, column.AllNull, result.Values[c]));
            }

            Truncated = result.Truncated;
            Banner = string.Format(
                CultureInfo.InvariantCulture,
                "Showing {0} of {1} selected rows · {2} columns · {3} differ",
                result.Rows.Count,
                result.TotalSelectedRows,
                result.Columns.Count,
                result.DifferingColumnCount);

            // Fire ColumnsChanged before Items.Refresh(): the code-behind rebuilds the "Row N"
            // DataGrid columns first, so by the time filtered rows repaint the bindings they
            // reference already exist.
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            Items.Refresh();
        }

        private bool FilterPredicate(object obj)
        {
            var item = (PivotRowItem)obj;
            if (ShowOnlyDiffering && !item.Differs) return false;
            if (HideAllNull && item.AllNull) return false;
            if (FilterText.Length > 0 && item.DisplayName.IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
