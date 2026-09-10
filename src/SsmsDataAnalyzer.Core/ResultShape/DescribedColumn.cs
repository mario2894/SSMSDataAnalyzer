namespace SsmsDataAnalyzer.Core.ResultShape
{
    /// <summary>One row from sys.dm_exec_describe_first_result_set (unfiltered — see
    /// SsmsDataAnalyzer.Vsix.ResultsGrid.DescribeFirstResultSetService's doc comment on why
    /// <c>is_hidden</c> can't be filtered in the SQL itself). Moved here from the Vsix project
    /// (docs/pivot-plan.md item 13) so the pure shape-match / agreement logic that consumes it
    /// can be unit-tested; the shape is unchanged.</summary>
    public sealed class DescribedColumn
    {
        public int Ordinal { get; set; }
        public string Name { get; set; }
        public string SourceDatabase { get; set; }
        public string SourceSchema { get; set; }
        public string SourceTable { get; set; }
        public string SourceColumn { get; set; }
        public int? ErrorNumber { get; set; }
        /// <summary>SQL Server's own error text for an error row. Surfaced in the decline
        /// message (status bar only — it can quote the query text, so it is never logged): a
        /// field report on a 191-column SELECT * said only "1 errored", which couldn't be
        /// reproduced locally or diagnosed without the actual error.</summary>
        public string ErrorMessage { get; set; }
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
}
