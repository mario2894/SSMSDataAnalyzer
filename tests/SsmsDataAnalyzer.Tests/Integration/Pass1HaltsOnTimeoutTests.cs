using System;
using System.Diagnostics;
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
    /// CONTRACT.md Amendment 10, branch 1: a pass-1 CommandTimeout (SQL error -2) must HALT
    /// pass 1 immediately -- later chunks are never attempted, and the warning names them --
    /// unlike a non-timeout error, which lets later chunks proceed
    /// (<see cref="Pass1ContinuesOnNonTimeoutErrorTests"/>).
    /// <para>
    /// <b>Why this doesn't reproduce a timeout the way the withdrawn first attempt did.</b>
    /// The original version of this test used a disposable ~15,000,000-row scratch table,
    /// relying on the scan simply being slow. The lead flagged two real problems with that: it
    /// is a race against hardware/disk-cache warmth rather than a deterministic assertion (a
    /// faster disk, more RAM, or a second run with the buffer pool already warm could finish
    /// under the timeout and fail the test for no real regression), and creating/dropping that
    /// table grew the database files by ~1.3 GB that SQL Server never gives back on its own.
    /// </para>
    /// <para>
    /// This version replaces data volume with CPU-bound work behind a computed column
    /// (<c>dbo.__BurnFn</c>, a scalar function doing a fixed-iteration loop), invoked per row by
    /// the pass-1 aggregate scan. Two properties make this the right lever, both verified
    /// empirically before relying on them:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// It is CPU-bound, not I/O-bound, so it does <b>not</b> suffer the disk-cache flakiness the
    /// lead was worried about -- there is no buffer pool page to warm between runs; the same
    /// fixed number of loop iterations costs roughly the same CPU time every time on the same
    /// machine.
    /// </item>
    /// <item>
    /// A per-row function call inside a table scan yields to SQL Server's attention/cancel
    /// check between rows, unlike one giant single-batch <c>WHILE</c> loop (tried first, and
    /// empirically confirmed NOT to abort: a 20-million-iteration ad hoc loop given a 2-second
    /// client timeout ran to full ~36s completion regardless). Calibrated directly against this
    /// technique: 30 rows x a 1-million-iteration-per-call function (~1.7s/call, ~52s total if
    /// uninterrupted) given a 3-second client timeout aborted at ~3.1s -- a clean, prompt,
    /// reproducible timeout.
    /// </item>
    /// </list>
    /// <para>
    /// Footprint stays tiny: a handful of rows, no persisted computed columns, so no meaningful
    /// growth of the database files -- unlike the row-volume approach this replaces.
    /// </para>
    /// </summary>
    [Trait("Category", "Integration")]
    public class Pass1HaltsOnTimeoutTests : IAsyncLifetime
    {
        private const string TableName = "__Pass1TimeoutProbe";
        private const string FunctionName = "__BurnFn";
        private static readonly TableRef Probe = new TableRef { Schema = "dbo", Name = TableName };

        // 3 non-filler columns (Id, DateCreated, SlowCol) + enough filler columns to land
        // exactly 4 columns in chunk 2, regardless of Core's actual chunk size.
        private static readonly int ChunkSize = ProfileSqlBuilder.Pass1ChunkSize;
        private const int NonFillerColumns = 3;
        private const int Chunk2ColumnCount = 4;
        private static readonly int FillerColumnCount = ChunkSize + Chunk2ColumnCount - NonFillerColumns;
        private const int RowCount = 5;

        // Generous fixed iteration count: on this machine, ~1M iterations cost ~1.7s per call
        // (see the calibration note above). 2M gives headroom against faster CPUs while staying
        // well clear of the sub-second range where per-call timing noise would matter.
        private const long BurnIterations = 2_000_000;

        public async Task InitializeAsync()
        {
            using (var connection = new SqlConnection(TestDb.ConnectionString))
            {
                await connection.OpenAsync();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
                    cmd.CommandText =
                        "IF OBJECT_ID('dbo." + TableName + "') IS NOT NULL DROP TABLE dbo." + TableName + ";" +
                        "IF OBJECT_ID('dbo." + FunctionName + "') IS NOT NULL DROP FUNCTION dbo." + FunctionName + ";";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
                    cmd.CommandText =
                        "CREATE FUNCTION dbo." + FunctionName + "(@x int) RETURNS int " +
                        "AS BEGIN " +
                        "  DECLARE @i bigint = 0, @acc bigint = 0; " +
                        "  WHILE @i < " + BurnIterations.ToString() + " " +
                        "  BEGIN " +
                        "    SET @acc = @acc + (@i % 7) + @x; " +
                        "    SET @i = @i + 1; " +
                        "  END " +
                        "  RETURN CAST(@acc % 2147483647 AS int); " +
                        "END";
                    await cmd.ExecuteNonQueryAsync();
                }

                var sb = new StringBuilder();
                sb.Append("CREATE TABLE dbo.").Append(TableName).AppendLine(" (");
                sb.AppendLine("    Id int IDENTITY PRIMARY KEY,");
                sb.AppendLine("    DateCreated datetime2(0) NOT NULL,");
                // Virtual (not persisted) computed column: every aggregate reference re-runs the
                // burn function for that row, so a single chunk-1 query invokes it many times.
                sb.Append("    SlowCol AS (dbo.").Append(FunctionName).AppendLine("(Id)),");
                for (int i = 2; i <= FillerColumnCount + 1; i++)
                    sb.Append("    Col").Append(i.ToString("000")).AppendLine(" int NOT NULL,");
                sb.Length -= (Environment.NewLine.Length + 1); // trim trailing comma
                sb.AppendLine();
                sb.AppendLine(");");

                sb.Append("INSERT INTO dbo.").Append(TableName).Append(" (DateCreated");
                for (int i = 2; i <= FillerColumnCount + 1; i++)
                    sb.Append(", Col").Append(i.ToString("000"));
                sb.AppendLine(")");
                sb.AppendLine("SELECT SYSDATETIME()" + string.Concat(Enumerable.Range(2, FillerColumnCount).Select(i => ", " + i)));
                sb.Append("FROM (VALUES ").Append(string.Join(",", Enumerable.Repeat("(1)", RowCount))).AppendLine(") v(x);");

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
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
                    cmd.CommandText =
                        "IF OBJECT_ID('dbo." + TableName + "') IS NOT NULL DROP TABLE dbo." + TableName + ";" +
                        "IF OBJECT_ID('dbo." + FunctionName + "') IS NOT NULL DROP FUNCTION dbo." + FunctionName + ";";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        // Marked separately from [Trait("Category","Integration")] above: this is the one test
        // in the suite that deliberately burns CPU time to provoke a real CommandTimeout
        // (bounded, ~2-5s after the redesign that replaced a 15M-row/50s+ approach -- see the
        // class doc comment -- but still the single most expensive test here). Run everything
        // except it with `dotnet test --filter "Speed!=Slow"`; it still runs by default with a
        // plain `dotnet test` since its cost is now small enough not to warrant exclusion.
        [Trait("Speed", "Slow")]
        [Fact]
        public async Task Timeout_InChunk1_HaltsPass1_LeavesChunk2Unattempted()
        {
            var profiler = new TableProfiler();
            var options = new ProfileOptions { QueryTimeoutSeconds = 2 };

            var sw = Stopwatch.StartNew();
            var profile = await profiler.ProfileAsync(
                TestDb.ConnectionString, Probe, options, progress: null, cancellationToken: CancellationToken.None);
            sw.Stop();

            Assert.NotNull(profile);

            // Bounded and fast: the abort happens close to the timeout, not after the ~50s+ the
            // chunk would otherwise take. A generous ceiling catches a regression to "waits for
            // the whole chunk" without being a tight, flaky bound.
            Assert.True(sw.Elapsed.TotalSeconds < 30,
                "Expected pass 1 to abort near the 2s timeout; took " + sw.Elapsed.TotalSeconds + "s -- " +
                "either the timeout didn't fire (unexpected on this machine per calibration) or pass 1 " +
                "didn't halt promptly.");

            // Pass-0 metadata survives regardless (Amendment 8's original guarantee).
            Assert.Equal(ChunkSize + Chunk2ColumnCount, profile.Columns.Count);
            Assert.Equal("DateCreated", profile.DateCreatedColumn);

            // Chunk 1 (contains SlowCol) failed with a timeout-specific SkipReason.
            var slowCol = profile.Columns.First(c => c.Meta.Name == "SlowCol");
            Assert.Null(slowCol.FilledCount);
            Assert.NotNull(slowCol.SkipReason);
            Assert.Contains("timed out", slowCol.SkipReason, StringComparison.OrdinalIgnoreCase);

            // Chunk 2 -- ordinary, fast int columns with no relationship to SlowCol -- must have
            // been left UNATTEMPTED, not merely failed: this is the behavioural difference from
            // Pass1ContinuesOnNonTimeoutErrorTests, where the equivalent chunk 2 succeeds.
            var lastFillerName = "Col" + (FillerColumnCount + 1).ToString("000");
            var lastFiller = profile.Columns.First(c => c.Meta.Name == lastFillerName);
            Assert.Null(lastFiller.FilledCount);

            // TotalRows == 0 here means "pass 1 never completed a chunk", not "the table is
            // empty" (it has RowCount rows) -- the two must stay distinguishable, and must not
            // trip the empty-table warning.
            Assert.Equal(0, profile.TotalRows);
            Assert.DoesNotContain(profile.Warnings, w => w.Contains("Table is empty"));

            // The warning must name the timeout value...
            Assert.Contains(profile.Warnings, w =>
                w.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 && w.Contains("2 s"));

            // ...and must identify chunk 2 as skipped/unattempted rather than silently dropping
            // it. Exact wording is Core's to choose; this checks for the chunk-2 column range
            // ("columns {ChunkSize+1}-{ChunkSize+Chunk2ColumnCount}", following the same
            // "columns X-Y" convention ProfileSqlBuilder already uses for Pass1Query.Detail) OR
            // a generic skip/not-attempted phrase, so the assertion doesn't over-fit one exact
            // sentence Agent A hasn't written yet.
            string chunk2Range = "columns " + (ChunkSize + 1) + "-" + (ChunkSize + Chunk2ColumnCount);
            Assert.Contains(profile.Warnings, w =>
                w.Contains(chunk2Range) ||
                w.IndexOf("skip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                w.IndexOf("not attempt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                w.IndexOf("unattempt", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
