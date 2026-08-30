using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Tests.TestSupport;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Integration
{
    /// <summary>
    /// Integration tests against the live dbo.Orders table seeded by tools/seed/seed.sql.
    /// Every asserted number is documented and independently verified via sqlcmd in
    /// tools/seed/expected.md -- this test asserts the profiler reproduces those exact numbers,
    /// it is not the source of truth itself.
    /// </summary>
    [Trait("Category", "Integration")]
    public class OrdersProfileTests
    {
        private static async Task<TableProfile> ProfileOrdersAsync(ProfileOptions options = null)
        {
            var profiler = new TableProfiler();
            return await profiler.ProfileAsync(
                TestDb.ConnectionString, TestDb.Orders, options ?? new ProfileOptions(),
                progress: null, cancellationToken: CancellationToken.None);
        }

        private static ColumnProfile Col(TableProfile profile, string name)
        {
            var cp = profile.Columns.FirstOrDefault(c => c.Meta.Name == name);
            Assert.True(cp != null, "Column " + name + " not found in profile.");
            return cp;
        }

        [Fact]
        public async Task TotalRows_Is1000()
        {
            var profile = await ProfileOrdersAsync();
            Assert.Equal(1000, profile.TotalRows);
        }

        [Fact]
        public async Task DateCreatedColumn_ResolvesToLiteralDateCreated()
        {
            var profile = await ProfileOrdersAsync();
            Assert.Equal("DateCreated", profile.DateCreatedColumn);
        }

        [Fact]
        public async Task ColFilledAlways_MatchesGroundTruth()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColFilledAlways");

            Assert.Equal(1000, cp.FilledCount);
            Assert.Equal(0, cp.BlankCount);
            Assert.Equal(5, cp.DistinctCount);
            Assert.Equal(new DateTime(2024, 2, 19), cp.LastFillDate);
        }

        [Fact]
        public async Task DifferentColumns_HaveDifferentLastFillDates_TheHeadlineFeature()
        {
            // The headline feature: distinct columns stop being populated on genuinely
            // different dates, and the profiler must report each one correctly rather than
            // collapsing everything to the newest row's date.
            var profile = await ProfileOrdersAsync();

            var stoppedDay10 = Col(profile, "ColStoppedDay10").LastFillDate;
            var stoppedDay30 = Col(profile, "ColStoppedDay30").LastFillDate;
            var filledAlways = Col(profile, "ColFilledAlways").LastFillDate;

            Assert.Equal(new DateTime(2024, 1, 10), stoppedDay10);
            Assert.Equal(new DateTime(2024, 1, 30), stoppedDay30);
            Assert.Equal(new DateTime(2024, 2, 19), filledAlways);

            Assert.NotEqual(stoppedDay10, stoppedDay30);
            Assert.NotEqual(stoppedDay30, filledAlways);
            Assert.True(stoppedDay10 < stoppedDay30);
            Assert.True(stoppedDay30 < filledAlways);
        }

        [Fact]
        public async Task ColDead_IsFullyNull_FlaggedDead_LastFillDateIsNull()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColDead");

            Assert.Equal(0, cp.FilledCount);
            Assert.Null(cp.LastFillDate);
            Assert.True((cp.Flags & ColumnFlag.Dead) == ColumnFlag.Dead);
        }

        [Fact]
        public async Task ColConstant_SingleDistinctValue_FlaggedConstant()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColConstant");

            Assert.Equal(1000, cp.FilledCount);
            Assert.Equal(1, cp.DistinctCount);
            Assert.True((cp.Flags & ColumnFlag.Constant) == ColumnFlag.Constant);
        }

        [Fact]
        public async Task ColUniqueGuid_DistinctEqualsRowCount_FlaggedUnique()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColUniqueGuid");

            Assert.Equal(1000, cp.FilledCount);
            Assert.Equal(1000, cp.DistinctCount);
            Assert.True((cp.Flags & ColumnFlag.Unique) == ColumnFlag.Unique);
        }

        [Fact]
        public async Task ColRecentOnly_FilledUnderFivePercent_FlaggedSparse()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColRecentOnly");

            Assert.Equal(20, cp.FilledCount);   // 20 / 1000 = 2%
            Assert.True((cp.Flags & ColumnFlag.Sparse) == ColumnFlag.Sparse);
            Assert.Equal(new DateTime(2024, 2, 19), cp.LastFillDate);
        }

        [Fact]
        public async Task ColStringBlank_DistinguishesNullFromEmptyFromWhitespace()
        {
            // By construction: n%4==0 -> NULL (250), 1 -> '' (250), 2 -> '   ' (250),
            // 3 -> real value (250 distinct). Verified against sqlcmd in expected.md:
            // filled=750, blank=500, distinct=251 (SQL Server's default collation treats
            // trailing spaces as insignificant, so '' and '   ' collapse to one distinct group).
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColStringBlank");

            Assert.Equal(750, cp.FilledCount);
            Assert.Equal(500, cp.BlankCount);
            Assert.Equal(251, cp.DistinctCount);

            // Filled (750) must differ from Blank (500): blank rows are a subset of filled
            // rows, and NULLs (250) must not be double-counted into either bucket.
            Assert.NotEqual(cp.FilledCount, cp.BlankCount);
            Assert.Equal(1000 - cp.FilledCount, 250); // implied null count
        }

        [Fact]
        public async Task ColIndexed_And_ColNotIndexed_ProduceIdenticalDistinctCounts()
        {
            // Same data distribution, one is index-backed (DistinctPlanner 2a fast path),
            // the other batched (2b) -- the two code paths must agree on the answer.
            var profile = await ProfileOrdersAsync();

            var indexed = Col(profile, "ColIndexed");
            var notIndexed = Col(profile, "ColNotIndexed");

            Assert.Equal(200, indexed.DistinctCount);
            Assert.Equal(200, notIndexed.DistinctCount);
            Assert.Equal(indexed.DistinctCount, notIndexed.DistinctCount);
            Assert.Equal("IX_Orders_ColIndexed", indexed.Meta.LeadingIndexName);
            Assert.Null(notIndexed.Meta.LeadingIndexName);
        }

        [Fact]
        public async Task ColXml_AggregatesAreSkipped_ButCountStillWorks()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColXml");

            // xml is NoDistinct in ColumnMeta.Support: COUNT_BIG works, DISTINCT/MIN/MAX don't.
            Assert.Equal(1000, cp.FilledCount);
            Assert.Null(cp.DistinctCount);
            Assert.NotNull(cp.SkipReason);
        }

        [Fact]
        public async Task ColBit_HasNoMinMax_ButDistinctWorks()
        {
            var profile = await ProfileOrdersAsync();
            var cp = Col(profile, "ColBit");

            Assert.Equal(1000, cp.FilledCount);
            Assert.Equal(2, cp.DistinctCount);
            Assert.Null(cp.MinValue);
            Assert.Null(cp.MaxValue);
        }

        [Fact]
        public async Task AllTypesInOrders_ProfileWithoutThrowing()
        {
            // int, bigint, decimal, bit, datetime2, date, uniqueidentifier, nvarchar(50),
            // nvarchar(max), varbinary(max), xml -- every type CONTRACT.md requires the
            // profiler to handle or deliberately skip.
            var profile = await ProfileOrdersAsync();
            Assert.DoesNotContain(profile.Warnings, w => w.Contains("Cancelled") || w.Contains("failed"));
            Assert.True(profile.Columns.Count >= 19);
        }
    }
}
