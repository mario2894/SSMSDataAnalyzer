using System;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Vsix.Options
{
    /// <summary>
    /// Decouples ProfileViewModel (constructed standalone by ProfileView's parameterless
    /// constructor, with no direct package/service-provider access) from
    /// DataAnalyzerOptionsPage (a DialogPage, only reachable via Package.GetDialogPage on the
    /// UI thread). DataAnalyzerPackage sets Provider once during InitializeAsync;
    /// ProfileViewModel calls GetCurrent() at the start of every run rather than caching it —
    /// a change in Tools > Options must take effect on the next run without restarting SSMS.
    /// </summary>
    internal static class OptionsAccessor
    {
        public static Func<ProfileOptions> Provider { get; set; }

        /// <summary>Returns a fresh ProfileOptions from the current Options page state, or Core's own defaults if no provider is wired (e.g. a harness).</summary>
        public static ProfileOptions GetCurrent() => Provider != null ? Provider() : new ProfileOptions();

        /// <summary>
        /// "Automatically execute the generated query" (user request, defaults ON) — a
        /// VSIX-only concern, not part of Core's ProfileOptions (Core has no notion of query
        /// windows), so it gets its own small provider rather than being folded into
        /// <see cref="Provider"/>. Same "read fresh every time" rule as the rest of this
        /// class; defaults to true (matching the Options page's own default and the user's
        /// explicit ask) when nothing is wired, e.g. a harness.
        /// </summary>
        public static Func<bool> AutoExecuteGoToSourceQueryProvider { get; set; }

        public static bool GetAutoExecuteGoToSourceQuery() =>
            AutoExecuteGoToSourceQueryProvider != null ? AutoExecuteGoToSourceQueryProvider() : true;
    }
}
