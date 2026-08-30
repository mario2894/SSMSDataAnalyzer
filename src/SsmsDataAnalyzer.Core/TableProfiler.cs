using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.Metadata;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Core
{
    /// <summary>
    /// Orchestrates pass 0 (metadata) -&gt; pass 1 (one scan for everything but distinct)
    /// -&gt; pass 2 (planned distinct batches). Progress carries a usable partial snapshot after
    /// pass 1 and after every distinct batch. Cancellation returns the work already done.
    /// </summary>
    public sealed class TableProfiler : ITableProfiler
    {
        /// <summary>SQL Server error numbers for "SELECT permission denied".</summary>
        private static readonly int[] PermissionErrors = { 229, 230 };

        public async Task<TableProfile> ProfileAsync(
            string connectionString,
            TableRef table,
            ProfileOptions options,
            IProgress<ProfileProgress> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", "connectionString");
            if (table == null) throw new ArgumentNullException("table");

            options = options ?? new ProfileOptions();
            options.Validate();

            var stopwatch = Stopwatch.StartNew();
            var profile = new TableProfile
            {
                Table = table,
                WasSampled = options.SamplePercent.HasValue
            };

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                    // ---- Pass 0 -------------------------------------------------------------
                    var schema = await new SchemaReader()
                        .ReadAsync(connection, table, options, cancellationToken).ConfigureAwait(false);

                    var columns = SelectColumns(schema.Columns, options, profile);
                    profile.EstimatedRows = schema.EstimatedRows;
                    profile.DateCreatedColumn = schema.DateCreatedColumn;

                    foreach (var meta in columns)
                    {
                        var cp = new ColumnProfile { Meta = meta };
                        if (meta.Support == AggregateSupport.MetadataOnly)
                            cp.SkipReason = string.Format(CultureInfo.InvariantCulture,
                                "Type '{0}' does not support aggregation; metadata only", meta.TypeName);
                        profile.Columns.Add(cp);
                    }

                    if (profile.DateCreatedColumn == null)
                    {
                        profile.Warnings.Add("No DateCreated-style column found (searched: "
                            + string.Join(", ", ToArray(options.DateCreatedCandidates)) + "). Last-fill dates are unavailable.");
                    }

                    if (schema.EstimatedRows > options.LargeTableThreshold)
                    {
                        profile.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                            "Large table: ~{0:N0} rows exceeds the {1:N0}-row threshold.",
                            schema.EstimatedRows, options.LargeTableThreshold));
                    }

                    if (options.SamplePercent.HasValue)
                    {
                        profile.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                            "Sampled at {0}% (TABLESAMPLE SYSTEM). Counts are estimates; distinct counts are suppressed.",
                            options.SamplePercent.Value));
                    }

                    Report(progress, "metadata", 1, 1, "schema read", profile, stopwatch);
                    cancellationToken.ThrowIfCancellationRequested();

                    // ---- Pass 1 -------------------------------------------------------------
                    var pass1 = ProfileSqlBuilder.BuildPass1(table, columns, profile.DateCreatedColumn, options);
                    var byColumn = IndexByColumn(profile);

                    // Amendment 8: a pass-1 chunk that fails without cancellation is a third
                    // outcome — neither success nor cancellation — and must not discard pass 0.
                    bool anyPass1Succeeded = false;
                    var pass1Failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < pass1.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Pass1Outcome outcome = await RunPass1Async(connection, pass1[i], byColumn,
                            profile, options, pass1Failed, cancellationToken).ConfigureAwait(false);

                        if (outcome == Pass1Outcome.Succeeded) anyPass1Succeeded = true;

                        StampTotalRows(profile);

                        // Only meaningful once a pass-1 query has actually returned a row count.
                        // After a failure TotalRows is still 0, which must not be mistaken for
                        // "the table is empty".
                        if (anyPass1Succeeded) NoteEmptyTable(profile);

                        // Amendment 10: a timeout describes the table, not this chunk's columns,
                        // so every later chunk would pay the full budget to learn the same fact.
                        // Any other SqlException is column-specific — later chunks carry
                        // different columns and are genuinely likely to succeed, so continue.
                        bool halt = outcome == Pass1Outcome.TimedOut;
                        if (halt)
                            AbandonRemainingPass1Chunks(pass1, i + 1, byColumn, profile, pass1Failed, options);

                        Report(progress, "pass1",
                            halt ? pass1.Count : i + 1, pass1.Count,
                            halt ? "stopped after timeout" : pass1[i].Detail,
                            profile, stopwatch);

                        if (halt) break;
                    }

                    StampTotalRows(profile);

                    // TotalRows is a non-nullable long, so a failed pass 1 leaves it at 0 —
                    // indistinguishable from a genuinely empty table. Say so explicitly rather
                    // than let a reader take the zero at face value.
                    if (!anyPass1Succeeded && pass1.Count > 0)
                    {
                        profile.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                            "Row count unavailable — no pass-1 query completed. The reported total of 0 is "
                            + "not a measurement; the pass-0 estimate is ~{0:N0} rows.",
                            profile.EstimatedRows));
                    }

                    // ---- Pass 2 -------------------------------------------------------------
                    var distinctCandidates = new List<ColumnMeta>();
                    foreach (var meta in columns)
                        if (meta.Support != AggregateSupport.MetadataOnly) distinctCandidates.Add(meta);

                    var plan = DistinctPlanner.Plan(table, distinctCandidates, options);

                    foreach (var pair in plan.Skipped)
                    {
                        ColumnProfile cp;
                        if (byColumn.TryGetValue(pair.Key, out cp) && cp.SkipReason == null)
                            cp.SkipReason = pair.Value;
                    }

                    if (plan.Queries.Count > 0)
                    {
                        for (int i = 0; i < plan.Queries.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await RunDistinctAsync(connection, plan.Queries[i], byColumn, profile, options, cancellationToken).ConfigureAwait(false);
                            StampTotalRows(profile);
                            // A usable snapshot after EVERY batch — this is what makes the grid
                            // fill in progressively instead of all at once at the end.
                            Report(progress, "distinct", i + 1, plan.Queries.Count, plan.Queries[i].Detail, profile, stopwatch);
                        }
                    }

                    // A successful distinct batch clears SkipReason, which would erase the
                    // pass-1 failure note for a column that got its distinct count anyway.
                    // Restore it: DistinctCount is real, but the pass-1 aggregates are still gone.
                    RestorePass1FailureReasons(byColumn, pass1Failed);
                }
            }
            catch (OperationCanceledException)
            {
                profile.Warnings.Add(CancelledWarning);
            }
            catch (SqlException ex) when (IsCancellation(ex, cancellationToken))
            {
                profile.Warnings.Add(CancelledWarning);
            }
            catch (SqlException ex) when (Array.IndexOf(PermissionErrors, ex.Number) >= 0)
            {
                throw new UnauthorizedAccessException(
                    "SELECT permission denied on " + table.QualifiedName + ". " + ex.Message, ex);
            }

            StampTotalRows(profile);
            stopwatch.Stop();
            profile.Elapsed = stopwatch.Elapsed;
            return profile;
        }

        /// <summary>
        /// A cancelled SqlCommand surfaces as SqlException 0 / "Operation cancelled by user",
        /// not OperationCanceledException. Treat it as cancellation so partial work survives.
        /// </summary>
        private static bool IsCancellation(SqlException ex, CancellationToken token)
        {
            return token.IsCancellationRequested;
        }

        /// <summary>SQL Server's "Execution Timeout Expired" — a client-side timeout, not a server error.</summary>
        private const int TimeoutErrorNumber = -2;

        /// <summary>
        /// Why a profile is partial. Three outcomes must stay distinguishable in the output:
        /// cancelled by the user, pass 1 failed, or complete (no warning at all).
        /// </summary>
        internal const string CancelledWarning = "Cancelled — the results below are partial.";

        private static bool IsPermissionError(SqlException ex)
        {
            return Array.IndexOf(PermissionErrors, ex.Number) >= 0;
        }

        /// <summary>
        /// Describes a pass-1 failure for a column's SkipReason — deliberately distinct wording
        /// from the cancellation warning so the two can never be confused.
        /// </summary>
        private static string Pass1SkipReason(SqlException ex, ProfileOptions options)
        {
            return ex.Number == TimeoutErrorNumber
                ? string.Format(CultureInfo.InvariantCulture,
                    "Pass 1 timed out after {0} s; aggregates unavailable (metadata is still accurate)",
                    options.QueryTimeoutSeconds)
                : "Pass 1 failed; aggregates unavailable (metadata is still accurate)";
        }

        private static string Pass1Warning(Pass1Query query, SqlException ex, ProfileOptions options)
        {
            string cause = ex.Number == TimeoutErrorNumber
                ? string.Format(CultureInfo.InvariantCulture,
                    "timed out after {0} s (CommandTimeout)", options.QueryTimeoutSeconds)
                : string.Format(CultureInfo.InvariantCulture, "failed (SQL error {0})", ex.Number);

            return string.Format(CultureInfo.InvariantCulture,
                "Pass 1 {0} for {1} — {2} Column metadata, types, nullability, PK and index "
                + "information are preserved; fill counts, min/max, last-fill dates and average "
                + "lengths are unavailable for those columns. Consider raising the timeout, "
                + "profiling fewer columns, or sampling.",
                cause, query.Detail, ex.Message.Split('\n')[0].Trim());
        }

        /// <summary>
        /// Re-applies pass-1 failure notes that a later successful distinct batch cleared.
        /// </summary>
        private static void RestorePass1FailureReasons(
            Dictionary<string, ColumnProfile> byColumn, Dictionary<string, string> pass1Failed)
        {
            foreach (var pair in pass1Failed)
            {
                ColumnProfile cp;
                if (byColumn.TryGetValue(pair.Key, out cp) && cp.SkipReason == null)
                    cp.SkipReason = pair.Value;
            }
        }

        private static IList<ColumnMeta> SelectColumns(IList<ColumnMeta> all, ProfileOptions options, TableProfile profile)
        {
            if (options.IncludedColumns == null || options.IncludedColumns.Count == 0)
                return all;

            var wanted = new HashSet<string>(options.IncludedColumns, StringComparer.OrdinalIgnoreCase);
            var kept = new List<ColumnMeta>();
            foreach (var c in all)
                if (wanted.Contains(c.Name)) kept.Add(c);

            if (kept.Count == 0)
                profile.Warnings.Add("IncludedColumns matched no columns; nothing was profiled.");

            return kept;
        }

        private static Dictionary<string, ColumnProfile> IndexByColumn(TableProfile profile)
        {
            var map = new Dictionary<string, ColumnProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var cp in profile.Columns) map[cp.Meta.Name] = cp;
            return map;
        }

        private static void StampTotalRows(TableProfile profile)
        {
            foreach (var cp in profile.Columns) cp.TotalRowsContext = profile.TotalRows;
        }

        /// <summary>CONTRACT Amendment 2 — the exact wording is contractual. Added at most once.</summary>
        internal const string EmptyTableWarning = "Table is empty — per-column flags are not meaningful.";

        /// <summary>
        /// Called only after a pass-1 query has actually returned, so a row count of zero means
        /// "the table is empty", never "we have not counted yet".
        /// </summary>
        private static void NoteEmptyTable(TableProfile profile)
        {
            if (profile.TotalRows != 0) return;
            if (profile.Warnings.Contains(EmptyTableWarning)) return;
            profile.Warnings.Add(EmptyTableWarning);
        }

        /// <summary>
        /// How one pass-1 chunk ended. TimedOut is separated from Failed because the two call
        /// for opposite responses — see CONTRACT Amendment 10.
        /// </summary>
        private enum Pass1Outcome { Succeeded, Failed, TimedOut }

        /// <summary>
        /// Runs one pass-1 chunk. A failure without cancellation is recorded on the affected
        /// columns and as a warning, and the profile built so far survives (Amendment 8).
        /// </summary>
        private async Task<Pass1Outcome> RunPass1Async(
            SqlConnection connection,
            Pass1Query query,
            Dictionary<string, ColumnProfile> byColumn,
            TableProfile profile,
            ProfileOptions options,
            Dictionary<string, string> pass1Failed,
            CancellationToken cancellationToken)
        {
            try
            {
                bool read = await RunPass1CoreAsync(connection, query, byColumn, profile, options, cancellationToken)
                    .ConfigureAwait(false);
                return read ? Pass1Outcome.Succeeded : Pass1Outcome.Failed;
            }
            catch (SqlException ex) when (!cancellationToken.IsCancellationRequested && !IsPermissionError(ex))
            {
                // Not cancellation (that path keeps its own warning) and not a permission
                // problem (that still surfaces as UnauthorizedAccessException). A timeout or
                // transient server error must cost only this chunk, never the whole profile.
                string reason = Pass1SkipReason(ex, options);

                foreach (var meta in query.Columns)
                {
                    pass1Failed[meta.Name] = reason;

                    ColumnProfile cp;
                    if (byColumn.TryGetValue(meta.Name, out cp) && cp.SkipReason == null)
                        cp.SkipReason = reason;
                }

                profile.Warnings.Add(Pass1Warning(query, ex, options));
                return ex.Number == TimeoutErrorNumber ? Pass1Outcome.TimedOut : Pass1Outcome.Failed;
            }
        }

        /// <summary>
        /// Amendment 10: after a pass-1 timeout the remaining chunks are deliberately not run.
        /// Mark their columns and say so explicitly, so the profile reads as partial *by choice*
        /// rather than partial by accident, and name the timeout the user would need to raise.
        /// </summary>
        private static void AbandonRemainingPass1Chunks(
            IList<Pass1Query> pass1,
            int firstUnattempted,
            Dictionary<string, ColumnProfile> byColumn,
            TableProfile profile,
            Dictionary<string, string> pass1Failed,
            ProfileOptions options)
        {
            if (firstUnattempted >= pass1.Count) return;   // the timeout hit the last chunk

            const string reason = "Pass 1 was not attempted — stopped after an earlier chunk timed out";
            var details = new List<string>();

            for (int i = firstUnattempted; i < pass1.Count; i++)
            {
                details.Add(pass1[i].Detail);

                foreach (var meta in pass1[i].Columns)
                {
                    pass1Failed[meta.Name] = reason;

                    ColumnProfile cp;
                    if (byColumn.TryGetValue(meta.Name, out cp) && cp.SkipReason == null)
                        cp.SkipReason = reason;
                }
            }

            profile.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                "Pass 1 stopped after the timeout: {0} of {1} chunks were not attempted ({2}). "
                + "A timeout reflects the table's size, width and the server's current load rather "
                + "than these particular columns, so continuing would very likely time out again "
                + "and cost {3} s per remaining chunk. Re-run with a larger QueryTimeoutSeconds "
                + "(currently {3} s) to profile them.",
                details.Count, pass1.Count, string.Join("; ", details.ToArray()),
                options.QueryTimeoutSeconds));
        }

        private async Task<bool> RunPass1CoreAsync(
            SqlConnection connection,
            Pass1Query query,
            Dictionary<string, ColumnProfile> byColumn,
            TableProfile profile,
            ProfileOptions options,
            CancellationToken cancellationToken)
        {
            using (var pc = SqlCommandFactory.Create(connection, query.Sql, options, cancellationToken))
            {
                using (var reader = await pc.Cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    // An aggregate query always yields one row; no row means no usable result,
                    // and in particular no row count, so report it as not-succeeded.
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;

                    for (int ordinal = 0; ordinal < query.Slots.Count; ordinal++)
                    {
                        var slot = query.Slots[ordinal];
                        object value = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

                        if (slot.Aggregate == Pass1Aggregate.TotalRows)
                        {
                            profile.TotalRows = value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                            continue;
                        }

                        ColumnProfile cp;
                        if (!byColumn.TryGetValue(slot.Column.Name, out cp)) continue;

                        switch (slot.Aggregate)
                        {
                            case Pass1Aggregate.Filled:
                                cp.FilledCount = value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                                break;
                            case Pass1Aggregate.LastFill:
                                cp.LastFillDate = ToDateTime(value);
                                break;
                            case Pass1Aggregate.Min:
                                cp.MinValue = value;
                                break;
                            case Pass1Aggregate.Max:
                                cp.MaxValue = value;
                                break;
                            case Pass1Aggregate.Bytes:
                                if (value != null && cp.FilledCount.HasValue && cp.FilledCount.Value > 0)
                                    cp.AvgByteLength = Convert.ToDouble(value, CultureInfo.InvariantCulture) / cp.FilledCount.Value;
                                break;
                            case Pass1Aggregate.Blank:
                                cp.BlankCount = value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                                break;
                        }
                    }
                }
            }

            return true;
        }

        private async Task RunDistinctAsync(
            SqlConnection connection,
            DistinctQuery query,
            Dictionary<string, ColumnProfile> byColumn,
            TableProfile profile,
            ProfileOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var pc = SqlCommandFactory.Create(connection, query.Sql, options, cancellationToken))
                using (var reader = await pc.Cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;

                    for (int i = 0; i < query.Columns.Count && i < reader.FieldCount; i++)
                    {
                        ColumnProfile cp;
                        if (!byColumn.TryGetValue(query.Columns[i].Name, out cp)) continue;
                        cp.DistinctCount = reader.IsDBNull(i)
                            ? 0L
                            : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
                        cp.SkipReason = null;
                    }
                }
            }
            catch (SqlException ex) when (!cancellationToken.IsCancellationRequested && !IsPermissionError(ex))
            {
                // One bad batch must not abandon the rest of the plan. A permission error is
                // excluded so it still reaches the UnauthorizedAccessException path, matching
                // pass 1 — "SELECT denied" is a clear message, not a per-column footnote.
                var names = new List<string>();
                foreach (var c in query.Columns)
                {
                    names.Add(c.Name);
                    ColumnProfile cp;
                    if (byColumn.TryGetValue(c.Name, out cp) && cp.SkipReason == null)
                        cp.SkipReason = "Distinct query failed: " + ex.Message;
                }
                profile.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Distinct batch [{0}] failed: {1}", string.Join(", ", names.ToArray()), ex.Message));
            }
        }

        private static DateTime? ToDateTime(object value)
        {
            if (value == null) return null;
            if (value is DateTime) return (DateTime)value;
            if (value is DateTimeOffset) return ((DateTimeOffset)value).UtcDateTime;
            return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private static void Report(IProgress<ProfileProgress> progress, string stage, int completed,
            int total, string detail, TableProfile profile, Stopwatch stopwatch)
        {
            if (progress == null) return;
            profile.Elapsed = stopwatch.Elapsed;
            StampTotalRows(profile);
            progress.Report(new ProfileProgress
            {
                Stage = stage,
                CompletedUnits = completed,
                TotalUnits = total,
                CurrentDetail = detail,
                Snapshot = profile.SnapshotCopy()
            });
        }

        private static string[] ToArray(IList<string> items)
        {
            if (items == null) return new string[0];
            var array = new string[items.Count];
            items.CopyTo(array, 0);
            return array;
        }
    }
}
