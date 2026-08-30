using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ObjectExplorer
{
    /// <summary>
    /// CONTRACT.md Amendment 13, Bug 2: the right-click "Analyze Data" path had no way to
    /// tell the user (or us) WHY it silently didn't appear — the feature flag, a failed
    /// service lookup, and a never-firing event all looked identical: nothing happens.
    /// Writes to SSMS's own ActivityLog (Help &gt; ... &gt; View Log, or
    /// %AppData%\Microsoft\...\ActivityLog.xml) via the Try* APIs, which never throw, so a
    /// diagnostics call can never itself become a new failure mode. One-shot messages are
    /// deduplicated per process so routine, high-frequency events (every Object Explorer
    /// selection change) don't flood the log; failures always log every time.
    /// </summary>
    internal static class OeDiagnostics
    {
        private const string Source = "SsmsDataAnalyzer.ObjectExplorer";

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Logs unconditionally. Use for state changes worth seeing every time (rare by construction — e.g. once per newly-seen menu handler).</summary>
        public static void Info(string message) => ActivityLog.TryLogInformation(Source, message);

        /// <summary>Logs the first time a given <paramref name="key"/> is seen this session, then never again — for routine/high-frequency events.</summary>
        public static void InfoOnce(string key, string message)
        {
            lock (LoggedOnce)
            {
                if (!LoggedOnce.Add(key)) return;
            }
            ActivityLog.TryLogInformation(Source, message);
        }

        public static void Warn(string message) => ActivityLog.TryLogWarning(Source, message);

        public static void Error(string message, Exception ex = null)
        {
            ActivityLog.TryLogError(Source, ex == null ? message : $"{message}: {ex}");
        }
    }
}
