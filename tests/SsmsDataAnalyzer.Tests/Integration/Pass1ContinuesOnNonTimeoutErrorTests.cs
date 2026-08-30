using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using SsmsDataAnalyzer.Tests.TestSupport;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Integration
{
    /// <summary>
    /// CONTRACT.md Amendment 10, branch 2: a non-timeout <c>SqlException</c> during a pass-1
    /// chunk must NOT halt pass 1 -- the next chunk is attempted and, being column-specific
    /// (different columns, no shared cause), is expected to succeed. This is the "continue"
    /// half of Amendment 10; <see cref="Pass1HaltsOnTimeoutTests"/> covers the "halt" half.
    /// <para>
    /// Deterministic and cheap by construction: a computed column dividing by zero
    /// (SQL error 8134) fails instantly, no calibration or CPU-burning needed, and no data
    /// volume beyond a handful of rows. The probe table has one more column than
    /// <see cref="ProfileSqlBuilder.Pass1ChunkSize"/> + 3 so pass 1 is guaranteed to split into
    /// exactly two chunks, with the bad column confined to chunk 1 and ordinary int columns
    /// filling out chunk 2.
    /// </para>
    /// </summary>
    [Trait("Category", "Integration")]
    public class Pass1ContinuesOnNonTimeoutErrorTests : IAsyncLifetime
    {
        private const string TableName = "__Pass1ErrorProbe";
        private static readonly TableRef Probe = new TableRef { Schema = "dbo", Name = TableName };

        // 4 non-filler columns (Id, DateCreated, ZeroCol, BadCol) + enough filler columns to
        // land exactly 4 columns in chunk 2, regardless of Core's actual chunk size.
        private static readonly int ChunkSize = ProfileSqlBuilder.Pass1ChunkSize;
        private const int NonFillerColumns = 4;
        private const int Chunk2ColumnCount = 4;
        private static readonly int FillerColumnCount = ChunkSize + Chunk2ColumnCount - NonFillerColumns;
        private const int RowCount = 5;

        public async Task InitializeAsync()
        {
            var sb = new StringBuilder();
            sb.Append("IF OBJECT_ID('dbo.").Append(TableName).Append("') IS NOT NULL DROP TABLE dbo.").Append(TableName).AppendLine(";");
            sb.Append("CREATE TABLE dbo.").Append(TableName).AppendLine(" (");
            sb.AppendLine("    Id int IDENTITY PRIMARY KEY,");
            sb.AppendLine("    DateCreated datetime2(0) NOT NULL,");
            sb.AppendLine("    ZeroCol int NOT NULL,");
            // Computed, virtual (not persisted): evaluating any aggregate over BadCol forces a
            // divide-by-zero (SQL error 8134) for every row where ZeroCol = 0.
            sb.AppendLine("    BadCol AS (1 / ZeroCol),");
            for (int i = 2; i <= FillerColumnCount + 1; i++)
                sb.Append("    Col").Append(i.ToString("000")).AppendLine(" int NOT NULL,");
            sb.Length -= (Environment.NewLine.Length + 1); // trim trailing comma
            sb.AppendLine();
            sb.AppendLine(");");

            sb.Append("INSERT INTO dbo.").Append(TableName).Append(" (DateCreated, ZeroCol");
            for (int i = 2; i <= FillerColumnCount + 1; i++)
                sb.Append(", Col").Append(i.ToString("000"));
            sb.AppendLine(")");
            sb.AppendLine("SELECT SYSDATETIME(), 0" + string.Concat(System.Linq.Enumerable.Range(2, FillerColumnCount).Select(i => ", " + i)));
            sb.Append("FROM (VALUES ").Append(string.Join(",", System.Linq.Enumerable.Repeat("(1)", RowCount))).AppendLine(") v(x);");

            using (var connection = new SqlConnection(TestDb.ConnectionString))
            {
                await connection.OpenAsync();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 60;
                    cmd.CommandText = sb.ToString();
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task DisposeAsync()
        {
            using (var connection = new SqlConnection(TestDb.ConnectionString))
            {
                await connection.OpenAsync();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
                    cmd.CommandText = "IF OBJECT_ID('dbo." + TableName + "') IS NOT NULL DROP TABLE dbo." + TableName + ";";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        [Fact]
        public async Task NonTimeoutError_InChunk1_StillLetsChunk2Profile()
        {
            var profiler = new TableProfiler();
            var profile = await profiler.ProfileAsync(
                TestDb.ConnectionString, Probe, new ProfileOptions(), progress: null, cancellationToken: CancellationToken.None);

            Assert.NotNull(profile);

            // Chunk 1 (Id, DateCreated, ZeroCol, BadCol, and the first filler columns) failed as
            // a whole -- the divide-by-zero takes down every column sharing that scan, not just
            // BadCol -- and each carries a SkipReason that is NOT a timeout.
            var badCol = profile.Columns.First(c => c.Meta.Name == "BadCol");
            Assert.Null(badCol.FilledCount);
            Assert.NotNull(badCol.SkipReason);
            Assert.DoesNotContain("timed out", badCol.SkipReason, StringComparison.OrdinalIgnoreCase);

            var zeroCol = profile.Columns.First(c => c.Meta.Name == "ZeroCol");
            Assert.Null(zeroCol.FilledCount);
            Assert.NotNull(zeroCol.SkipReason);

            // Chunk 2 -- ordinary int columns with no relationship to the bad column -- must
            // have been attempted AND succeeded: this is the actual behavioural difference from
            // the timeout branch (Pass1HaltsOnTimeoutTests), where the equivalent chunk 2 is
            // never attempted at all.
            var lastFillerName = "Col" + (FillerColumnCount + 1).ToString("000");
            var lastFiller = profile.Columns.First(c => c.Meta.Name == lastFillerName);
            Assert.Equal(RowCount, lastFiller.FilledCount);
            Assert.Null(lastFiller.SkipReason);

            // Proof the chunk 2 query actually ran to completion: it independently reports the
            // real row count, which chunk 1 (having failed) never got the chance to.
            Assert.Equal(RowCount, profile.TotalRows);

            // A non-timeout pass-1 failure still produced a warning (matching Amendment 8's
            // pre-existing behaviour), just not one naming a timeout.
            Assert.Contains(profile.Warnings, w => w.IndexOf("Pass 1", StringComparison.Ordinal) >= 0);
            Assert.DoesNotContain(profile.Warnings, w => w.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
