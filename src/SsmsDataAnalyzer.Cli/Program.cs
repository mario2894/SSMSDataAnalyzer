using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core;
using SsmsDataAnalyzer.Core.Export;
using SsmsDataAnalyzer.Core.Metadata;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Cli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
            {
                Console.WriteLine(CliOptions.Usage);
                return args.Length == 0 ? 1 : 0;
            }

            CliOptions cli;
            try
            {
                cli = CliOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(CliOptions.Usage);
                return 2;
            }

            var table = TableRef.Parse(cli.Table);
            table.Server = cli.Server;
            table.Database = cli.Database;

            var csb = new SqlConnectionStringBuilder
            {
                DataSource = cli.Server,
                InitialCatalog = cli.Database,
                IntegratedSecurity = true,

                // Encrypt and TrustServerCertificate are set explicitly, never inherited.
                // The Encrypt default moved from false to true at MDS 4.0, and this connection
                // string has to behave identically under the 6.x client SSMS hosts and any
                // future one. Verified equal (Encrypt=True, TSC=False) in both 5.2.2 and 6.1.5;
                // stating them means a later shift cannot silently change how the CLI connects.
                Encrypt = true,
                TrustServerCertificate = true,

                ApplicationName = "SsmsDataAnalyzer.Cli",
                ConnectTimeout = 15
            };

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    // Do not kill the process — cancel, so the partial profile still prints.
                    e.Cancel = true;
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Cancelling… partial results will still be shown.");
                    cts.Cancel();
                };

                try
                {
                    if (cli.ShowSql)
                        return await ShowSqlAsync(csb.ConnectionString, table, cli.Profile, cts.Token).ConfigureAwait(false);

                    return await RunAsync(csb.ConnectionString, table, cli, cts.Token).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message);
                    return 4;
                }
                catch (SqlException ex)
                {
                    Console.Error.WriteLine("SQL error " + ex.Number + ": " + ex.Message);
                    return 3;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message);
                    return 1;
                }
            }
        }

        private static async Task<int> RunAsync(string connectionString, TableRef table, CliOptions cli, CancellationToken token)
        {
            bool live = !Console.IsOutputRedirected;
            int lastLineLength = 0;

            var progress = new Progress<ProfileProgress>(p =>
            {
                string line = string.Format(CultureInfo.InvariantCulture,
                    "[{0,-8}] {1,3}/{2,-3} {3}", p.Stage, p.CompletedUnits, p.TotalUnits, p.CurrentDetail);

                if (live)
                {
                    if (line.Length < lastLineLength) line += new string(' ', lastLineLength - line.Length);
                    lastLineLength = line.Length;
                    Console.Error.Write("\r" + line);
                }
                else
                {
                    Console.Error.WriteLine(line);
                }
            });

            var profiler = new TableProfiler();
            TableProfile profile = await profiler
                .ProfileAsync(connectionString, table, cli.Profile, progress, token)
                .ConfigureAwait(false);

            if (live && lastLineLength > 0)
                Console.Error.Write("\r" + new string(' ', lastLineLength) + "\r");

            string output;
            switch (cli.Format)
            {
                case "md": output = MarkdownExporter.Export(profile); break;
                case "csv": output = CsvExporter.Export(profile); break;
                default: output = TableRenderer.Render(profile); break;
            }

            Console.Out.Write(output);

            if (!string.IsNullOrEmpty(cli.OutputPath))
            {
                File.WriteAllText(cli.OutputPath, output);
                Console.Error.WriteLine("Written to " + cli.OutputPath);
            }

            return token.IsCancellationRequested ? 130 : 0;
        }

        /// <summary>--show-sql: read metadata, print the plan, run nothing against the data.</summary>
        private static async Task<int> ShowSqlAsync(string connectionString, TableRef table, ProfileOptions options, CancellationToken token)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                var schema = await new SchemaReader().ReadAsync(connection, table, options, token).ConfigureAwait(false);

                Console.WriteLine("-- PASS 1");
                foreach (var q in ProfileSqlBuilder.BuildPass1(table, schema.Columns, schema.DateCreatedColumn, options))
                {
                    Console.WriteLine("-- " + q.Detail);
                    Console.WriteLine(q.Sql);
                    Console.WriteLine();
                }

                var plan = DistinctPlanner.Plan(table, schema.Columns, options);
                Console.WriteLine("-- PASS 2 — " + plan.TotalQueries + " quer"
                    + (plan.TotalQueries == 1 ? "y" : "ies") + " over ~"
                    + schema.EstimatedRows.ToString("N0", CultureInfo.InvariantCulture) + " rows");

                foreach (var q in plan.Queries)
                {
                    Console.WriteLine("-- [" + q.Kind + "] " + q.Detail);
                    Console.WriteLine(q.Sql);
                    Console.WriteLine();
                }

                foreach (var pair in plan.Skipped)
                    Console.WriteLine("-- skipped " + pair.Key + ": " + pair.Value);
            }

            return 0;
        }
    }
}
