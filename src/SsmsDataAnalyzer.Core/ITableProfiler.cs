using System;
using System.Threading;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core
{
    public sealed class ProfileProgress
    {
        /// <summary>"metadata" | "pass1" | "distinct"</summary>
        public string Stage { get; set; }

        public int CompletedUnits { get; set; }
        public int TotalUnits { get; set; }

        /// <summary>e.g. "columns 9-16"</summary>
        public string CurrentDetail { get; set; }

        /// <summary>Partial result, safe to bind to UI.</summary>
        public TableProfile Snapshot { get; set; }
    }

    public interface ITableProfiler
    {
        Task<TableProfile> ProfileAsync(
            string connectionString,
            TableRef table,
            ProfileOptions options,
            IProgress<ProfileProgress> progress,
            CancellationToken cancellationToken);
    }
}
