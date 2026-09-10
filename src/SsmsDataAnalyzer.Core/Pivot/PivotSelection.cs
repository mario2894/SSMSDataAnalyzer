using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>
    /// Turns a set of possibly-overlapping row ranges (one Ctrl-click block per range) into
    /// distinct-row counts and prefixes without ever walking row-by-row -- the whole point
    /// is that Ctrl+A on a million-row grid stays cheap.
    /// </summary>
    public static class PivotSelection
    {
        /// <summary>Distinct rows covered by all ranges (overlaps counted once).</summary>
        public static long CountDistinctRows(IEnumerable<RowRange> ranges)
        {
            long count = 0;
            foreach (var merged in Merge(ranges))
                count += merged.Last - merged.First + 1;
            return count;
        }

        /// <summary>First <paramref name="limit"/> distinct rows, ascending grid order.</summary>
        public static IReadOnlyList<long> TakeDistinctRows(IEnumerable<RowRange> ranges, int limit)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be >= 1.");

            var result = new List<long>();
            foreach (var merged in Merge(ranges))
            {
                for (var row = merged.First; row <= merged.Last && result.Count < limit; row++)
                    result.Add(row);

                if (result.Count >= limit) break;
            }
            return result;
        }

        /// <summary>
        /// Sorts ranges by start and merges anything overlapping or touching end-to-end
        /// (Last of one == First - 1 of the next), so a selection built from many small
        /// Ctrl-click blocks collapses to the few intervals it actually represents.
        /// A null enumerable is an empty selection, not an error -- callers pass whatever the
        /// grid's SelectedCells adapter hands them, and "nothing selected" is a real case.
        /// </summary>
        private static List<RowRange> Merge(IEnumerable<RowRange> ranges)
        {
            var merged = new List<RowRange>();
            if (ranges == null) return merged;

            var sorted = ranges.OrderBy(r => r.First).ToList();
            foreach (var range in sorted)
            {
                if (merged.Count > 0 && range.First <= merged[merged.Count - 1].Last + 1)
                {
                    var last = merged[merged.Count - 1];
                    if (range.Last > last.Last)
                        merged[merged.Count - 1] = new RowRange(last.First, range.Last);
                }
                else
                {
                    merged.Add(range);
                }
            }
            return merged;
        }
    }
}
