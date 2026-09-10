using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>Snapshot of a pivoted selection -- copied out of the grid once, at command
    /// time, so the pivot survives a closed tab or a re-run query.</summary>
    public sealed class PivotResult
    {
        public PivotResult(
            IReadOnlyList<long> rows,
            IReadOnlyList<PivotColumn> columns,
            string[][] values,
            long totalSelectedRows,
            int differingColumnCount)
        {
            Rows = rows;
            Columns = columns;
            Values = values;
            TotalSelectedRows = totalSelectedRows;
            DifferingColumnCount = differingColumnCount;
        }

        /// <summary>Pivoted grid rows, ascending.</summary>
        public IReadOnlyList<long> Rows { get; }

        /// <summary>Grid order.</summary>
        public IReadOnlyList<PivotColumn> Columns { get; }

        /// <summary>[columnIndex][rowIndex]; never null, a missing cell is "".</summary>
        public string[][] Values { get; }

        public long TotalSelectedRows { get; }

        /// <summary>True when the selection had more distinct rows than the row limit allowed.</summary>
        public bool Truncated => TotalSelectedRows > Rows.Count;

        public int DifferingColumnCount { get; }
    }
}
