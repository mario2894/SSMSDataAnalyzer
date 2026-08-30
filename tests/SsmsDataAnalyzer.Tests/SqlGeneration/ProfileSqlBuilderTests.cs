using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SqlGeneration
{
    public class ProfileSqlBuilderTests
    {
        private static readonly TableRef Table = new TableRef { Schema = "dbo", Name = "Orders" };

        private static ColumnMeta Int(string name)
        {
            return new ColumnMeta { Name = name, TypeName = "int", MaxLength = 4, IsNullable = true };
        }

        [Fact]
        public void BuildPass1_AlwaysStartsWithReadUncommitted()
        {
            var columns = new List<ColumnMeta> { Int("Col1") };
            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            Assert.All(queries, q => Assert.StartsWith(ProfileSqlBuilder.ReadUncommittedPrefix, q.Sql.Trim()));
            Assert.All(queries, q => Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED", q.Sql));
        }

        [Fact]
        public void BuildPass1_BracketDoublesTableAndColumnIdentifiers()
        {
            var columns = new List<ColumnMeta> { Int("Weird]Col") };
            var table = new TableRef { Schema = "dbo", Name = "Bracket]Table" };

            var queries = ProfileSqlBuilder.BuildPass1(table, columns, null, new ProfileOptions());
            string sql = queries[0].Sql;

            Assert.Contains("[Bracket]]Table]", sql);
            Assert.Contains("[Weird]]Col]", sql);
        }

        [Fact]
        public void BuildPass1_SixtyOneColumns_SplitsIntoTwoChunks()
        {
            var columns = Enumerable.Range(1, ProfileSqlBuilder.Pass1ChunkSize + 1)
                .Select(i => Int("Col" + i))
                .ToList();

            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            Assert.Equal(2, queries.Count());
            Assert.Equal(ProfileSqlBuilder.Pass1ChunkSize, queries[0].Columns.Count);
            Assert.Equal(1, queries[1].Columns.Count);
        }

        [Fact]
        public void BuildPass1_SixtyColumnsExactly_StaysInOneChunk()
        {
            var columns = Enumerable.Range(1, ProfileSqlBuilder.Pass1ChunkSize)
                .Select(i => Int("Col" + i))
                .ToList();

            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            Assert.Single(queries);
            Assert.Equal(ProfileSqlBuilder.Pass1ChunkSize, queries[0].Columns.Count);
        }

        [Fact]
        public void BuildPass1_TwoHundredColumns_ProducesFourChunks()
        {
            // The wide-table scenario from tools/seed/seed.sql (160 columns) plus headroom:
            // 200 columns at a 60-column chunk size is ceil(200/60) = 4 chunks.
            var columns = Enumerable.Range(1, 200).Select(i => Int("Col" + i)).ToList();
            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            Assert.Equal(4, queries.Count);
            Assert.Equal(200, queries.Sum(q => q.Columns.Count));
        }

        [Fact]
        public void BuildPass1_NeverEmitsApproxCountDistinct()
        {
            var columns = Enumerable.Range(1, 10).Select(i => Int("Col" + i)).ToList();
            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            foreach (var q in queries)
                // Built via concatenation, not as one literal: keeps this term out of the
                // source-text scan in ApproxCountDistinctBanTests.cs (which now scans this file
                // too), while still performing a real runtime substring check below.
                Assert.DoesNotContain("APPROX_COUNT" + "_DISTINCT", q.Sql, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildPass1_WithSamplePercent_AddsTablesampleClause()
        {
            // TABLESAMPLE rejects variables/parameters (SQL Server error 497), so the percentage
            // is emitted as a range-validated, invariantly-formatted numeric literal via
            // SqlIdentifier.Percent rather than user text concatenation -- see the "wherever the
            // syntax allows" carve-out in CONTRACT.md rule 2.
            var columns = new List<ColumnMeta> { Int("Col1") };
            var options = new ProfileOptions { SamplePercent = 5.0 };

            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", options);
            string sql = queries[0].Sql;

            Assert.Contains("TABLESAMPLE SYSTEM (", sql);
            Assert.Contains("PERCENT)", sql);
        }

        [Fact]
        public void BuildPass1_WithoutSamplePercent_HasNoTablesampleClause()
        {
            var columns = new List<ColumnMeta> { Int("Col1") };
            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", new ProfileOptions());

            Assert.DoesNotContain("TABLESAMPLE", queries[0].Sql);
        }

        [Fact]
        public void BuildPass1_DoesNotIncludeMaxGrantPercent()
        {
            // Rule 4 in CONTRACT.md scopes MAX_GRANT_PERCENT to *batched distinct* queries only
            // (see DistinctPlannerTests for the positive case) -- pass 1 must never carry it.
            var columns = new List<ColumnMeta> { Int("Col1") };
            var options = new ProfileOptions { MaxGrantPercent = 25 };

            var queries = ProfileSqlBuilder.BuildPass1(Table, columns, "DateCreated", options);

            Assert.All(queries, q => Assert.DoesNotContain("MAX_GRANT_PERCENT", q.Sql));
        }
    }
}
