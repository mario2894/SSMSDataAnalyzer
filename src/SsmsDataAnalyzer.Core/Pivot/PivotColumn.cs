namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>One grid column as pivoted: its identity plus the two highlight flags the
    /// tool window paints (differs / all-null) so the UI doesn't recompute them per render.</summary>
    public sealed class PivotColumn
    {
        public PivotColumn(int gridOrdinal, string name, string displayName, bool differs, bool allNull)
        {
            GridOrdinal = gridOrdinal;
            Name = name;
            DisplayName = displayName;
            Differs = differs;
            AllNull = allNull;
        }

        /// <summary>1-based grid column index.</summary>
        public int GridOrdinal { get; }

        /// <summary>Header text as the grid shows it.</summary>
        public string Name { get; }

        /// <summary>Name, or "Name (2)", "Name (3)" for repeats.</summary>
        public string DisplayName { get; }

        /// <summary>Not all pivoted values are ordinally equal. Always false under 2 rows.</summary>
        public bool Differs { get; }

        /// <summary>Every pivoted value equals <see cref="PivotBuilder.NullDisplayText"/>.</summary>
        public bool AllNull { get; }
    }
}
