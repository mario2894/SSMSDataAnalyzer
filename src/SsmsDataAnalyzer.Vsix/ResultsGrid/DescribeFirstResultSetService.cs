using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>One row from sys.dm_exec_describe_first_result_set (unfiltered — see the
    /// class doc comment on why <c>is_hidden</c> can't be filtered in the SQL itself).</summary>
    internal sealed class DescribedColumn
    {
        public int Ordinal { get; set; }
        public string Name { get; set; }
        public string SourceDatabase { get; set; }
        public string SourceSchema { get; set; }
        public string SourceTable { get; set; }
        public string SourceColumn { get; set; }
        public int? ErrorNumber { get; set; }
        public bool? IsHidden { get; set; }
        /// <summary>v0.8.0: this column's own SQL Server type name (e.g. "int", "varchar",
        /// "float") as the DM reports it -- needed now that the results grid only gives us
        /// DISPLAY TEXT (IGridStorage.GetCellDataAsString), not a typed value, so
        /// SqlLiteralFormatter.TryFormatDisplayText needs to know what it's parsing.</summary>
        public string SystemTypeName { get; set; }
        /// <summary>Declared max_length in bytes, -1 for a MAX/LOB type. Used to decline
        /// converting a possibly-truncated-for-display string back to a literal.</summary>
        public int MaxLength { get; set; }
    }

    /// <summary>
    /// CONTRACT.md Amendment 16 / docs/resultsgrid-api.md section 6.2: the only route that
    /// resolves a result column back to a base table/column, since SSMS executes with
    /// CommandBehavior.Default (never KeyInfo) and the grid retains nothing usable.
    ///
    /// The trailing "1" (browse information) on the DM call is LOAD-BEARING — with 0 every
    /// source_* column comes back NULL and the feature looks impossible (verified live by
    /// Agent C's spike). Compiles but does not execute the batch; errors come back as ROWS
    /// (error_number), never as a thrown exception — callers must check for them explicitly.
    ///
    /// Verified live against this project's own fixtures (not just the doc's examples): an
    /// error row's <c>is_hidden</c> comes back **NULL**, not 0 or 1 — e.g. for
    /// "SELECT Id INTO #t FROM dbo.FkChild; SELECT Id FROM #t" the DM returns exactly one row,
    /// error_number = 11525, is_hidden = NULL. A `WHERE is_hidden = 0` filter in the SQL text
    /// (as the doc's own §6.2 sketch has it) silently excludes that row under three-valued
    /// logic, which would make gate 3 (error-row check) never fire and fall through to gate 4
    /// (count mismatch) instead — same eventual decline, but via a misleading status message,
    /// and a fragile coincidence rather than the actual check working. So this fetches every
    /// row unfiltered; callers must check <see cref="DescribedColumn.ErrorNumber"/> BEFORE
    /// filtering on <see cref="DescribedColumn.IsHidden"/> for the ordinal/count gates.
    /// </summary>
    internal static class DescribeFirstResultSetService
    {
        private const string DescribeSql = @"
SELECT column_ordinal, name, source_database, source_schema, source_table, source_column, error_number, is_hidden, system_type_name, max_length
FROM sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)
ORDER BY column_ordinal;";

        public static async Task<List<DescribedColumn>> DescribeAsync(
            SqlConnection connection, string tsql, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var result = new List<DescribedColumn>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DescribeSql;
                cmd.CommandTimeout = timeoutSeconds;
                cmd.Parameters.Add("@tsql", SqlDbType.NVarChar, -1).Value = tsql ?? string.Empty;

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(true))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
                    {
                        result.Add(new DescribedColumn
                        {
                            Ordinal = reader.GetInt32(0),
                            Name = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(1),
                            SourceDatabase = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(2),
                            SourceSchema = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(3),
                            SourceTable = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(4),
                            SourceColumn = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(5),
                            ErrorNumber = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(true) ? (int?)null : reader.GetInt32(6),
                            IsHidden = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(true) ? (bool?)null : Convert.ToBoolean(reader.GetValue(7)),
                            // system_type_name comes back like "varchar(50)" or "decimal(18,2)" for
                            // some types -- strip any parenthesized part, callers only need the bare
                            // type name to decide how to parse display text.
                            SystemTypeName = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(true) ? null : StripTypeArgs(reader.GetString(8)),
                            MaxLength = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(true) ? 0 : reader.GetInt16(9)
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>system_type_name can come back as "varchar(50)", "decimal(18, 2)", etc. --
        /// this strips the parenthesized part so callers can match on the bare type name.</summary>
        private static string StripTypeArgs(string systemTypeName)
        {
            if (string.IsNullOrEmpty(systemTypeName)) return systemTypeName;
            int paren = systemTypeName.IndexOf('(');
            return paren < 0 ? systemTypeName : systemTypeName.Substring(0, paren).TrimEnd();
        }
    }
}
