using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// Remembers, per query document, the text SSMS actually EXECUTED — the selection if one was
    /// highlighted when Execute ran, otherwise the whole document.
    ///
    /// Why (v0.13.2 field report): Go to source and pivot FK links describe the query to find
    /// each grid column's base table. They used to read the editor as it is at right-click time.
    /// A user ran a query, then pasted copied rows into the same window; the editor now held
    /// invalid SQL above the query, the describe call failed with "error 102: Incorrect syntax",
    /// and every jump declined even though the grid itself was perfectly valid. The grid always
    /// comes from the LAST execution in its window (SSMS replaces the grids on every run), so
    /// "last executed text for this document" is exactly the text that produced it.
    ///
    /// Hooked through public DTE CommandEvents on SSMS's own Execute commands — no undocumented
    /// API. If a hook isn't available (renamed command, package loaded after a query already
    /// ran), callers fall back to the old selection-or-full-text behaviour.
    ///
    /// The captured text is kept in memory only and never logged: it can contain literal
    /// values. Only the NAMES of hooked commands are logged.
    /// </summary>
    internal static class ExecutedQueryTextTracker
    {
        // Names as shown in Tools > Options > Environment > Keyboard. Each is looked up
        // individually, so a name missing from some SSMS build is simply skipped.
        private static readonly string[] ExecuteCommandNames =
        {
            "Query.Execute",
            "Query.ExecuteCurrentStatement",
            "Query.ExecuteWithDebugger"
        };

        private static readonly Dictionary<string, string> ExecutedTextByDocument =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // CommandEvents objects must be kept alive, or the COM event sink is collected and the
        // handler silently stops firing.
        private static readonly List<CommandEvents> KeepAlive = new List<CommandEvents>();

        private static DTE _dte;

        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _dte = serviceProvider.GetService(typeof(DTE)) as DTE;
                if (_dte == null)
                {
                    OeDiagnostics.Warn("Executed-query tracking unavailable: no DTE service. Go to source will read the editor's current text.");
                    return;
                }

                var hooked = new List<string>();
                foreach (var name in ExecuteCommandNames)
                {
                    try
                    {
                        Command command = _dte.Commands.Item(name, -1);
                        CommandEvents events = _dte.Events.CommandEvents[command.Guid, command.ID];
                        events.BeforeExecute += OnBeforeExecute;
                        KeepAlive.Add(events);
                        hooked.Add(name);
                    }
                    catch (ArgumentException)
                    {
                        // Command name not present in this SSMS build.
                    }
                    catch (Exception ex)
                    {
                        OeDiagnostics.Error("Executed-query tracking: could not hook " + name, ex);
                    }
                }

                OeDiagnostics.Info(hooked.Count > 0
                    ? "Executed-query tracking hooked: " + string.Join(", ", hooked)
                    : "Executed-query tracking: no Execute command found. Go to source will read the editor's current text.");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Executed-query tracking failed to initialize", ex);
            }
        }

        /// <summary>The text last executed in the ACTIVE document (the query window whose grid
        /// was just right-clicked), or null when nothing was captured for it.</summary>
        public static string TryGetForActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string key = _dte?.ActiveDocument?.FullName;
                if (string.IsNullOrEmpty(key)) return null;
                return ExecutedTextByDocument.TryGetValue(key, out var text) ? text : null;
            }
            catch
            {
                return null;
            }
        }

        private static void OnBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Document document = _dte?.ActiveDocument;
                string key = document?.FullName;
                if (string.IsNullOrEmpty(key)) return;

                string text = null;
                if (document.Selection is TextSelection selection && !selection.IsEmpty)
                {
                    // SSMS executes only the highlighted text when there is a selection.
                    text = selection.Text;
                }
                else if (document.Object("TextDocument") is TextDocument textDocument)
                {
                    text = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
                }

                if (string.IsNullOrWhiteSpace(text))
                    ExecutedTextByDocument.Remove(key);
                else
                    ExecutedTextByDocument[key] = text;
            }
            catch (Exception ex)
            {
                // Never interfere with the user's Execute. No text in the message.
                OeDiagnostics.Error("Executed-query tracking: capturing the executed text failed", ex);
            }
        }
    }
}
