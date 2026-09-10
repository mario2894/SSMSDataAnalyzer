using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.Pivot;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Pivot
{
    public class PivotSelectionTests
    {
        [Fact]
        public void RowRange_FirstAfterLast_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(5, 3));
        }

        [Fact]
        public void RowRange_NegativeFirst_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(-1, 3));
        }

        [Fact]
        public void RowRange_EqualFirstLast_IsSingleRow()
        {
            var range = new RowRange(4, 4);
            Assert.Equal(1, PivotSelection.CountDistinctRows(new[] { range }));
        }

        [Fact]
        public void CountDistinctRows_OverlappingRanges_CountedOnce()
        {
            var ranges = new[] { new RowRange(0, 10), new RowRange(5, 15) };
            Assert.Equal(16, PivotSelection.CountDistinctRows(ranges));
        }

        [Fact]
        public void CountDistinctRows_AdjacentRanges_Merge()
        {
            // 0..4 and 5..9 touch end-to-end and must collapse into one 10-row span.
            var ranges = new[] { new RowRange(0, 4), new RowRange(5, 9) };
            Assert.Equal(10, PivotSelection.CountDistinctRows(ranges));
        }

        [Fact]
        public void CountDistinctRows_UnorderedRanges_StillMergeCorrectly()
        {
            var ranges = new[] { new RowRange(20, 25), new RowRange(0, 5), new RowRange(10, 15) };
            Assert.Equal(18, PivotSelection.CountDistinctRows(ranges));
        }

        [Fact]
        public void CountDistinctRows_DuplicateRanges_CountedOnce()
        {
            var ranges = new[] { new RowRange(0, 5), new RowRange(0, 5), new RowRange(0, 5) };
            Assert.Equal(6, PivotSelection.CountDistinctRows(ranges));
        }

        [Fact]
        public void CountDistinctRows_NullRanges_IsEmpty()
        {
            Assert.Equal(0, PivotSelection.CountDistinctRows(null));
        }

        [Fact]
        public void TakeDistinctRows_NullRanges_IsEmpty()
        {
            Assert.Empty(PivotSelection.TakeDistinctRows(null, 10));
        }

        [Fact]
        public void CountDistinctRows_MillionRowRange_IsInstantAndCorrect()
        {
            var ranges = new[] { new RowRange(0, 999_999) };
            var count = PivotSelection.CountDistinctRows(ranges);
            Assert.Equal(1_000_000, count);
        }

        [Fact]
        public void TakeDistinctRows_MillionRowRange_TakesFirst100Instantly()
        {
            var ranges = new[] { new RowRange(0, 999_999) };
            var rows = PivotSelection.TakeDistinctRows(ranges, 100);

            Assert.Equal(100, rows.Count);
            Assert.Equal(0, rows[0]);
            Assert.Equal(99, rows[99]);
        }

        [Fact]
        public void TakeDistinctRows_AscendingGridOrder_EvenFromUnorderedOverlappingInput()
        {
            var ranges = new List<RowRange>
            {
                new RowRange(50, 60),
                new RowRange(0, 10),
                new RowRange(5, 8), // fully inside the previous range
            };

            var rows = PivotSelection.TakeDistinctRows(ranges, 1000);

            Assert.Equal(0, rows[0]);
            Assert.Equal(10, rows[10]);
            Assert.Equal(50, rows[11]); // jumps straight to 50 after the merged 0..10 block
        }

        [Fact]
        public void TakeDistinctRows_LimitSmallerThanSelection_StopsAtLimit()
        {
            var ranges = new[] { new RowRange(0, 999) };
            var rows = PivotSelection.TakeDistinctRows(ranges, 5);
            Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, rows);
        }

        [Fact]
        public void TakeDistinctRows_ZeroLimit_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PivotSelection.TakeDistinctRows(new[] { new RowRange(0, 5) }, 0));
        }
    }
}
