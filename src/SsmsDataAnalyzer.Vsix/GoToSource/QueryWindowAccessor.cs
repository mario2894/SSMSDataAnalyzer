using System;
using System.Threading.Tasks;

namespace SsmsDataAnalyzer.Vsix.GoToSource
{
    /// <summary>Outcome of attempting to open a new query window. <see cref="Reason"/> is
    /// always set — on success it is a human-readable confirmation ("opened via ..."), on
    /// failure it names the actual cause (never "see the ActivityLog" — that ergonomics
    /// mistake is exactly what CONTRACT.md's "Go to source" work has had to walk back twice).</summary>
    internal struct QueryWindowOpenResult
    {
        public bool Success;
        public string Reason;

        public static QueryWindowOpenResult Ok(string reason) => new QueryWindowOpenResult { Success = true, Reason = reason };
        public static QueryWindowOpenResult Fail(string reason) => new QueryWindowOpenResult { Success = false, Reason = reason };
    }

    /// <summary>
    /// Decouples ProfileViewModel/ResultsGridSourceCommand (no package/service-provider
    /// reference) from the actual "open a new query window" mechanism, which needs
    /// package-level, VS-Shell-hosted code. Same shape as Options/OptionsAccessor.cs.
    /// DataAnalyzerPackage wires TryOpenNewQueryWindowAsync once during InitializeAsync.
    ///
    /// Async, not a plain Func&lt;bool&gt;: the implementation opens a fresh ADO.NET
    /// connection to hand to the query window, which is real network I/O and must not block
    /// the UI thread (same rule as every other query in this codebase).
    /// </summary>
    internal static class QueryWindowAccessor
    {
        /// <summary>(sql text, ADO.NET connection string for the target server/database,
        /// OPTIONAL real UIConnectionInfo to copy from — see TryOpenAsync) -> outcome, with a
        /// real reason either way.</summary>
        public static Func<string, string, Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo, Task<QueryWindowOpenResult>> TryOpenNewQueryWindowAsync { get; set; }

        /// <param name="sourceConnectionInfo">
        /// v0.6.1 field report — the new window opened but showed "Disconnected.": decompiling
        /// CreateNewScript/SetConnection showed <c>IsConnected</c> is purely
        /// <c>m_connection.State == Open</c>, so a hand-built <see cref="Microsoft.SqlServer.
        /// Management.Smo.RegSvrEnum.UIConnectionInfo"/> missing fields like <c>ServerType</c>
        /// (required by CreateNewScript's own validation for a live-connection call — the
        /// literal GUID is <c>RegSvrConnectionInfo.SqlServerTypeGuid</c>, read from that
        /// type's own .cctor, not guessed) can leave the handoff incomplete. Pass the REAL,
        /// already-working <c>UIConnectionInfo</c> from wherever "Go to source" was clicked —
        /// the Object Explorer node's connection, or the clicked results-grid editor's
        /// <c>SqlScriptEditorControl.Connection</c> — when the caller has one; the
        /// implementation copies it (<c>.Copy()</c>) and overrides only the database, per the
        /// "never reconstruct credentials, only inherit" rule (CONTRACT.md Amendment 13). Null
        /// when no such live object is available (e.g. the tool window, which by "Go to
        /// source" time only has a connection string) — the implementation falls back to
        /// hand-building, now with the previously-missing fields filled in.
        /// </param>
        public static async Task<QueryWindowOpenResult> TryOpenAsync(
            string sql, string connectionString,
            Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo sourceConnectionInfo = null)
        {
            var handler = TryOpenNewQueryWindowAsync;
            if (handler == null)
            {
                return QueryWindowOpenResult.Fail(
                    "the query-window opener was never wired up (DataAnalyzerPackage.InitializeQueryWindowAccessor did not run) — this points at a package initialization problem, not this specific action.");
            }
            return await handler(sql, connectionString, sourceConnectionInfo).ConfigureAwait(true);
        }
    }
}
