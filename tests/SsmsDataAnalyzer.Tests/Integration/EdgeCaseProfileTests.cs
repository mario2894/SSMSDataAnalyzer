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
    [Trait("Category", "Integration")]
    public class EdgeCaseProfileTests
    {
        private static async Task<TableProfile> ProfileAsync(TableRef table, ProfileOptions options = null)
        {
            var profiler = new TableProfiler();
            return await profiler.ProfileAsync(
                TestDb.ConnectionString, table, options ?? new ProfileOptions(),
                progress: null, cancellationToken: CancellationToken.None);
        }

        [Fact]
        public async Task EmptyTable_ZeroRows_DoesNotThrow_AndReportsZeros()
        {
            // The classic division-by-zero risk: fill % / distinct % over 0 rows.
            var profile = await ProfileAsync(TestDb.EmptyTable);

            Assert.Equal(0, profile.TotalRows);
            Assert.NotEmpty(profile.Columns);

            foreach (var cp in profile.Columns)
            {
                Assert.Equal(0, cp.FilledCount);
                Assert.Null(cp.LastFillDate);
                Assert.Null(cp.FillRatio);      // must be null, not a NaN or a divide-by-zero throw
                Assert.Null(cp.DistinctRatio);
            }
        }

        [Fact]
        public async Task EmptyTable_NoColumnCarriesDeadFlag_PerAmendment2()
        {
            // CONTRACT Amendment 2, raised from this exact table: FilledCount == 0 on every
            // column of a 0-row table would trivially satisfy the literal Dead rule for all of
            // them. The ruling: at TotalRowsContext == 0, every column's Flags is None instead.
            var profile = await ProfileAsync(TestDb.EmptyTable);

            Assert.NotEmpty(profile.Columns);
            foreach (var cp in profile.Columns)
            {
                Assert.Equal(0, cp.TotalRowsContext);
                Assert.Equal(ColumnFlag.None, cp.Flags);
                Assert.NotEqual(ColumnFlag.Dead, cp.Flags & ColumnFlag.Dead);
            }
        }

        [Fact]
        public async Task EmptyTable_WarningsContainExactAmendment2Text()
        {
            var profile = await ProfileAsync(TestDb.EmptyTable);

            Assert.Contains("Table is empty — per-column flags are not meaningful.", profile.Warnings);
        }

        [Fact]
        public async Task NoDateTable_HasNoDateCreatedColumn_ButStillProfilesEverythingElse()
        {
            var profile = await ProfileAsync(TestDb.NoDateTable);

            Assert.Null(profile.DateCreatedColumn);
            Assert.Equal(20, profile.TotalRows);
            Assert.Contains(profile.Warnings, w => w.Contains("No DateCreated-style column found"));

            foreach (var cp in profile.Columns)
                Assert.Null(cp.LastFillDate);

            var idCol = profile.Columns.First(c => c.Meta.Name == "Id");
            Assert.Equal(20, idCol.FilledCount);
            Assert.Equal(20, idCol.DistinctCount);
        }

        [Fact]
        public async Task FallbackDateTable_ResolvesToCreatedOn_ViaCandidateListOrder()
        {
            // DateCreated and CreatedDate are both absent; CreatedOn (3rd in the default
            // candidate list) must be the one picked.
            var profile = await ProfileAsync(TestDb.FallbackDateTable);

            Assert.Equal("CreatedOn", profile.DateCreatedColumn);
            Assert.Equal(20, profile.TotalRows);

            var valueCol = profile.Columns.First(c => c.Meta.Name == "Value");
            Assert.Equal(new DateTime(2024, 5, 4), valueCol.LastFillDate);
        }

        [Fact]
        public async Task BracketNamedTable_ProfilesCorrectly_ProvingBracketDoublingEndToEnd()
        {
            var profile = await ProfileAsync(TestDb.BracketTable);

            Assert.Equal(10, profile.TotalRows);
            Assert.Equal("[dbo].[Bracket]]Table]", TestDb.BracketTable.QualifiedName);

            var valueCol = profile.Columns.First(c => c.Meta.Name == "Value]Col");
            Assert.Equal(10, valueCol.FilledCount);
            Assert.Equal(10, valueCol.DistinctCount);
        }

        [Fact]
        public async Task WideTable_160Columns_ProfilesAllOfThemAcrossChunkedScans()
        {
            var profile = await ProfileAsync(TestDb.WideTable);

            Assert.Equal(120, profile.TotalRows);
            // RowId (identity PK) + DateCreated + 160 ColNNN columns = 162 metadata columns total.
            Assert.Equal(162, profile.Columns.Count);

            var boundaryNames = new[] { "Col001", "Col060", "Col061", "Col120", "Col121", "Col160" };
            foreach (var name in boundaryNames)
            {
                var cp = profile.Columns.First(c => c.Meta.Name == name);
                Assert.Equal(120, cp.FilledCount);
                Assert.Equal(37, cp.DistinctCount);
                Assert.Equal(new DateTime(2024, 3, 12), cp.LastFillDate);
            }
        }

        [Fact]
        public async Task Sampling_ForcesDistinctCountNull_WithContractedSkipReason()
        {
            var options = new ProfileOptions { SamplePercent = 10.0 };
            var profile = await ProfileAsync(TestDb.Orders, options);

            Assert.True(profile.WasSampled);
            Assert.NotEmpty(profile.Columns);

            foreach (var cp in profile.Columns)
            {
                Assert.Null(cp.DistinctCount);
                if (cp.Meta.Support != AggregateSupport.MetadataOnly)
                {
                    Assert.Equal(ProfileOptions.SampledDistinctSkipReason, cp.SkipReason);
                }
            }
        }
    }
}
