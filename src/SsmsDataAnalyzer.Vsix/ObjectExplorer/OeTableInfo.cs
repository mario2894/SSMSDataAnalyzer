using System;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Vsix.ObjectExplorer
{
    /// <summary>
    /// Extracts (server, database, schema, table) and a connection string from an Object
    /// Explorer node, per docs/oe-api.md section 4.1. Deliberately two separate steps:
    /// URN parsing (identity — always cheap and safe) and connection-string building (which
    /// can legitimately fail, e.g. Entra/token-based connections — see
    /// <see cref="TryBuildConnectionString"/>), so a caller can still prefill the table
    /// target even when the connection can't be reused automatically.
    /// </summary>
    internal static class OeTableInfo
    {
        /// <summary>
        /// True if <paramref name="node"/>'s URN shape is a table (covers every table
        /// variant — UserTable*, MemoryOptimizedTable*, TemporalTable*, LedgerTable*,
        /// FileTable, ExternalTable, SystemTable, ... — they all share this UrnPath per
        /// docs/oe-api.md section 4).
        /// </summary>
        public static bool IsTableNode(INodeInformation node) =>
            node != null && string.Equals(node.UrnPath, "Server/Database/Table", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses the node's URN into a <see cref="TableRef"/>. This never touches
        /// credentials and cannot fail for anything CONTRACT.md cares about — it is pure
        /// string parsing of data SSMS itself already computed.
        /// </summary>
        public static bool TryParseTableRef(INodeInformation node, out TableRef table)
        {
            table = null;
            if (node?.Context == null) return false;

            Urn urn;
            try
            {
                urn = new Urn(node.Context);
            }
            catch (Exception)
            {
                return false;
            }

            if (!string.Equals(urn.Type, "Table", StringComparison.OrdinalIgnoreCase)) return false;

            var name = urn.GetAttribute("Name");
            var schema = urn.GetAttribute("Schema");
            var database = urn.GetAttribute("Name", "Database");
            var server = urn.GetAttribute("Name", "Server");

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(database)) return false;

            table = new TableRef
            {
                Server = server,
                Database = database,
                Schema = string.IsNullOrEmpty(schema) ? "dbo" : schema,
                Name = name
            };
            return true;
        }

        /// <summary>
        /// Builds a connection string from the node's own <see cref="SqlOlapConnectionInfoBase"/>
        /// — the whole point of CONTRACT.md Amendment 13, Priority 1: whatever auth SSMS
        /// itself used for this connection (Windows, SQL login, Entra password/integrated)
        /// is reused verbatim.
        ///
        /// Returns false for token-based Entra connections (<c>AccessToken != null</c>) —
        /// per docs/oe-api.md section 4.1's explicit guidance, reusing a live
        /// <see cref="IRenewableToken"/> across a brand-new <c>SqlConnection</c> is out of
        /// scope here; the caller falls back to the standalone auth picker (which now also
        /// offers "Microsoft Entra (interactive)" — CONTRACT.md Amendment 13, Priority 2)
        /// rather than us producing a connection string that can never authenticate.
        ///
        /// <paramref name="preferredDatabase"/>: v0.5.3 field report — for many Object
        /// Explorer nodes, <c>SqlConnectionInfo.DatabaseName</c> carries the SERVER-LEVEL
        /// connection's catalog (often empty, or "master"), not the database the clicked
        /// TABLE actually lives in. The caller's own URN-parsed <see cref="TableRef.Database"/>
        /// is the authoritative source for that (this is exactly what the profiler needs to
        /// run against) — pass it here and it wins over <c>ci.DatabaseName</c> whenever it is
        /// non-empty. <c>ci.DatabaseName</c> remains the fallback for callers that don't have
        /// a URN-derived database to hand (there are none today, but the parameter is optional
        /// so this method still degrades sensibly if that ever changes).
        ///
        /// Never logs or returns the built string anywhere except directly to the caller,
        /// which must follow the same rule.
        /// </summary>
        public static bool TryBuildConnectionString(
            INodeInformation node, string preferredDatabase, out string connectionString, out bool trustServerCertificate)
        {
            connectionString = null;
            trustServerCertificate = false;

            var ci = node?.Connection as SqlConnectionInfo;
            if (ci == null) return false;

            // Entra/token-based auth: the credential lives in a live IRenewableToken, not a
            // password we can copy into a new connection string. Degrade rather than guess.
            if (ci.AccessToken != null) return false;

            if (string.IsNullOrEmpty(ci.ServerName)) return false;

            // v0.5.3 field report: SqlConnectionStringBuilder.InitialCatalog THROWS
            // ArgumentNullException on a null assignment (it has no "leave unset" tolerance
            // for null the way some of its other properties do) — the exact crash the user
            // hit. Every assignment below is guarded the same way: null/empty in, property
            // simply left unset, never assigned.
            string database = !string.IsNullOrEmpty(preferredDatabase) ? preferredDatabase : ci.DatabaseName;

            trustServerCertificate = ci.TrustServerCertificate;

            var csb = new SqlConnectionStringBuilder
            {
                DataSource = ci.ServerName,
                TrustServerCertificate = ci.TrustServerCertificate,
                ApplicationName = "SSMS Data Analyzer"
            };
            if (!string.IsNullOrEmpty(database))
            {
                csb.InitialCatalog = database;
            }

            if (ci.UseIntegratedSecurity)
            {
                csb.IntegratedSecurity = true;
            }
            else
            {
                switch (ci.Authentication)
                {
                    case SqlConnectionInfo.AuthenticationMethod.ActiveDirectoryPassword:
                        csb.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                        if (!string.IsNullOrEmpty(ci.UserName)) csb.UserID = ci.UserName;
                        if (!string.IsNullOrEmpty(ci.Password)) csb.Password = ci.Password;
                        break;
                    case SqlConnectionInfo.AuthenticationMethod.ActiveDirectoryIntegrated:
                        csb.Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                        break;
                    default:
                        // SqlPassword / NotSpecified: plain SQL login, exactly as SSMS has it.
                        if (!string.IsNullOrEmpty(ci.UserName)) csb.UserID = ci.UserName;
                        if (!string.IsNullOrEmpty(ci.Password)) csb.Password = ci.Password;
                        break;
                }
            }

            connectionString = csb.ConnectionString;
            return true;
        }
    }
}
