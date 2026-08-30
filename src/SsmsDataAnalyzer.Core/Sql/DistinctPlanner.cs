using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Sql
{
    public enum DistinctQueryKind
    {
        /// <summary>2a — leading index key: a narrow index scan, not a table scan.</summary>
        IndexBacked,

        /// <summary>2b — ordinary columns, batched, memory-grant capped.</summary>
        Batched,

        /// <summary>2c — LOB / wide string, a batch of one, run last so it can be cancelled.</summary>
        Lob
    }

    public sealed class DistinctQuery
    {
        public DistinctQueryKind Kind { get; set; }
        public IList<ColumnMeta> Columns { get; set; }
        public string Sql { get; set; }

        /// <summary>Index used for the IndexBacked fast path, else null.</summary>
        public string IndexName { get; set; }

        /// <summary>Human text for the progress line and the cost preview.</summary>
        public string Detail { get; set; }

        /// <summary>How many passes over the table (or an index) this query costs. Always 1 today.</summary>
        public int ScanCost { get { return 1; } }

        public DistinctQuery() { Columns = new List<ColumnMeta>(); }
    }

    /// <summary>
    /// The ordered plan for pass 2, produced before anything executes so the cost can be
    /// previewed ("k scans over n rows") and columns deselected.
    /// </summary>
    public sealed class DistinctPlan
    {
        public IList<DistinctQuery> Queries { get; set; }

        /// <summary>Columns that will get no distinct count, with the reason.</summary>
        public IDictionary<string, string> Skipped { get; set; }

        public DistinctPlan()
        {
            Queries = new List<DistinctQuery>();
            Skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public int TotalQueries { get { return Queries.Count; } }
    }

    /// <summary>
    /// Splits distinct counting into index-backed singles, memory-capped batches, and
    /// isolated LOB queries placed last. Exact counts only — this codebase never approximates.
    /// </summary>
    public static class DistinctPlanner
    {
        /// <summary>
        /// String columns at or above this declared length are treated as "wide" and given their
        /// own batch of one, alongside true LOB columns. Distinct over long strings is the most
        /// expensive sort/hash in the whole profile.
        /// </summary>
        public const int WideStringChars = 400;

        public static DistinctPlan Plan(TableRef table, IList<ColumnMeta> columns, ProfileOptions options)
        {
            if (table == null) throw new ArgumentNullException("table");
            if (columns == null) throw new ArgumentNullException("columns");
            options = options ?? new ProfileOptions();
            options.Validate();

            var plan = new DistinctPlan();

            if (options.SamplePercent.HasValue)
            {
                // Sampled cardinality cannot be extrapolated; refuse rather than guess.
                foreach (var c in columns)
                    plan.Skipped[c.Name] = ProfileOptions.SampledDistinctSkipReason;
                return plan;
            }

            if (!options.IncludeDistinct)
            {
                foreach (var c in columns)
                    plan.Skipped[c.Name] = "Distinct counts were not requested";
                return plan;
            }

            var indexBacked = new List<ColumnMeta>();
            var batchable = new List<ColumnMeta>();
            var wide = new List<ColumnMeta>();

            foreach (var c in columns)
            {
                if (!c.SupportsDistinct)
                {
                    plan.Skipped[c.Name] = string.Format(CultureInfo.InvariantCulture,
                        "Type '{0}' does not support the DISTINCT operator", c.TypeName);
                    continue;
                }

                if (IsWide(c)) wide.Add(c);
                else if (!string.IsNullOrEmpty(c.LeadingIndexName)) indexBacked.Add(c);
                else batchable.Add(c);
            }

            // 2a — cheapest first.
            foreach (var c in indexBacked)
                plan.Queries.Add(BuildIndexBacked(table, c, options));

            // 2b — batched, memory-grant capped.
            for (int i = 0; i < batchable.Count; i += options.DistinctBatchSize)
            {
                var batch = new List<ColumnMeta>();
                for (int j = i; j < batchable.Count && j < i + options.DistinctBatchSize; j++)
                    batch.Add(batchable[j]);
                plan.Queries.Add(BuildBatched(table, batch, options, DistinctQueryKind.Batched));
            }

            // 2c — LOB / wide strings, one per query, LAST so they can be cancelled unpaid-for.
            foreach (var c in wide)
                plan.Queries.Add(BuildBatched(table, new List<ColumnMeta> { c }, options, DistinctQueryKind.Lob));

            return plan;
        }

        public static bool IsWide(ColumnMeta c)
        {
            if (c.IsLob) return true;
            if (c.IsStringType && c.CharLength >= WideStringChars) return true;
            if (string.Equals(c.TypeName, "varbinary", StringComparison.OrdinalIgnoreCase)
                && (c.MaxLength == -1 || c.MaxLength >= WideStringChars)) return true;
            return false;
        }

        private static DistinctQuery BuildIndexBacked(TableRef table, ColumnMeta column, ProfileOptions options)
        {
            string col = SqlIdentifier.Bracket(column.Name);
            var sb = new StringBuilder();

            sb.AppendLine(ProfileSqlBuilder.ReadUncommittedPrefix);
            sb.AppendLine("SELECT COUNT_BIG(*) AS [c0_distinct]");
            sb.AppendLine("FROM (");
            sb.Append("    SELECT DISTINCT ").Append(col).AppendLine();
            sb.Append("    FROM ").Append(table.QualifiedName)
              .Append(" WITH (INDEX(").Append(SqlIdentifier.Bracket(column.LeadingIndexName)).AppendLine("))");
            // COUNT(DISTINCT col) ignores NULLs; match that exactly.
            sb.Append("    WHERE ").Append(col).AppendLine(" IS NOT NULL");
            sb.Append(") d");

            string hints = ProfileSqlBuilder.QueryHints(options, includeMaxGrant: false);
            if (hints != null) sb.AppendLine().Append(hints);
            sb.Append(';');

            var q = new DistinctQuery
            {
                Kind = DistinctQueryKind.IndexBacked,
                Sql = sb.ToString(),
                IndexName = column.LeadingIndexName,
                Detail = string.Format(CultureInfo.InvariantCulture,
                    "distinct {0} via index {1}", column.Name, column.LeadingIndexName)
            };
            q.Columns.Add(column);
            return q;
        }

        private static DistinctQuery BuildBatched(TableRef table, IList<ColumnMeta> batch,
            ProfileOptions options, DistinctQueryKind kind)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ProfileSqlBuilder.ReadUncommittedPrefix);
            sb.AppendLine("SELECT");

            var parts = new List<string>();
            for (int i = 0; i < batch.Count; i++)
            {
                parts.Add("    COUNT_BIG(DISTINCT " + SqlIdentifier.Bracket(batch[i].Name) + ") AS "
                    + SqlIdentifier.Bracket("c" + i.ToString(CultureInfo.InvariantCulture) + "_distinct"));
            }

            sb.AppendLine(string.Join("," + Environment.NewLine, parts.ToArray()));
            sb.Append("FROM ").Append(table.QualifiedName);

            // Rule 4: batched distinct queries always cap their memory grant.
            string hints = ProfileSqlBuilder.QueryHints(options, includeMaxGrant: true);
            if (hints != null) sb.AppendLine().Append(hints);
            sb.Append(';');

            var names = new List<string>();
            foreach (var c in batch) names.Add(c.Name);

            var q = new DistinctQuery
            {
                Kind = kind,
                Sql = sb.ToString(),
                Detail = (kind == DistinctQueryKind.Lob ? "distinct (LOB) " : "distinct ")
                         + string.Join(", ", names.ToArray())
            };
            foreach (var c in batch) q.Columns.Add(c);
            return q;
        }
    }
}
