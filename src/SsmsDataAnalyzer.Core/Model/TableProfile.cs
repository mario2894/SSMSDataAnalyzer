using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.Model
{
    public sealed class TableProfile
    {
        public TableRef Table { get; set; }
        public long TotalRows { get; set; }

        /// <summary>From sys.dm_db_partition_stats (pass 0) — free, never touches the table.</summary>
        public long EstimatedRows { get; set; }

        /// <summary>Resolved DateCreated-style column name, or null.</summary>
        public string DateCreatedColumn { get; set; }

        public bool WasSampled { get; set; }
        public TimeSpan Elapsed { get; set; }
        public IList<ColumnProfile> Columns { get; set; }
        public IList<string> Warnings { get; set; }

        public TableProfile()
        {
            Columns = new List<ColumnProfile>();
            Warnings = new List<string>();
        }

        /// <summary>
        /// Deep-enough copy for handing a partial result to a UI thread while profiling continues.
        /// ColumnMeta is shared (immutable in practice); the mutable ColumnProfile rows are cloned.
        /// </summary>
        internal TableProfile SnapshotCopy()
        {
            var copy = new TableProfile
            {
                Table = Table,
                TotalRows = TotalRows,
                EstimatedRows = EstimatedRows,
                DateCreatedColumn = DateCreatedColumn,
                WasSampled = WasSampled,
                Elapsed = Elapsed,
                Columns = new List<ColumnProfile>(Columns.Count),
                Warnings = new List<string>(Warnings)
            };

            foreach (var c in Columns)
            {
                copy.Columns.Add(new ColumnProfile
                {
                    Meta = c.Meta,
                    FilledCount = c.FilledCount,
                    BlankCount = c.BlankCount,
                    DistinctCount = c.DistinctCount,
                    LastFillDate = c.LastFillDate,
                    MinValue = c.MinValue,
                    MaxValue = c.MaxValue,
                    AvgByteLength = c.AvgByteLength,
                    SkipReason = c.SkipReason,
                    TotalRowsContext = c.TotalRowsContext
                });
            }

            return copy;
        }
    }
}
