using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SqlGeneration
{
    public class DistinctPlannerTests
    {
        private static readonly TableRef Table = new TableRef { Schema = "dbo", Name = "Orders" };

        private static ColumnMeta Int(string name, string leadingIndex = null)
        {
            return new ColumnMeta { Name = name, TypeName = "int", MaxLength = 4, LeadingIndexName = leadingIndex };
        }

        [Fact]
        public void Plan_IndexBackedColumn_GetsItsOwnSingleColumnIndexQuery()
        {
            var columns = new List<ColumnMeta> { Int("ColIndexed", "IX_Orders_ColIndexed") };
            var plan = DistinctPlanner.Plan(Table, columns, new ProfileOptions());

            var q = Assert.Single(plan.Queries);
            Assert.Equal(DistinctQueryKind.IndexBacked, q.Kind);
            Assert.Contains("WITH (INDEX([IX_Orders_ColIndexed]))", q.Sql);
            Assert.Contains("SELECT DISTINCT [ColIndexed]", q.Sql);
        }

        [Fact]
        public void Plan_ColumnsWithoutIndex_AreBatched()
        {
            var columns = new List<ColumnMeta> { Int("A"), Int("B"), Int("C") };
            var options = new ProfileOptions { DistinctBatchSize = 8 };
            var plan = DistinctPlanner.Plan(Table, columns, options);

            var q = Assert.Single(plan.Queries);
            Assert.Equal(DistinctQueryKind.Batched, q.Kind);
            Assert.Equal(3, q.Columns.Count);
        }

        [Fact]
        public void Plan_BatchedDistinctQuery_CarriesMaxGrantPercent()
        {
            var columns = new List<ColumnMeta> { Int("A"), Int("B") };
            var options = new ProfileOptions { MaxGrantPercent = 25 };
            var plan = DistinctPlanner.Plan(Table, columns, options);

            var q = Assert.Single(plan.Queries);
            Assert.Contains("MAX_GRANT_PERCENT = 25", q.Sql);
        }

        [Fact]
        public void Plan_IndexBackedQuery_DoesNotCarryMaxGrantPercent()
        {
            // Index-backed queries are cheap narrow index scans; the memory-grant cap is only
            // needed for the batched multi-COUNT(DISTINCT) queries.
            var columns = new List<ColumnMeta> { Int("ColIndexed", "IX_Orders_ColIndexed") };
            var plan = DistinctPlanner.Plan(Table, columns, new ProfileOptions { MaxGrantPercent = 25 });

            var q = Assert.Single(plan.Queries);
            Assert.DoesNotContain("MAX_GRANT_PERCENT", q.Sql);
        }

        [Fact]
        public void Plan_NineteenBatchableColumns_BatchSizeEight_ProducesThreeBatches()
        {
            var columns = Enumerable.Range(1, 19).Select(i => Int("Col" + i)).ToList();
            var options = new ProfileOptions { DistinctBatchSize = 8 };
            var plan = DistinctPlanner.Plan(Table, columns, options);

            Assert.Equal(3, plan.Queries.Count);
            Assert.Equal(8, plan.Queries[0].Columns.Count);
            Assert.Equal(8, plan.Queries[1].Columns.Count);
            Assert.Equal(3, plan.Queries[2].Columns.Count);
            Assert.All(plan.Queries, q => Assert.Equal(DistinctQueryKind.Batched, q.Kind));
        }

        [Fact]
        public void Plan_IndexBackedColumns_RunBeforeBatchedColumns()
        {
            // "Cheapest first": the plan order matters for the progressive-fill UI and the
            // cost preview -- index-backed (2a) precedes batched (2b).
            var columns = new List<ColumnMeta>
            {
                Int("NoIndexA"),
                Int("Indexed", "IX_Foo"),
                Int("NoIndexB"),
            };
            var plan = DistinctPlanner.Plan(Table, columns, new ProfileOptions());

            Assert.Equal(DistinctQueryKind.IndexBacked, plan.Queries[0].Kind);
            Assert.All(plan.Queries.Skip(1), q => Assert.Equal(DistinctQueryKind.Batched, q.Kind));
        }

        [Fact]
        public void Plan_LobColumn_GetsOwnBatchOfOnePlacedLast()
        {
            var lob = new ColumnMeta { Name = "BigText", TypeName = "nvarchar", MaxLength = -1 };
            var columns = new List<ColumnMeta> { Int("A"), lob, Int("B") };
            var plan = DistinctPlanner.Plan(Table, columns, new ProfileOptions());

            var last = plan.Queries.Last();
            Assert.Equal(DistinctQueryKind.Lob, last.Kind);
            Assert.Single(last.Columns);
            Assert.Equal("BigText", last.Columns[0].Name);
        }

        [Fact]
        public void Plan_WhenSampled_SkipsEveryColumnWithContractedReason()
        {
            var columns = new List<ColumnMeta> { Int("A"), Int("B", "IX_Foo") };
            var options = new ProfileOptions { SamplePercent = 10 };
            var plan = DistinctPlanner.Plan(Table, columns, options);

            Assert.Empty(plan.Queries);
            Assert.Equal(2, plan.Skipped.Count);
            Assert.All(plan.Skipped.Values,
                reason => Assert.Equal(ProfileOptions.SampledDistinctSkipReason, reason));
        }

        [Fact]
        public void Plan_NeverEmitsApproxCountDistinct()
        {
            var columns = Enumerable.Range(1, 20).Select(i => Int("Col" + i, i == 1 ? "IX_One" : null)).ToList();
            var lob = new ColumnMeta { Name = "BigText", TypeName = "nvarchar", MaxLength = -1 };
            columns.Add(lob);

            var plan = DistinctPlanner.Plan(Table, columns, new ProfileOptions());

            foreach (var q in plan.Queries)
                // Built via concatenation, not as one literal -- see the comment in
                // ProfileSqlBuilderTests.cs for why.
                Assert.DoesNotContain("APPROX_COUNT" + "_DISTINCT", q.Sql, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
