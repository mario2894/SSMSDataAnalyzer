using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Sql
{
    /// <summary>Which aggregate an output column of a pass-1 query carries.</summary>
    public enum Pass1Aggregate { TotalRows, Filled, LastFill, Min, Max, Bytes, Blank }

    /// <summary>Maps one result-set ordinal back to a column + aggregate.</summary>
    public sealed class Pass1Slot
    {
        public string Alias { get; set; }
        public Pass1Aggregate Aggregate { get; set; }

        /// <summary>null for the TotalRows slot.</summary>
        public ColumnMeta Column { get; set; }
    }

    public sealed class Pass1Query
    {
        public string Sql { get; set; }
        public IList<Pass1Slot> Slots { get; set; }
        public IList<ColumnMeta> Columns { get; set; }

        /// <summary>e.g. "columns 1-60"</summary>
        public string Detail { get; set; }

        public Pass1Query()
        {
            Slots = new List<Pass1Slot>();
            Columns = new List<ColumnMeta>();
        }
    }

    /// <summary>
    /// Generates pass 1: one table scan per chunk producing COUNT_BIG(*), and per column
    /// COUNT_BIG(col), MAX(CASE WHEN col IS NOT NULL THEN [DateCreated] END), MIN, MAX,
    /// SUM(CAST(DATALENGTH(col) AS BIGINT)) and a blank count for string columns.
    /// Only aggregates the column's type actually supports are emitted.
    /// </summary>
    public static class ProfileSqlBuilder
    {
        /// <summary>
        /// Columns per pass-1 query. Each column emits up to 6 expressions, so 60 columns is
        /// ~360 expressions — comfortably under SQL Server's 1024-expression select-list limit
        /// while keeping wide tables to a handful of scans.
        /// </summary>
        public const int Pass1ChunkSize = 60;

        public const string ReadUncommittedPrefix = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;";

        // TABLESAMPLE is one of the few clauses where T-SQL rejects a variable
        // ("Variables are not allowed in the TABLESAMPLE or REPEATABLE clauses", error 497),
        // so the percentage is emitted as a range-checked, invariant-formatted numeric literal
        // via SqlIdentifier.Percent — see the "wherever the syntax allows" carve-out in rule 2.

        public static IList<Pass1Query> BuildPass1(
            TableRef table,
            IList<ColumnMeta> columns,
            string dateCreatedColumn,
            ProfileOptions options)
        {
            if (table == null) throw new ArgumentNullException("table");
            if (columns == null) throw new ArgumentNullException("columns");
            options = options ?? new ProfileOptions();

            var queries = new List<Pass1Query>();
            int index = 0;

            // A table with no profilable columns still needs its row count.
            do
            {
                var chunk = new List<ColumnMeta>();
                while (index < columns.Count && chunk.Count < Pass1ChunkSize)
                    chunk.Add(columns[index++]);

                queries.Add(BuildPass1Chunk(table, chunk, dateCreatedColumn, options,
                    queries.Count * Pass1ChunkSize + 1));
            }
            while (index < columns.Count);

            return queries;
        }

        private static Pass1Query BuildPass1Chunk(
            TableRef table,
            IList<ColumnMeta> chunk,
            string dateCreatedColumn,
            ProfileOptions options,
            int firstOrdinal)
        {
            var query = new Pass1Query();
            var sb = new StringBuilder();

            sb.AppendLine(ReadUncommittedPrefix);
            sb.AppendLine("SELECT");

            var parts = new List<string>();
            parts.Add("    COUNT_BIG(*) AS [_TotalRows]");
            query.Slots.Add(new Pass1Slot { Alias = "_TotalRows", Aggregate = Pass1Aggregate.TotalRows });

            string dateCol = string.IsNullOrEmpty(dateCreatedColumn) ? null : SqlIdentifier.Bracket(dateCreatedColumn);

            for (int i = 0; i < chunk.Count; i++)
            {
                var meta = chunk[i];
                var col = SqlIdentifier.Bracket(meta.Name);
                // Aliases are ordinal-based: a 128-char column name plus a suffix would overflow
                // the identifier limit, and ordinals keep result-set mapping unambiguous.
                string prefix = "c" + (firstOrdinal + i).ToString(CultureInfo.InvariantCulture);

                if (!meta.SupportsCount)
                    continue;   // MetadataOnly: emit nothing at all for this column.

                Add(parts, query, prefix + "_filled", Pass1Aggregate.Filled, meta,
                    "COUNT_BIG(" + col + ")");

                if (dateCol != null)
                {
                    Add(parts, query, prefix + "_lastfill", Pass1Aggregate.LastFill, meta,
                        "MAX(CASE WHEN " + col + " IS NOT NULL THEN " + dateCol + " END)");
                }

                if (meta.SupportsMinMax)
                {
                    Add(parts, query, prefix + "_min", Pass1Aggregate.Min, meta, "MIN(" + col + ")");
                    Add(parts, query, prefix + "_max", Pass1Aggregate.Max, meta, "MAX(" + col + ")");
                }

                if (meta.SupportsDataLength)
                {
                    Add(parts, query, prefix + "_bytes", Pass1Aggregate.Bytes, meta,
                        "SUM(CAST(DATALENGTH(" + col + ") AS BIGINT))");
                }

                if (meta.IsStringType && meta.Support != AggregateSupport.MetadataOnly)
                {
                    Add(parts, query, prefix + "_blank", Pass1Aggregate.Blank, meta,
                        "SUM(CASE WHEN LTRIM(RTRIM(" + col + ")) = '' THEN CAST(1 AS BIGINT) ELSE CAST(0 AS BIGINT) END)");
                }

                query.Columns.Add(meta);
            }

            sb.AppendLine(string.Join("," + Environment.NewLine, parts.ToArray()));
            sb.Append("FROM ").Append(table.QualifiedName);

            if (options.SamplePercent.HasValue)
                sb.Append(" TABLESAMPLE SYSTEM (")
                  .Append(SqlIdentifier.Percent(options.SamplePercent.Value, "SamplePercent"))
                  .Append(" PERCENT)");

            string hints = QueryHints(options, includeMaxGrant: false);
            if (hints != null) sb.AppendLine().Append(hints);
            sb.Append(';');

            query.Sql = sb.ToString();
            query.Detail = chunk.Count == 0
                ? "row count"
                : string.Format(CultureInfo.InvariantCulture, "columns {0}-{1}", firstOrdinal, firstOrdinal + chunk.Count - 1);
            return query;
        }

        private static void Add(List<string> parts, Pass1Query query, string alias,
            Pass1Aggregate aggregate, ColumnMeta meta, string expression)
        {
            parts.Add("    " + expression + " AS " + SqlIdentifier.Bracket(alias));
            query.Slots.Add(new Pass1Slot { Alias = alias, Aggregate = aggregate, Column = meta });
        }

        /// <summary>
        /// Builds the OPTION (...) clause. Hint values cannot be parameters, so they are
        /// range-validated integers formatted invariantly — never free-form user text.
        /// </summary>
        internal static string QueryHints(ProfileOptions options, bool includeMaxGrant)
        {
            var hints = new List<string>();

            if (includeMaxGrant)
                hints.Add("MAX_GRANT_PERCENT = " + SqlIdentifier.Int(options.MaxGrantPercent, 1, 100, "MaxGrantPercent"));

            if (options.MaxDop.HasValue)
                hints.Add("MAXDOP " + SqlIdentifier.Int(options.MaxDop.Value, 0, 32767, "MaxDop"));

            if (hints.Count == 0) return null;
            return "OPTION (" + string.Join(", ", hints.ToArray()) + ")";
        }
    }
}
