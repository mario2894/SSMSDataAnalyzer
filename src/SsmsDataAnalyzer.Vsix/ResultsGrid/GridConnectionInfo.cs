using System;
using System.Runtime.InteropServices;
using System.Security;
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
    /// the tool window's "Go to source"), so this deliberately does NOT distinguish
    /// SQL/Windows/Entra by that field. It uses the two facts that ARE reliable:
    ///
    /// <list type="bullet">
    /// <item><see cref="UIConnectionInfo.RenewableToken"/> non-null =&gt; a genuine
    /// Entra/token-based sign-in. There is no plaintext credential to copy into a new
    /// connection string, so decline.</item>
    /// <item>Otherwise, a UserName AND a password (from <c>Password</c> or
    /// <c>InMemoryPassword</c>) =&gt; a SQL login; anything else =&gt; Windows/integrated.</item>
    /// </list>
    ///
    /// v0.8.1 field report: an earlier version inferred the mode from UserName alone —
    /// "UserName but no Password" was treated as Entra. That is wrong, because SSMS populates
    /// UserName for WINDOWS authentication too (e.g. "DOMAIN\User", shown read-only in the
    /// Connect dialog), so the feature declined for every Windows-authenticated editor.
    /// Never re-derive the auth mode from UserName's presence.
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

            // Order matters. Do NOT infer the auth mode from whether UserName is populated:
            // SSMS fills UserName in for WINDOWS authentication too (e.g. "DOMAIN\User", shown
            // read-only in the Connect dialog), so "UserName but no Password" is the normal
            // Windows case, not an Entra one. Treating it as Entra was a real bug — it declined
            // for every Windows-authenticated editor.
            //
            // RenewableToken is the actual token-based indicator: SSMS populates it for
            // Entra/AAD sign-ins and leaves it null otherwise.
            if (ci.RenewableToken != null)
            {
                // Genuinely token-based. A brand-new SqlConnection would need the live access
                // token, which we cannot hand over through a connection string — decline rather
                // than build one that can never authenticate.
                return false;
            }

            string password = ci.Password;
            if (string.IsNullOrEmpty(password) && ci.InMemoryPassword != null)
            {
                // SQL logins may carry the secret here rather than in Password.
                password = SecureStringToString(ci.InMemoryPassword);
            }

            if (!string.IsNullOrEmpty(ci.UserName) && !string.IsNullOrEmpty(password))
            {
                csb.UserID = ci.UserName;
                csb.Password = password;
            }
            else
            {
                // No token and no password => Windows / integrated authentication, whether or
                // not UserName happens to be populated.
                csb.IntegratedSecurity = true;
            }

            connectionString = csb.ConnectionString;
            return true;
        }

        /// <summary>
        /// Reads a <see cref="SecureString"/> into a managed string just long enough to put it
        /// into a connection string, zeroing the unmanaged copy afterwards. CONTRACT.md
        /// Amendment 13 still holds: the value is used to build one connection and nothing
        /// else — never stored, logged, or surfaced in a message.
        /// </summary>
        private static string SecureStringToString(SecureString secure)
        {
            if (secure == null || secure.Length == 0) return null;

            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
                return Marshal.PtrToStringUni(ptr);
            }
            catch
            {
                // Never let a credential-reading failure become a user-visible crash; falling
                // through to integrated auth is the safe outcome.
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }
}
