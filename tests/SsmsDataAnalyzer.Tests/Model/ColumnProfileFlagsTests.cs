using SsmsDataAnalyzer.Core.Model;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Model
{
    /// <summary>
    /// Unit tests for <see cref="ColumnProfile.Flags"/> against hand-constructed instances,
    /// with <see cref="ColumnProfile.TotalRowsContext"/> set explicitly on every case.
    /// <para>
    /// This file exists because of CONTRACT.md Amendment 1: <c>TotalRowsContext</c> was
    /// originally <c>internal</c>, which meant a hand-built <c>ColumnProfile</c> always reported
    /// <c>Flags = None</c> regardless of what the test set up -- any flag assertion written
    /// against one would have passed for the wrong reason (the property silently defaulting to
    /// 0/None, not the rule under test actually firing). Amendment 1 promoted the property to
    /// public+settable specifically so this class of test is meaningful. Every test below sets
    /// <c>TotalRowsContext</c> explicitly and (for the "flag fires" cases) also confirms a
    /// mutated input changes the result, so a silently-inert property would fail loudly here.
    /// </para>
    /// </summary>
    public class ColumnProfileFlagsTests
    {
        private static ColumnMeta Meta(string name = "Col")
        {
            return new ColumnMeta { Name = name, TypeName = "int", MaxLength = 4 };
        }

        [Fact]
        public void Dead_WhenFilledCountIsZero_OnNonEmptyTable()
        {
            var cp = new ColumnProfile { Meta = Meta(), FilledCount = 0, TotalRowsContext = 100 };

            Assert.Equal(ColumnFlag.Dead, cp.Flags & ColumnFlag.Dead);
        }

        [Fact]
        public void NotDead_WhenFilledCountIsPositive()
        {
            var cp = new ColumnProfile { Meta = Meta(), FilledCount = 1, TotalRowsContext = 100 };

            Assert.Equal(ColumnFlag.None, cp.Flags & ColumnFlag.Dead);
        }

        [Fact]
        public void Constant_WhenDistinctCountIsOne()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 100, DistinctCount = 1, TotalRowsContext = 100
            };

            Assert.Equal(ColumnFlag.Constant, cp.Flags & ColumnFlag.Constant);
        }

        [Fact]
        public void NotConstant_WhenDistinctCountIsTwo()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 100, DistinctCount = 2, TotalRowsContext = 100
            };

            Assert.Equal(ColumnFlag.None, cp.Flags & ColumnFlag.Constant);
        }

        [Fact]
        public void Unique_WhenDistinctCountEqualsTotalRows()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 100, DistinctCount = 100, TotalRowsContext = 100
            };

            Assert.Equal(ColumnFlag.Unique, cp.Flags & ColumnFlag.Unique);
        }

        [Fact]
        public void NotUnique_WhenDistinctCountIsLessThanTotalRows()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 99, DistinctCount = 99, TotalRowsContext = 100
            };

            Assert.Equal(ColumnFlag.None, cp.Flags & ColumnFlag.Unique);
        }

        [Fact]
        public void Sparse_WhenFillRatioBelowFivePercent()
        {
            // 4 / 100 = 4% < 5% threshold.
            var cp = new ColumnProfile { Meta = Meta(), FilledCount = 4, TotalRowsContext = 100 };

            Assert.Equal(ColumnFlag.Sparse, cp.Flags & ColumnFlag.Sparse);
        }

        [Fact]
        public void NotSparse_WhenFillRatioAtOrAboveFivePercent()
        {
            // 5 / 100 = 5% == threshold -> not sparse (strictly-less-than rule).
            var cp = new ColumnProfile { Meta = Meta(), FilledCount = 5, TotalRowsContext = 100 };

            Assert.Equal(ColumnFlag.None, cp.Flags & ColumnFlag.Sparse);
        }

        [Fact]
        public void MultipleFlags_CanCombine()
        {
            // Filled in every row (not Dead/Sparse), but only one distinct value: Constant AND,
            // since 1 == TotalRowsContext only when the table itself has exactly 1 row, we pick
            // TotalRowsContext = 1 here specifically to exercise Constant + Unique firing together.
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 1, DistinctCount = 1, TotalRowsContext = 1
            };

            Assert.Equal(ColumnFlag.Constant | ColumnFlag.Unique, cp.Flags);
        }

        // ---- CONTRACT Amendment 2: flags are suppressed entirely on an empty table ---------

        [Fact]
        public void TotalRowsContextZero_SuppressesEveryFlag_EvenDead()
        {
            // FilledCount == 0 would normally fire Dead, but TotalRowsContext == 0 must win.
            var cp = new ColumnProfile { Meta = Meta(), FilledCount = 0, TotalRowsContext = 0 };

            Assert.Equal(ColumnFlag.None, cp.Flags);
        }

        [Fact]
        public void TotalRowsContextZero_SuppressesConstantAndUnique()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 0, DistinctCount = 0, TotalRowsContext = 0
            };

            Assert.Equal(ColumnFlag.None, cp.Flags);
        }

        [Fact]
        public void TotalRowsContextZero_SuppressesFlags_EvenWithNonZeroFilledAndDistinct()
        {
            // Pathological / defensive case: even if a caller mis-stamps FilledCount and
            // DistinctCount on a row claiming TotalRowsContext == 0, no flag may fire. The
            // zero-rows guard must be the first check, not merely coincidentally correct because
            // the other counts also happen to be zero.
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 1, DistinctCount = 1, TotalRowsContext = 0
            };

            Assert.Equal(ColumnFlag.None, cp.Flags);
        }

        [Fact]
        public void TotalRowsContextZero_FillRatioAndDistinctRatio_AreNullNotNaN()
        {
            var cp = new ColumnProfile
            {
                Meta = Meta(), FilledCount = 0, DistinctCount = 0, TotalRowsContext = 0
            };

            Assert.Null(cp.FillRatio);
            Assert.Null(cp.DistinctRatio);
        }
    }
}
