using System;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>
    /// Inclusive range of grid rows, 0-based. Kept as a struct so a Ctrl+A selection over a
    /// million rows is a handful of these, not a million longs.
    /// </summary>
    public readonly struct RowRange
    {
        public RowRange(long first, long last)
        {
            // Fail fast here rather than downstream in the merge/count math, where a bad
            // range would silently produce a wrong (too small or negative) count.
            if (first < 0) throw new ArgumentOutOfRangeException(nameof(first), first, "First must be >= 0.");
            if (last < first) throw new ArgumentOutOfRangeException(nameof(last), last, "Last must be >= First.");

            First = first;
            Last = last;
        }

        public long First { get; }
        public long Last { get; }
    }
}
