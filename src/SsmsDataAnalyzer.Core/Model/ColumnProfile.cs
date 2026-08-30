using System;

namespace SsmsDataAnalyzer.Core.Model
{
    [Flags]
    public enum ColumnFlag { None = 0, Dead = 1, Constant = 2, Unique = 4, Sparse = 8 }

    public sealed class ColumnProfile
    {
        /// <summary>Fill ratio below which a column is flagged Sparse.</summary>
        public const double SparseThreshold = 0.05;

        public ColumnMeta Meta { get; set; }

        /// <summary>COUNT_BIG(col) — non-NULL rows.</summary>
        public long? FilledCount { get; set; }

        /// <summary>'' or all-whitespace; string columns only.</summary>
        public long? BlankCount { get; set; }

        /// <summary>EXACT COUNT(DISTINCT col). null = not yet computed / skipped.</summary>
        public long? DistinctCount { get; set; }

        /// <summary>MAX(DateCreated) over rows where this column IS NOT NULL.</summary>
        public DateTime? LastFillDate { get; set; }

        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public double? AvgByteLength { get; set; }

        /// <summary>Non-null =&gt; aggregates were deliberately skipped, and why.</summary>
        public string SkipReason { get; set; }

        /// <summary>
        /// The table's row count, supplied by the profiler so <see cref="Flags"/> can be derived
        /// (CONTRACT Amendment 1). Public and settable on purpose: flag derivation must be
        /// exercisable on a hand-constructed ColumnProfile, or unit tests of the flag rules
        /// pass for the wrong reason.
        /// </summary>
        public long TotalRowsContext { get; set; }

        /// <summary>
        /// Derived from <see cref="TotalRowsContext"/> and the counts above.
        /// <para>
        /// CONTRACT Amendment 2: at zero rows every flag is suppressed. Dead must mean "never
        /// populated even though the table holds data" — a finding about the column. In an empty
        /// table it degenerates into restating the row count, and Sparse / Constant / Unique
        /// carry no information either.
        /// </para>
        /// </summary>
        public ColumnFlag Flags
        {
            get
            {
                long total = TotalRowsContext;
                if (total <= 0) return ColumnFlag.None;

                var flags = ColumnFlag.None;

                if (FilledCount.HasValue && FilledCount.Value == 0)
                    flags |= ColumnFlag.Dead;

                if (DistinctCount.HasValue && DistinctCount.Value == 1)
                    flags |= ColumnFlag.Constant;

                if (DistinctCount.HasValue && DistinctCount.Value > 0 && DistinctCount.Value == total)
                    flags |= ColumnFlag.Unique;

                if (FilledCount.HasValue && FilledCount.Value > 0)
                {
                    double fill = (double)FilledCount.Value / total;
                    if (fill < SparseThreshold)
                        flags |= ColumnFlag.Sparse;
                }

                return flags;
            }
        }

        /// <summary>FilledCount / TotalRows, or null when either is unknown.</summary>
        public double? FillRatio
        {
            get
            {
                if (!FilledCount.HasValue || TotalRowsContext <= 0) return null;
                return (double)FilledCount.Value / TotalRowsContext;
            }
        }

        /// <summary>DistinctCount / FilledCount — selectivity — or null when unknown.</summary>
        public double? DistinctRatio
        {
            get
            {
                if (!DistinctCount.HasValue || !FilledCount.HasValue || FilledCount.Value <= 0) return null;
                return (double)DistinctCount.Value / FilledCount.Value;
            }
        }
    }
}
