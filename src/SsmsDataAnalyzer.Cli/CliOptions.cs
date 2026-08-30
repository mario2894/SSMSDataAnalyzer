using System;
using System.Collections.Generic;
using System.Globalization;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Cli
{
    internal sealed class CliOptions
    {
        public string Server = ".";
        public string Database;
        public string Table;
        public string Format = "table";
        public int? ExactTimeoutSeconds;
        public string OutputPath;
        public bool ShowSql;
        public readonly ProfileOptions Profile = new ProfileOptions();

        public const string Usage = @"ssmsanalyze — SQL Server table data profiler

USAGE
  ssmsanalyze analyze --server <s> --db <database> --table <schema.table> [options]

REQUIRED
  --db <name>              Database to profile
  --table <schema.table>   Table to profile (schema defaults to dbo)

OPTIONS
  --server <name>          SQL Server instance          (default: .)
  --no-distinct            Skip pass 2 entirely
  --batch-size <n>         Columns per batched distinct query (default: 8)
  --sample <pct>           TABLESAMPLE SYSTEM percent; suppresses distinct counts
  --exact-timeout <sec>    Command timeout in seconds   (default: 120)
  --format table|md|csv    Output format                (default: table)
  --max-grant <pct>        OPTION (MAX_GRANT_PERCENT=n) (default: 25)
  --maxdop <n>             OPTION (MAXDOP n)
  --columns <a,b,c>        Profile only these columns
  --out <path>             Write the report to a file as well as stdout
  --show-sql               Print the generated pass-1 / pass-2 SQL, then exit
  -h, --help               This text

Press Ctrl+C during a run: the partial profile collected so far is still printed.";

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            int i = 0;

            if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
            {
                if (!string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Unknown command '" + args[0] + "'. The only command is 'analyze'.");
                i = 1;
            }

            for (; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--server": o.Server = Next(args, ref i, a); break;
                    case "--db":
                    case "--database": o.Database = Next(args, ref i, a); break;
                    case "--table": o.Table = Next(args, ref i, a); break;
                    case "--format": o.Format = Next(args, ref i, a).ToLowerInvariant(); break;
                    case "--out": o.OutputPath = Next(args, ref i, a); break;
                    case "--no-distinct": o.Profile.IncludeDistinct = false; break;
                    case "--show-sql": o.ShowSql = true; break;
                    case "--batch-size": o.Profile.DistinctBatchSize = Int(Next(args, ref i, a), a); break;
                    case "--max-grant": o.Profile.MaxGrantPercent = Int(Next(args, ref i, a), a); break;
                    case "--maxdop": o.Profile.MaxDop = Int(Next(args, ref i, a), a); break;
                    case "--exact-timeout": o.ExactTimeoutSeconds = Int(Next(args, ref i, a), a); break;
                    case "--sample": o.Profile.SamplePercent = Double(Next(args, ref i, a), a); break;
                    case "--columns":
                        o.Profile.IncludedColumns = new HashSet<string>(
                            Next(args, ref i, a).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                            StringComparer.OrdinalIgnoreCase);
                        break;
                    default:
                        throw new ArgumentException("Unknown option '" + a + "'.");
                }
            }

            if (o.ExactTimeoutSeconds.HasValue)
                o.Profile.QueryTimeoutSeconds = o.ExactTimeoutSeconds.Value;

            if (string.IsNullOrWhiteSpace(o.Database)) throw new ArgumentException("--db is required.");
            if (string.IsNullOrWhiteSpace(o.Table)) throw new ArgumentException("--table is required.");
            if (o.Format != "table" && o.Format != "md" && o.Format != "csv")
                throw new ArgumentException("--format must be table, md or csv.");

            o.Profile.Validate2();
            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(flag + " needs a value.");
            return args[++i];
        }

        private static int Int(string s, string flag)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new ArgumentException(flag + " needs an integer, got '" + s + "'.");
            return v;
        }

        private static double Double(string s, string flag)
        {
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new ArgumentException(flag + " needs a number, got '" + s + "'.");
            return v;
        }
    }

    internal static class ProfileOptionsCliExtensions
    {
        /// <summary>Surfaces Core's internal validation as a friendly CLI error.</summary>
        public static void Validate2(this ProfileOptions options)
        {
            if (options.DistinctBatchSize < 1) throw new ArgumentException("--batch-size must be at least 1.");
            if (options.MaxGrantPercent < 1 || options.MaxGrantPercent > 100) throw new ArgumentException("--max-grant must be between 1 and 100.");
            if (options.QueryTimeoutSeconds < 0) throw new ArgumentException("--exact-timeout cannot be negative.");
            if (options.MaxDop.HasValue && (options.MaxDop.Value < 0 || options.MaxDop.Value > 32767)) throw new ArgumentException("--maxdop must be between 0 and 32767.");
            if (options.SamplePercent.HasValue && (options.SamplePercent.Value <= 0 || options.SamplePercent.Value > 100)) throw new ArgumentException("--sample must be greater than 0 and at most 100.");
        }
    }
}
