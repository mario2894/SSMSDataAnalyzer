using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.Model
{
    public sealed class ProfileOptions
    {
        /// <summary>The message written to SkipReason when sampling suppresses distinct counts.</summary>
        public const string SampledDistinctSkipReason = "Distinct counts are not computed on sampled data";

        public bool IncludeDistinct { get; set; }

        /// <summary>Columns per batched distinct query.</summary>
        public int DistinctBatchSize { get; set; }

        /// <summary>OPTION (MAX_GRANT_PERCENT = n) on batched distinct queries.</summary>
        public int MaxGrantPercent { get; set; }

        public int QueryTimeoutSeconds { get; set; }
        public int? MaxDop { get; set; }

        /// <summary>null = no sampling. Non-null forces DistinctCount to null everywhere.</summary>
        public double? SamplePercent { get; set; }

        public long LargeTableThreshold { get; set; }

        /// <summary>null = all columns.</summary>
        public ISet<string> IncludedColumns { get; set; }

        public IList<string> DateCreatedCandidates { get; set; }

        public ProfileOptions()
        {
            IncludeDistinct = true;
            DistinctBatchSize = 8;
            MaxGrantPercent = 25;
            QueryTimeoutSeconds = 120;
            LargeTableThreshold = 10000000;
            IncludedColumns = null;
            DateCreatedCandidates = new List<string>
            {
                "DateCreated", "CreatedDate", "CreatedOn", "Created",
                "InsertDate", "DateInserted", "RowCreatedAt", "ModifiedDate"
            };
        }

        internal void Validate()
        {
            if (DistinctBatchSize < 1) throw new ArgumentOutOfRangeException("DistinctBatchSize", "DistinctBatchSize must be at least 1.");
            if (MaxGrantPercent < 1 || MaxGrantPercent > 100) throw new ArgumentOutOfRangeException("MaxGrantPercent", "MaxGrantPercent must be between 1 and 100.");
            if (QueryTimeoutSeconds < 0) throw new ArgumentOutOfRangeException("QueryTimeoutSeconds", "QueryTimeoutSeconds cannot be negative.");
            if (MaxDop.HasValue && (MaxDop.Value < 0 || MaxDop.Value > 32767)) throw new ArgumentOutOfRangeException("MaxDop", "MaxDop must be between 0 and 32767.");
            if (SamplePercent.HasValue && (SamplePercent.Value <= 0 || SamplePercent.Value > 100)) throw new ArgumentOutOfRangeException("SamplePercent", "SamplePercent must be in (0, 100].");
        }
    }
}
