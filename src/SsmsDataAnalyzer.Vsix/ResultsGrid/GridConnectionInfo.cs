using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// Builds a Microsoft.Data.SqlClient connection string from the query editor's own
    /// <see cref="UIConnectionInfo"/> (docs/resultsgrid-api.md section 5 — the same public
    /// type docs/oe-api.md documents for Tier B, exposed here via the public
    /// <c>SqlScriptEditorControl.Connection</c>). CONTRACT.md Amendment 13's rule applies
    /// unchanged: inherit the editor's own connection, never invent one.
    ///
    /// <see cref="UIConnectionInfo.AuthenticationType"/>'s exact enum mapping is unverified
    /// (see DataAnalyzerPackage.TryOpenNewQueryWindowAsync's comment on the same problem for
    /// the tool window's "Go to source"), so this deliberately does NOT try to distinguish
    /// SQL/Windows/Entra by that field. Instead: no UserName -&gt; Windows/integrated auth
    /// (the common case, and safe to assume); UserName present but no in-memory Password
    /// (Entra/token-based sign-ins, which SSMS does not expose as a plaintext password) -&gt;
    /// decline rather than build a connection string that can never authenticate.
    /// </summary>
    internal static class GridConnectionInfo
    {
        /// <param name="databaseOverride">
        /// Use a specific database (e.g. a describe result's <c>source_database</c>) instead
        /// of the editor's current one. Null/empty falls back to
        /// <c>ci.AdvancedOptions["DATABASE"]</c> — the editor's own current database context.
        /// </param>
        public static bool TryBuild(UIConnectionInfo ci, string databaseOverride, out string connectionString)
        {
            connectionString = null;
            if (ci == null || string.IsNullOrEmpty(ci.ServerName)) return false;

            string database = databaseOverride;
            if (string.IsNullOrEmpty(database) && ci.AdvancedOptions != null)
            {
                database = ci.AdvancedOptions["DATABASE"];
            }

            var csb = new SqlConnectionStringBuilder
            {
                DataSource = ci.ServerName,
                ApplicationName = "SSMS Data Analyzer",
                TrustServerCertificate = true
            };
            if (!string.IsNullOrEmpty(database))
            {
                csb.InitialCatalog = database;
            }

            if (string.IsNullOrEmpty(ci.UserName))
            {
                csb.IntegratedSecurity = true;
            }
            else if (!string.IsNullOrEmpty(ci.Password))
            {
                csb.UserID = ci.UserName;
                csb.Password = ci.Password;
            }
            else
            {
                // A user name with no in-memory password: most likely an Entra/token-based
                // sign-in. We have no live token to hand to a brand-new SqlConnection —
                // decline rather than guess (same posture as OeTableInfo.TryBuildConnectionString).
                return false;
            }

            connectionString = csb.ConnectionString;
            return true;
        }
    }
}
