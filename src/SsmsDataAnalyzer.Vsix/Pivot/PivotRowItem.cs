using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// One DataGrid row in the pivoted view — i.e. one ORIGINAL grid COLUMN (the pivot turns
    /// rows into columns and columns into rows, docs/pivot-plan.md section 1). The snapshot
    /// data (DisplayName/Differs/AllNull/Values/GridOrdinal) is immutable, so every binding to
    /// it is Mode=OneWay by convention (see PivotView.xaml — the pitfall list: a TwoWay default
    /// on a read-only property once blanked the whole grid in ProfileView).
    ///
    /// The FK properties (IsLinkColumn/TargetText/CanGoFlags) are the one mutable part: they
    /// start "no link" and are filled in once, after PivotFkLinks.ResolveAsync finishes
    /// (docs/pivot-plan.md §12), via <see cref="ApplyFkColumn"/>. INotifyPropertyChanged lets
    /// the already-bound DataGrid cells pick up that single update.
    /// </summary>
    internal sealed class PivotRowItem : INotifyPropertyChanged
    {
        public PivotRowItem(int gridOrdinal, string displayName, bool differs, bool allNull, string[] values)
        {
            GridOrdinal = gridOrdinal;
            DisplayName = displayName;
            Differs = differs;
            AllNull = allNull;
            Values = values;
            CanGoFlags = new bool[values.Length];
        }

        /// <summary>The 1-based grid column ordinal (PivotColumn.GridOrdinal) — NOT the list
        /// index. Duplicate display names or column filtering must never shift this; it is the
        /// key PivotFkLinkMap's members expect.</summary>
        public int GridOrdinal { get; }

        /// <summary>The original column's display name — bound to the frozen "Column" DataGrid column.</summary>
        public string DisplayName { get; }

        /// <summary>Not all pivoted values agree (PivotColumn.Differs) — drives the row highlight.</summary>
        public bool Differs { get; }

        /// <summary>Every pivoted value is PivotBuilder.NullDisplayText — drives the "hide all-NULL" toggle.</summary>
        public bool AllNull { get; }

        /// <summary>One value per pivoted grid row, in the same order as PivotViewModel.Rows.
        /// Bound per-cell as "Values[i]" by the dynamically generated "Row N" columns.</summary>
        public string[] Values { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isLinkColumn;
        /// <summary>True once resolved and PivotFkLinkMap.IsLinkColumn(GridOrdinal) says so —
        /// drives the 🔗 marker on the "Column" cell.</summary>
        public bool IsLinkColumn
        {
            get => _isLinkColumn;
            private set { _isLinkColumn = value; OnPropertyChanged(); }
        }

        private string _targetText;
        /// <summary>PivotFkLinkMap.GetTargetText(GridOrdinal) once resolved, else null.</summary>
        public string TargetText
        {
            get => _targetText;
            private set { _targetText = value; OnPropertyChanged(); }
        }

        private bool[] _canGoFlags;
        /// <summary>One flag per pivoted grid row (same indexing as Values), computed once
        /// after resolve via PivotFkLinkMap.CanGo(GridOrdinal, Values[i]) — not re-evaluated
        /// per layout pass. All false before resolution / for a non-link column. Bound
        /// per-cell as "CanGoFlags[i]"; replacing the array and raising PropertyChanged on
        /// this property re-evaluates those indexer bindings.</summary>
        public bool[] CanGoFlags
        {
            get => _canGoFlags;
            private set { _canGoFlags = value; OnPropertyChanged(); }
        }

        /// <summary>Called once per pivot, after PivotFkLinks.ResolveAsync finishes, for every
        /// row (docs/pivot-plan.md §12). <paramref name="canGo"/> may be null (non-link column)
        /// — an all-false array is used instead so bindings never see a null array.</summary>
        public void ApplyFkColumn(bool isLink, string targetText, bool[] canGo)
        {
            IsLinkColumn = isLink;
            TargetText = targetText;
            CanGoFlags = canGo ?? new bool[Values.Length];
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
