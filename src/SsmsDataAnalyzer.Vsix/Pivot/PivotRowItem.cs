namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// One DataGrid row in the pivoted view — i.e. one ORIGINAL grid COLUMN (the pivot turns
    /// rows into columns and columns into rows, docs/pivot-plan.md section 1). Immutable
    /// snapshot data, so every binding to it is Mode=OneWay by convention (see
    /// PivotView.xaml — the pitfall list: a TwoWay default on a read-only property once
    /// blanked the whole grid in ProfileView).
    /// </summary>
    internal sealed class PivotRowItem
    {
        public PivotRowItem(string displayName, bool differs, bool allNull, string[] values)
        {
            DisplayName = displayName;
            Differs = differs;
            AllNull = allNull;
            Values = values;
        }

        /// <summary>The original column's display name — bound to the frozen "Column" DataGrid column.</summary>
        public string DisplayName { get; }

        /// <summary>Not all pivoted values agree (PivotColumn.Differs) — drives the row highlight.</summary>
        public bool Differs { get; }

        /// <summary>Every pivoted value is PivotBuilder.NullDisplayText — drives the "hide all-NULL" toggle.</summary>
        public bool AllNull { get; }

        /// <summary>One value per pivoted grid row, in the same order as PivotViewModel.Rows.
        /// Bound per-cell as "Values[i]" by the dynamically generated "Row N" columns.</summary>
        public string[] Values { get; }
    }
}
