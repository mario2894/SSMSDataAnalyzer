using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Vsix.Options
{
    /// <summary>
    /// Tools &gt; Options page exposing the knobs on Core's <c>ProfileOptions</c>
    /// (CONTRACT.md). Registered via <c>[ProvideOptionPage]</c> on
    /// <see cref="DataAnalyzerPackage"/>; values here seed a fresh
    /// <c>SsmsDataAnalyzer.Core.Model.ProfileOptions</c> for each run rather than the page
    /// holding a live reference, so Core stays free of any VS Shell dependency
    /// (per CONTRACT.md: "Core must not reference anything from Visual Studio").
    /// </summary>
    public sealed class DataAnalyzerOptionsPage : DialogPage
    {
        [Category("Go to source")]
        [DisplayName("Automatically execute the generated query")]
        [Description("When a 'Go to source' query window opens (from the tool window's Min/Max/table jump, or the results grid's right-click), run it immediately instead of leaving it for review. The generated queries are always bounded and read-only (SELECT TOP (1000) ... for the table jump, or a single-key-filtered SELECT for the value jump). Only executes when the new window is actually connected to the same server/database as where you clicked \"Go to source\" -- otherwise the query is left in place, unexecuted, and the status line says so. Turn off to always review before running.")]
        [DefaultValue(true)]
        public bool AutoExecuteGoToSourceQuery { get; set; } = true;

        [Category("Object Explorer integration")]
        [DisplayName("Enable right-click Analyze Data (experimental)")]
        [Description("Adds 'Analyze Data...' to the right-click menu of table nodes in Object Explorer, wired to that node's own connection (CONTRACT.md Amendment 13). Uses unsupported, undocumented SSMS API (see docs/oe-api.md) behind a try/catch that falls back to the Tools menu entry point if unavailable. Turn off if a future SSMS update breaks it.")]
        [DefaultValue(true)]
        public bool EnableObjectExplorerIntegration { get; set; } = true;

        [Category("Distinct counts")]
        [DisplayName("Distinct batch size")]
        [Description("Number of columns grouped into each batched COUNT(DISTINCT ...) query. Larger batches mean fewer scans but a bigger memory grant and more spill risk.")]
        [DefaultValue(8)]
        public int DistinctBatchSize { get; set; } = 8;

        [Category("Distinct counts")]
        [DisplayName("Max grant percent")]
        [Description("OPTION (MAX_GRANT_PERCENT = n) applied to every batched distinct query, capping how much of the server's memory one profiling query can reserve.")]
        [DefaultValue(25)]
        public int MaxGrantPercent { get; set; } = 25;

        [Category("Query execution")]
        [DisplayName("Query timeout (seconds)")]
        [Description("CommandTimeout applied to every profiling query.")]
        [DefaultValue(120)]
        public int QueryTimeoutSeconds { get; set; } = 120;

        [Category("Query execution")]
        [DisplayName("MAXDOP")]
        [Description("Optional OPTION (MAXDOP n) hint applied to profiling queries. Leave at 0 to omit the hint.")]
        [DefaultValue(0)]
        public int MaxDop { get; set; } = 0;

        [Category("Large tables")]
        [DisplayName("Large table threshold (rows)")]
        [Description("Above this estimated row count, the tool window warns and pre-selects sampling / column opt-out before running anything.")]
        [DefaultValue(10_000_000L)]
        public long LargeTableThreshold { get; set; } = 10_000_000L;

        [Category("DateCreated resolution")]
        [DisplayName("DateCreated candidate columns")]
        [Description("Ordered, comma-separated fallback list searched when no column literally named 'DateCreated' exists.")]
        [DefaultValue("CreatedDate,CreatedOn,Created,InsertDate,DateInserted,RowCreatedAt,ModifiedDate")]
        public string DateCreatedCandidates { get; set; } =
            "CreatedDate,CreatedOn,Created,InsertDate,DateInserted,RowCreatedAt,ModifiedDate";

        /// <summary>Splits <see cref="DateCreatedCandidates"/> into the ordered list Core's ProfileOptions expects.</summary>
        public IList<string> GetDateCreatedCandidateList()
        {
            var list = new List<string>();
            foreach (var part in (DateCreatedCandidates ?? string.Empty).Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list;
        }

        /// <summary>
        /// Copies this page's current values into a fresh Core ProfileOptions — one direction
        /// only (page -> ProfileOptions), per CONTRACT.md: Core must never reference anything
        /// from Visual Studio, so ProfileOptions itself cannot know about DialogPage. Called
        /// fresh at the start of every run (see OptionsAccessor / ProfileViewModel.RunAsync)
        /// rather than cached, so a change in Tools > Options takes effect on the very next
        /// run without restarting SSMS.
        /// </summary>
        public ProfileOptions ToProfileOptions()
        {
            return new ProfileOptions
            {
                DistinctBatchSize = DistinctBatchSize,
                MaxGrantPercent = MaxGrantPercent,
                QueryTimeoutSeconds = QueryTimeoutSeconds,
                MaxDop = MaxDop > 0 ? (int?)MaxDop : null,
                LargeTableThreshold = LargeTableThreshold,
                DateCreatedCandidates = GetDateCreatedCandidateList()
            };
        }
    }
}
