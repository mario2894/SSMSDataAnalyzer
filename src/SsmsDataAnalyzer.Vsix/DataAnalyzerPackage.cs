using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.Options;
using SsmsDataAnalyzer.Vsix.ToolWindow;
using Task = System.Threading.Tasks.Task;

// CONTRACT.md Amendment 7 / Amendment 9: the VSIX does not ship Microsoft.Data.SqlClient
// (see SsmsHostOwnedAssembly / SuppressFromVsix in the .csproj) — it binds to the copy
// SSMS 22 already has loaded, at assembly version 6.0.0.0 (FileVersion 6.15.26114.3,
// ProductVersion 6.1.5). Per Amendment 9, Agent A has since pinned Core to
// Microsoft.Data.SqlClient 6.1.5 — an exact match with the host (the package keeps
// AssemblyVersion at major.0.0.0, so 6.1.5 is also 6.0.0.0) — so this redirect is no longer
// load-bearing: Core's IL already references "Microsoft.Data.SqlClient, Version=6.0.0.0"
// directly. Kept anyway as harmless insurance in case Core ever reverts to a pre-6.x
// SqlClient (CreatePkgDef rejects OldVersionUpperBound >= NewVersion, so the range can only
// cover versions strictly below the 6.0.0.0 target — it can't also insure against some
// future 6.x+ bump past what the host provides; that would need updating alongside any such
// bump). ProvideBindingRedirection emits the pkgdef entry VS's package-load-time
// binding-redirect mechanism reads; it is assembly-scoped (not a class attribute —
// AttributeUsage restricts it to AssemblyTarget). GenerateCodeBase is deliberately false —
// we are not shipping our own copy for a <codeBase> to point at.
[assembly: Microsoft.VisualStudio.Shell.ProvideBindingRedirection(
    AssemblyName = "Microsoft.Data.SqlClient",
    PublicKeyToken = "23ec7fc2d6eaa4a5",
    OldVersionLowerBound = "0.0.0.0",
    OldVersionUpperBound = "5.65535.65535.65535",
    NewVersion = "6.0.0.0",
    GenerateCodeBase = false)]

namespace SsmsDataAnalyzer.Vsix
{
    /// <summary>
    /// The VSPackage entry point. Target: SSMS 22 only (VS 18.x shell, amd64,
    /// .NET Framework 4.7.2 hosted) — see CONTRACT.md / PLAN.md at the repo root.
    ///
    /// Initialization is asynchronous and background-thread-safe: <see cref="InitializeAsync"/>
    /// must not assume it is on the UI thread, and any UI-affinitized call (registering the
    /// tool window's frame commands, touching WPF) explicitly switches via
    /// <see cref="JoinableTaskFactory.SwitchToMainThreadAsync"/> first.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "0.1.0", IconResourceID = 400)]
    [Guid(PackageGuids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ProfileToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057" /* Solution Explorer tab group */)]
    // v0.7.2: "Find in Results" — see ResultsGrid.GridFindToolWindow's doc comment for why
    // this replaced the floating WPF Window used through v0.7.1. No docking-group Window
    // GUID given deliberately: unlike ProfileToolWindow (a persistent, user-navigated-to
    // panel that makes sense tabbed alongside Solution Explorer), this is a lightweight
    // utility window the user opens transiently from a context menu — VS's own default
    // placement (floating on first show) matches how Find/Replace-style tool windows
    // conventionally behave, and MultiInstances = 0 (the default) is correct since
    // ResultsGridFindCommand always shows/reuses id 0, never creates a second instance.
    [ProvideToolWindow(typeof(ResultsGrid.GridFindToolWindow))]
    [ProvideOptionPage(typeof(DataAnalyzerOptionsPage), "SSMS Data Analyzer", "General", 0, 0, true)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class DataAnalyzerPackage : AsyncPackage
    {
        // Tier A (CONTRACT.md Amendment 13, Priority 1). Kept as a field only so it isn't
        // garbage-collected out from under its event subscription; disposed alongside the
        // package. Null whenever Tier A is off (feature flag) or its wiring failed — both
        // are the same "use Tier B instead" state to every other code path.
        private OeContextBridge _oeBridge;

        /// <summary>
        /// Async, background-thread package initialization. Do not call ThrowIfNotOnUIThread
        /// here without first switching — the whole point of AllowsBackgroundLoading is that
        /// this runs off the UI thread until something explicitly needs it.
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Command registration touches the shell's command service, which is UI-affinitized.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await Commands.AnalyzeDataCommand.InitializeAsync(this);
            await ResultsGrid.ResultsGridSourceCommand.InitializeAsync(this);
            await ResultsGrid.ResultsGridFindCommand.InitializeAsync(this);

            // v0.7.6 field report (SSMS 22.3 vs our 22.9 dev build): probe ONCE, here, rather
            // than waiting for the first results-grid right-click, so the ActivityLog carries
            // the answer from the very start of the session. ResultsGridCapability itself has
            // no compile-time reference to any SSMS grid type -- see its doc comment -- so
            // this call is safe unconditionally, on every SSMS 22.x build.
            if (ResultsGrid.ResultsGridCapability.IsSupported)
            {
                OeDiagnostics.Info("Results-grid features (Find in Results, Go to source) are supported in this SSMS session.");
            }
            else
            {
                OeDiagnostics.Warn("Results-grid features (Find in Results, Go to source) are NOT supported in this SSMS session and will not appear on the results-grid context menu: " + ResultsGrid.ResultsGridCapability.UnsupportedReason);
            }

            InitializeObjectExplorerIntegration();
            InitializeOptionsAccessor();
            InitializeQueryWindowAccessor();
        }

        /// <summary>
        /// Wires OptionsAccessor.Provider so ProfileViewModel (which has no direct package
        /// reference — it's constructed standalone by ProfileView) can read the live Tools >
        /// Options state fresh on every run. GetDialogPage requires the UI thread; the
        /// provider delegate itself is only ever invoked from ProfileViewModel.RunAsync right
        /// after it switches to the UI thread, so this assertion is a real safety check, not
        /// a formality.
        /// </summary>
        private void InitializeOptionsAccessor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Options.OptionsAccessor.Provider = () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var page = (DataAnalyzerOptionsPage)GetDialogPage(typeof(DataAnalyzerOptionsPage));
                return page.ToProfileOptions();
            };
            // "Go to source" auto-execute (user request): read fresh per invocation, same
            // pattern as the ProfileOptions provider above, so a Tools > Options change takes
            // effect on the very next click without restarting SSMS.
            Options.OptionsAccessor.AutoExecuteGoToSourceQueryProvider = () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var page = (DataAnalyzerOptionsPage)GetDialogPage(typeof(DataAnalyzerOptionsPage));
                return page.AutoExecuteGoToSourceQuery;
            };
        }

        /// <summary>
        /// CONTRACT.md Amendment 14/15 "Go to source": wires QueryWindowAccessor so
        /// ProfileViewModel (no package reference) can open a new query window without
        /// knowing anything about ServiceCache/IScriptFactory.
        /// </summary>
        private void InitializeQueryWindowAccessor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            GoToSource.QueryWindowAccessor.TryOpenNewQueryWindowAsync = TryOpenNewQueryWindowAsync;
        }

        /// <summary>
        /// Opens a NEW query window pre-filled with <paramref name="sql"/>, connected to the
        /// same server/database <paramref name="connectionString"/> already proved reachable
        /// during profiling — and does NOT execute it (CONTRACT.md: read-only by default, the
        /// user reviews and runs).
        ///
        /// v0.5.0 field report: this silently produced "Could not open a query window — see
        /// the SSMS ActivityLog for details" with perfect data behind it (a real single-column
        /// FK, a formattable literal). Root-caused by IL: <c>ServiceCache.ScriptFactory</c>'s
        /// getter (<c>SqlPackageBase.dll</c>) calls a private generic
        /// <c>ServiceCache.GetService&lt;T&gt;()</c> whose FIRST attempt is
        /// <c>Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(IScriptFactory))</c>
        /// — the VS-wide GLOBAL service container. If <c>IScriptFactory</c> was never
        /// registered there (SSMS's own internal callers may hold a direct reference instead
        /// of resolving through this public facade every time — that path is invisible to us),
        /// <c>GetGlobalService</c> returns null WITHOUT throwing, and the surrounding
        /// try/catch in <c>GetService&lt;T&gt;</c> only has a fallback for an EXCEPTION, not a
        /// clean null — so <c>ScriptFactory</c> comes back null, cleanly, exactly matching
        /// what this project's own logging showed. This is why the message never carried a
        /// "real" exception: there wasn't one.
        ///
        /// Two routes, tried in order, with the specific reason for a failure always reported
        /// (never "see the ActivityLog" — Amendment-level rule by now):
        /// 1. <c>ServiceCache.ScriptFactory.CreateNewScript(text, UIConnectionInfo,
        ///    IDbConnection)</c> — precise: WE choose the exact server/database via a fresh,
        ///    already-open connection, no ambiguity about what the new window ends up
        ///    connected to. Used whenever <c>ScriptFactory</c> actually resolves.
        /// 2. <c>EnvDTE.DTE.Commands</c>, self-discovered at runtime (never a hardcoded
        ///    command name we can't verify from static analysis — SSMS's compiled .vsct/
        ///    .ctmenu command tables are a compressed CFCT blob we cannot read, so any exact
        ///    string would be a guess) for a "new query" command, executed, then the new
        ///    window's own connection is READ BACK (public <c>SqlScriptEditorControl</c>
        ///    surface, same as docs/resultsgrid-api.md §5) and checked against our target
        ///    server before writing any text into it — if it landed on the wrong server this
        ///    declines rather than silently handing the user a window pointed at the wrong
        ///    instance.
        /// </summary>
        private async Task<QueryWindowOpenResult> TryOpenNewQueryWindowAsync(
            string sql, string connectionString,
            Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo sourceConnectionInfo)
        {
            Microsoft.Data.SqlClient.SqlConnection connection = null;
            try
            {
                var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);

                // Real network I/O — off the UI thread, exactly like every other query in this
                // codebase.
                connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                await Task.Run(() => connection.Open()).ConfigureAwait(true);

                await JoinableTaskFactory.SwitchToMainThreadAsync();

                Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo uiConnectionInfo;
                if (sourceConnectionInfo != null)
                {
                    // v0.6.1 fix: COPY the real, already-working UIConnectionInfo from wherever
                    // "Go to source" was clicked, rather than hand-building one — per
                    // CONTRACT.md Amendment 13 ("never reconstruct credentials, only inherit")
                    // and confirmed necessary: the hand-built path below was missing ServerType
                    // and Password, either of which can leave the new window "Disconnected."
                    // Override only the database — the one thing that legitimately differs
                    // between the clicked session and the FK target.
                    uiConnectionInfo = sourceConnectionInfo.Copy();
                    uiConnectionInfo.ServerName = csb.DataSource;
                    if (uiConnectionInfo.AdvancedOptions != null)
                    {
                        uiConnectionInfo.AdvancedOptions["DATABASE"] = csb.InitialCatalog;
                    }
                }
                else
                {
                    // No live UIConnectionInfo available (the tool window's "Go to source" only
                    // has a connection string by this point) — hand-build one, now with the two
                    // fields v0.6.1 found missing:
                    //   - ServerType: CreateNewScript's own validation (decompiled IL) checks
                    //     each UIConnectionInfo's ServerType against the Database Engine GUID
                    //     when a live IDbConnection is supplied; a default/empty Guid fails
                    //     that check. The literal value below is read directly from
                    //     RegSvrConnectionInfo.SqlServerTypeGuid's own static field — not typed
                    //     out as a magic string — so it can never drift from SSMS's own value.
                    //   - Password: for SQL authentication this was parsed into the ADO.NET
                    //     connection string (csb.Password) already but was never copied across
                    //     — a real gap, now fixed. AuthenticationType is still set to the
                    //     "NotSpecified" convention (0): decompiling SetConnection's own IL
                    //     shows it never reads this field, so there is still no confirmed
                    //     mapping to guess a non-zero value from.
                    uiConnectionInfo = new Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo
                    {
                        ServerName = csb.DataSource,
                        ServerType = Microsoft.SqlServer.Management.Smo.RegSvrEnum.RegSvrConnectionInfo.SqlServerTypeGuid,
                        AuthenticationType = 0
                    };
                    if (uiConnectionInfo.AdvancedOptions != null)
                    {
                        uiConnectionInfo.AdvancedOptions["DATABASE"] = csb.InitialCatalog;
                    }
                    if (!csb.IntegratedSecurity && !string.IsNullOrEmpty(csb.UserID))
                    {
                        uiConnectionInfo.UserName = csb.UserID;
                        if (!string.IsNullOrEmpty(csb.Password))
                        {
                            uiConnectionInfo.Password = csb.Password;
                        }
                    }
                }

                var scriptFactory = Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache.ScriptFactory;
                if (scriptFactory != null)
                {
                    // v0.5.3 field report: CreateNewScript(string, UIConnectionInfo,
                    // IDbConnection) DOES NOT take script content — its first parameter is
                    // "strFullPathToScript", a TEMPLATE FILE PATH. Confirmed by decompiling its
                    // actual implementation (not just its overload list): it calls
                    // System.IO.File.Exists on that first argument and throws
                    // FileNotFoundException via SRError.CannotFindTemplateFileForScript when it
                    // isn't a real file — exactly the exception the user hit, with our SQL text
                    // named as the "missing" file. There is NO overload that accepts literal
                    // script text; every one of them ultimately routes through this same
                    // file-path parameter.
                    //
                    // CreateNewBlankScript(ScriptType, UIConnectionInfo, IDbConnection) is the
                    // right primitive instead — its own IL shows it resolves SSMS's OWN known-
                    // good blank .sql template path (GetFullPathToBlankScriptTemplate, backed
                    // by a real file under the SSMS install dir) and calls CreateNewScript with
                    // THAT, sidestepping the FileNotFoundException entirely, connected via our
                    // live IDbConnection exactly as before. It creates an EMPTY connected
                    // window; the query text is then written into it via EnvDTE (the same
                    // Document.Selection mechanism already used by the fallback below).
                    scriptFactory.CreateNewBlankScript(
                        Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptType.Sql,
                        uiConnectionInfo, connection);

                    EnvDTE.DTE dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    var activeDocument = dte?.ActiveDocument;
                    if (activeDocument == null)
                    {
                        return QueryWindowOpenResult.Fail(
                            "ServiceCache.ScriptFactory.CreateNewBlankScript created a window, but no active EnvDTE document could be found to write the query into.");
                    }

                    if (!TryInsertSqlIntoDocument(activeDocument, sql, out var insertError))
                    {
                        return QueryWindowOpenResult.Fail(
                            $"ServiceCache.ScriptFactory.CreateNewBlankScript created a window, but writing the query into it failed: {insertError}");
                    }

                    var confirmation = $"Opened a new query window on {csb.DataSource}/{csb.InitialCatalog}.";
                    var editorControl = FindSqlScriptEditorControl(activeDocument);
                    confirmation += EnsureConnected(editorControl, uiConnectionInfo, connection);
                    confirmation += TryAutoExecute(editorControl);
                    OeDiagnostics.Info($"'Go to source' via ServiceCache.ScriptFactory.CreateNewBlankScript: {confirmation}");
                    return QueryWindowOpenResult.Ok(confirmation);
                }

                OeDiagnostics.Warn("'Go to source': ServiceCache.ScriptFactory resolved to null (IScriptFactory is not registered as a VS-wide global service in this session) — falling back to EnvDTE.");
                return await TryOpenViaDteNewQueryAsync(sql, csb, connection).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("'Go to source' failed to open a new query window", ex);
                connection?.Dispose();
                return QueryWindowOpenResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback when <c>ServiceCache.ScriptFactory</c> is unavailable. Never hardcodes an
        /// unverifiable DTE command name (SSMS's compiled .vsct/.ctmenu command table is a
        /// compressed CFCT blob nobody on this team could read — see
        /// docs/resultsgrid-api.md §3.2's honest "could not decompress" note): instead
        /// enumerates <c>dte.Commands</c> at runtime and picks whichever available command
        /// looks like "open a new query window", preferring one that mentions "current
        /// connection" if more than one candidate matches. Every candidate considered is
        /// logged, so a report against a future SSMS build is diagnosable instead of another
        /// silent dead end.
        ///
        /// The new window inherits WHATEVER connection DTE considers "current" — not
        /// necessarily our target server/database. After the command runs, this reads the new
        /// window's own connection back (public SqlScriptEditorControl surface) and declines
        /// rather than writing SQL into a window connected to the wrong SERVER; a same-server,
        /// different-database mismatch is fixed up safely with a leading <c>USE</c> — FK
        /// targets are always same-database as their parent table (SQL Server does not support
        /// cross-database FOREIGN KEY constraints), so this is never a guess.
        /// </summary>
        private async Task<QueryWindowOpenResult> TryOpenViaDteNewQueryAsync(
            string sql, Microsoft.Data.SqlClient.SqlConnectionStringBuilder targetCsb, Microsoft.Data.SqlClient.SqlConnection connection)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            EnvDTE.DTE dte;
            try
            {
                dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            }
            catch (Exception ex)
            {
                connection.Dispose();
                return QueryWindowOpenResult.Fail($"ServiceCache.ScriptFactory was unavailable, and the EnvDTE fallback also failed: could not get EnvDTE.DTE ({ex.GetType().Name}: {ex.Message}).");
            }
            if (dte == null)
            {
                connection.Dispose();
                return QueryWindowOpenResult.Fail("ServiceCache.ScriptFactory was unavailable, and the EnvDTE fallback also failed: EnvDTE.DTE service is not available in this session.");
            }

            // The connection we opened is only needed for the ScriptFactory route — the DTE
            // route inherits its own connection from whatever SSMS considers "current" and
            // never takes an explicit IDbConnection, so ours is not usable here.
            connection.Dispose();

            EnvDTE.Command chosen = null;
            var candidatesConsidered = new System.Collections.Generic.List<string>();
            try
            {
                foreach (EnvDTE.Command c in dte.Commands)
                {
                    string name;
                    try { name = c.Name; } catch (Exception) { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf("NewQuery", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("New Query", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    bool isAvailable;
                    try { isAvailable = c.IsAvailable; } catch (Exception) { isAvailable = false; }
                    candidatesConsidered.Add($"{name} (available={isAvailable})");
                    if (!isAvailable) continue;

                    // Prefer a command that explicitly mentions inheriting the current
                    // connection over a generic "new query" — the latter may prompt the user
                    // for a fresh connection instead of proceeding unattended.
                    if (chosen == null || name.IndexOf("CurrentConnection", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        chosen = c;
                    }
                }
            }
            catch (Exception ex)
            {
                return QueryWindowOpenResult.Fail($"ServiceCache.ScriptFactory was unavailable, and enumerating EnvDTE.DTE.Commands to find a fallback also failed ({ex.GetType().Name}: {ex.Message}).");
            }

            OeDiagnostics.Info($"'Go to source' EnvDTE fallback: candidates considered = [{string.Join(", ", candidatesConsidered)}]; chosen = {(chosen == null ? "none" : chosen.Name)}.");

            if (chosen == null)
            {
                return QueryWindowOpenResult.Fail(
                    "ServiceCache.ScriptFactory was unavailable, and no available EnvDTE command that opens a new query window could be found in this SSMS session.");
            }

            try
            {
                dte.ExecuteCommand(chosen.Name);
            }
            catch (Exception ex)
            {
                return QueryWindowOpenResult.Fail(
                    $"ServiceCache.ScriptFactory was unavailable; the EnvDTE fallback command '{chosen.Name}' also failed ({ex.GetType().Name}: {ex.Message}).");
            }

            var activeDocument = dte.ActiveDocument;
            if (activeDocument == null)
            {
                return QueryWindowOpenResult.Fail(
                    $"ServiceCache.ScriptFactory was unavailable; '{chosen.Name}' ran but no new active document appeared, so nothing could be pre-filled.");
            }

            // Read the new window's own connection back (public surface, same as
            // docs/resultsgrid-api.md §5) so we never silently write SQL into a window
            // connected to the wrong server.
            var editorControl = FindSqlScriptEditorControl(activeDocument);
            string effectiveSql = sql;
            if (editorControl?.Connection != null)
            {
                var actualServer = editorControl.Connection.ServerName;
                if (!string.Equals(actualServer, targetCsb.DataSource, StringComparison.OrdinalIgnoreCase))
                {
                    return QueryWindowOpenResult.Fail(
                        $"ServiceCache.ScriptFactory was unavailable; the EnvDTE fallback opened a window, but it is connected to '{actualServer}', not the target server '{targetCsb.DataSource}' — declined rather than risk running this against the wrong instance. Please connect the new window manually.");
                }

                var actualDatabase = editorControl.Connection.AdvancedOptions?["DATABASE"];
                if (!string.IsNullOrEmpty(targetCsb.InitialCatalog)
                    && !string.Equals(actualDatabase, targetCsb.InitialCatalog, StringComparison.OrdinalIgnoreCase))
                {
                    // Same server, different database — safe to fix up with USE: FK targets
                    // are always same-database as their parent table (SQL Server has no
                    // cross-database FOREIGN KEY constraint), so this is never a guess.
                    effectiveSql = $"USE {SsmsDataAnalyzer.Core.Sql.SqlIdentifier.Bracket(targetCsb.InitialCatalog)};\r\nGO\r\n{sql}";
                }
            }
            else
            {
                OeDiagnostics.Warn($"'Go to source' EnvDTE fallback: could not read back the new window's connection to verify server/database — proceeding, but this window's connection was not confirmed to match '{targetCsb.DataSource}/{targetCsb.InitialCatalog}'.");
            }

            if (!TryInsertSqlIntoDocument(activeDocument, effectiveSql, out var insertError))
            {
                return QueryWindowOpenResult.Fail(
                    $"ServiceCache.ScriptFactory was unavailable; '{chosen.Name}' opened a window, but writing the query into it failed: {insertError}");
            }

            var fallbackConfirmation =
                $"Opened a new query window via '{chosen.Name}' (ServiceCache.ScriptFactory was unavailable this session) — its connection was {(editorControl?.Connection != null ? "verified against" : "NOT verified against")} {targetCsb.DataSource}/{targetCsb.InitialCatalog}.";

            // Only auto-execute when the connection was actually VERIFIED against our target
            // above — an unverified connection is exactly the case TryAutoExecute's own
            // IsConnected check cannot protect against (it could be "connected", just to the
            // wrong place), so this path stays unexecuted rather than risk it.
            if (editorControl?.Connection != null)
            {
                fallbackConfirmation += TryAutoExecute(editorControl);
            }

            return QueryWindowOpenResult.Ok(fallbackConfirmation);
        }

        /// <summary>
        /// Walks from an EnvDTE.Document to the WinForms SqlScriptEditorControl hosting it, the
        /// same public type docs/resultsgrid-api.md §5 documents (<c>Connection</c>,
        /// <c>EditorText</c>). <c>Document.ActiveWindow.Object</c> / <c>.HWnd</c> are the only
        /// public hooks from EnvDTE into the underlying WinForms control; guarded because this
        /// bridge is not documented for this direction and must degrade to "connection not
        /// verified" rather than throw.
        /// </summary>
        /// <summary>
        /// Writes <paramref name="sql"/> into <paramref name="document"/>'s full text, via
        /// EnvDTE.TextSelection — the SAME mechanism whether the document came from
        /// CreateNewBlankScript (ServiceCache route) or from the EnvDTE-command fallback, so
        /// there is exactly one place this logic lives. Never throws; reports a reason instead.
        /// </summary>
        /// <summary>
        /// User request, v0.5.4: "auto execute using connection string from session where is
        /// clicked Go to source". Gated by Options ("Automatically execute the generated
        /// query", default ON — <see cref="OptionsAccessor.GetAutoExecuteGoToSourceQuery"/>).
        ///
        /// Prefers the direct control method over discovering a DTE command: we already have
        /// the live <see cref="SqlScriptEditorControl"/> reference (used moments earlier to
        /// verify the connection), and <c>ScriptAndResultsEditorControl.OnExecScript(object,
        /// EventArgs)</c> — reflected, since it is internal — is the actual handler SSMS wires
        /// to its own Execute toolbar button/F5 (confirmed by its signature matching a
        /// standard WinForms Click handler and by its name), so invoking it replicates a real
        /// user click exactly, including whatever pre-execution bookkeeping (status bar,
        /// IsExecuting flag, etc.) a lower-level primitive like the also-internal
        /// <c>DoScriptExec(ITextSpan)</c> would leave for the caller to redo. Returns a
        /// human-readable SUFFIX to append to the "opened a new query window" message — never
        /// throws, and never invokes anything unless <see cref="ScriptAndResultsEditorControl.
        /// IsConnected"/> is already true, specifically so this can never be the thing that
        /// pops SSMS's own connect dialog at the user (worse than just not executing).
        /// </summary>
        /// <summary>
        /// v0.6.1 field report: the new window opened, SQL landed correctly, but the editor
        /// showed "Disconnected." — no session, no SPID, not merely a different connection.
        /// Decompiled IL traces this precisely:
        /// <c>ScriptAndResultsEditorControl.IsConnected</c> (get) is PURELY
        /// <c>m_connection != null &amp;&amp; m_connection.State == ConnectionState.Open</c> —
        /// its setter is a no-op (<c>ret</c>, verified from IL — nothing else can flip it).
        /// <c>CreateNewScript</c>'s own internal path is supposed to populate
        /// <c>m_connection</c> via an event (<c>ScriptEditorControl.NewScriptEditor</c> ->
        /// <c>ScriptFactory.OnNewScriptEditorForConnectionStamp</c> ->
        /// <c>ISqlScriptWindowWithConnection.SetConnection(ci, liveCon)</c>), but that whole
        /// chain is undocumented, event-based, and was evidently not completing reliably for
        /// our hand-built <c>UIConnectionInfo</c> (missing <c>ServerType</c> — required by
        /// CreateNewScript's own validation when a live connection is supplied — is the
        /// leading suspect, now fixed at the call site above).
        ///
        /// Rather than keep trusting that internal chain, this calls the SAME method IT calls
        /// — <c>SetConnection(UIConnectionInfo, IDbConnection)</c> — but PUBLICLY and
        /// DIRECTLY, ourselves, exactly once, only if the window is not already connected
        /// (SetConnection throws InvalidOperationException if called while already connected —
        /// verified from IL — so IsConnected is checked first, both to avoid that and because
        /// re-stamping a window that already connected correctly would be pointless). This is
        /// the "connect an already-open window explicitly" route the lead asked about — a
        /// real, public, supported-shape API on the control itself, not a guess.
        /// </summary>
        private static string EnsureConnected(
            Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl editorControl,
            Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo uiConnectionInfo,
            Microsoft.Data.SqlClient.SqlConnection connection)
        {
            if (editorControl == null)
            {
                return " Could not verify the new window's connection state (no editor control found).";
            }
            if (editorControl.IsConnected)
            {
                return string.Empty; // CreateNewBlankScript's own internal stamping already worked.
            }

            try
            {
                editorControl.SetConnection(uiConnectionInfo, connection);
                OeDiagnostics.Info("'Go to source': CreateNewBlankScript's internal connection stamping did not leave the window connected — connected it explicitly via SetConnection.");
                return editorControl.IsConnected ? string.Empty : " Connected explicitly, but the window still does not report as connected.";
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("'Go to source': explicit SetConnection also failed", ex);
                return $" The new window is not connected ({ex.GetType().Name}: {ex.Message}).";
            }
        }

        private static string TryAutoExecute(Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl editorControl)
        {
            if (!OptionsAccessor.GetAutoExecuteGoToSourceQuery())
            {
                return string.Empty; // feature is off — leave the "Opened a new query window..." message as-is.
            }

            if (editorControl == null)
            {
                return " Left unexecuted: could not reach the new window's editor control to check its connection.";
            }

            if (!editorControl.IsConnected || editorControl.Connection == null)
            {
                return " Left unexecuted: the new window is not connected yet.";
            }

            try
            {
                var method = typeof(Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptAndResultsEditorControl)
                    .GetMethod("OnExecScript", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                {
                    return " Left unexecuted: could not find an execute method on the editor control.";
                }
                method.Invoke(editorControl, new object[] { editorControl, EventArgs.Empty });
                return " Executed automatically.";
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException ? ex.InnerException ?? ex : ex;
                OeDiagnostics.Error("'Go to source' auto-execute failed", real);
                return $" Left unexecuted: auto-execute failed ({real.GetType().Name}: {real.Message}).";
            }
        }

        private static bool TryInsertSqlIntoDocument(EnvDTE.Document document, string sql, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            try
            {
                var selection = document.Selection as EnvDTE.TextSelection;
                if (selection == null)
                {
                    error = "the active document has no editable text selection.";
                    return false;
                }
                selection.SelectAll();
                selection.Text = sql;
                selection.StartOfDocument();
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl FindSqlScriptEditorControl(EnvDTE.Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var window = document.ActiveWindow;
                if (window == null) return null;

                if (window.Object is Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl direct)
                {
                    return direct;
                }

                // EnvDTE.Window.HWnd is IntPtr on this SSMS's re-versioned EnvDTE PIA (the
                // classic EnvDTE interface has it as int) — read it as whatever it actually is
                // rather than assuming either shape.
                object rawHwnd = window.HWnd;
                IntPtr hwnd = rawHwnd is IntPtr ip ? ip : new IntPtr(Convert.ToInt64(rawHwnd));
                if (hwnd == IntPtr.Zero) return null;

                var control = System.Windows.Forms.Control.FromHandle(hwnd);
                var cursor = control;
                while (cursor != null)
                {
                    if (cursor is Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl sse) return sse;
                    cursor = cursor.Parent;
                }
                return null;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn($"'Go to source' EnvDTE fallback: could not walk to the SqlScriptEditorControl ({ex.GetType().Name}: {ex.Message}) — connection will not be verified.");
                return null;
            }
        }

        /// <summary>
        /// docs/oe-api.md's binding guidance: Tier A uses public-but-unsupported SSMS API, so
        /// its whole activation is one feature flag plus one try/catch that degrades to
        /// Tier B (the "Analyze Data..." Tools menu entry, wired up above unconditionally) —
        /// never a partial failure that leaves the package in a broken state.
        /// </summary>
        private void InitializeObjectExplorerIntegration()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var options = (DataAnalyzerOptionsPage)GetDialogPage(typeof(DataAnalyzerOptionsPage));
                if (options != null && !options.EnableObjectExplorerIntegration)
                {
                    OeDiagnostics.Info("Object Explorer integration is OFF (Tools > Options > SSMS Data Analyzer > EnableObjectExplorerIntegration). Right-click 'Analyze Data' will not appear; use the Tools menu entry instead.");
                    return;
                }

                _oeBridge = new OeContextBridge(this, OnAnalyzeObjectExplorerNode);
                OeDiagnostics.Info("Object Explorer integration initialized: hooked IContextService.ObjectExplorerContext.CurrentContextChanged. Right-click 'Analyze Data' should appear on table nodes.");
            }
            catch (Exception ex)
            {
                // Any failure here (missing service, an SSMS update that changed the
                // undocumented shape docs/oe-api.md relies on, etc.) means Tier A simply
                // isn't available this session. Tier B remains fully functional regardless —
                // this must never prevent the package itself from finishing InitializeAsync.
                // Logged (not swallowed silently) so a report from the user is diagnosable
                // instead of "nothing happened, don't know why."
                OeDiagnostics.Error("Object Explorer integration failed to initialize; falling back to the Tools menu entry point only", ex);
                _oeBridge = null;
            }
        }

        /// <summary>
        /// CONTRACT.md Amendment 13 hardening: multiple independent ways to reach the visible
        /// window's ViewModel, tried in order, so a shape surprise in any single one of them
        /// (the exact class of bug this feature has already hit twice — the checkbox binding
        /// and the OE menu retargeting) degrades to a fallback instead of silently opening a
        /// window nothing gets written to. Returns null only if none of them work, which is
        /// itself logged by the caller.
        /// </summary>
        private ProfileViewModel TryResolveViewModel(WindowPane pane, out string resolutionPath)
        {
            // 1. Primary: the pane's own authoritative accessor (set once at construction,
            //    never re-derived from Content's runtime type).
            if (pane is ProfileToolWindow toolWindow && toolWindow.ViewModel != null)
            {
                resolutionPath = "ProfileToolWindow.ViewModel";
                return toolWindow.ViewModel;
            }

            // 2. Fallback: Content as ProfileView (what the code originally did).
            if (pane?.Content is ProfileView view && view.ViewModel != null)
            {
                resolutionPath = "pane.Content as ProfileView";
                return view.ViewModel;
            }

            // 3. Fallback: walk the visual tree under Content for anything whose DataContext
            //    is a ProfileViewModel, in case Content is wrapped by chrome we did not expect
            //    (docking adorners, a Border, etc.) rather than being the ProfileView directly.
            if (pane?.Content is DependencyObject root)
            {
                var found = FindDataContext(root);
                if (found != null)
                {
                    resolutionPath = "visual-tree walk under pane.Content";
                    return found;
                }
            }

            resolutionPath = "none";
            return null;
        }

        private static ProfileViewModel FindDataContext(DependencyObject root)
        {
            if (root is FrameworkElement fe && fe.DataContext is ProfileViewModel vm) return vm;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindDataContext(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Invoked (via AnalyzeMenuHandler's click handler) when the user picks "Analyze
        /// Data..." on a table node. Reuses the node's own connection when possible
        /// (CONTRACT.md Amendment 13, Priority 1); when it can't be reused automatically
        /// (currently: Entra/token-based connections — see OeTableInfo), prefills the target
        /// and leaves auth to the standalone picker (Priority 2) instead of guessing.
        /// </summary>
        private void OnAnalyzeObjectExplorerNode(INodeInformation node)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                // v0.5.2 field report ("no more work" — a right-click that visibly did
                // nothing, window left on its default empty state): open/resolve the
                // ViewModel FIRST, before anything that can fail (parsing the node, building
                // a connection string) — so every failure past this point has somewhere to
                // report to (ReportObjectExplorerFailure), rather than only the ActivityLog.
                // The one thing that genuinely CANNOT be reported into the window is failing
                // to resolve the ViewModel itself — there is nothing to write into.
                WindowPane pane;
                try
                {
                    pane = await ShowToolWindowAsync(typeof(ProfileToolWindow), 0, true, DisposalToken)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    OeDiagnostics.Error("'Analyze Data' failed to open/show the tool window", ex);
                    return;
                }

                var viewModel = TryResolveViewModel(pane, out string resolutionPath);
                if (viewModel == null)
                {
                    // CONTRACT.md Amendment 13 hardening: this is exactly the failure mode the
                    // lead flagged as indistinguishable from "nothing happened" — the window is
                    // already visible (ShowToolWindowAsync succeeded) but we could not reach its
                    // ViewModel to tell it anything. We cannot write a status into a ViewModel we
                    // do not have, so this is the practical limit of what we can surface from
                    // here; logged loudly (not just Warn) so it stops looking identical to "OE
                    // never fired" in the ActivityLog.
                    OeDiagnostics.Error($"'Analyze Data' opened the tool window but could not resolve its ViewModel through any known path ({resolutionPath}) — the window is visible but nothing can be shown in it. This means ShowToolWindowAsync returned a pane whose Content/DataContext shape did not match ProfileToolWindow's own construction, which should not be possible — report this exact log line.");
                    return;
                }
                if (resolutionPath != "ProfileToolWindow.ViewModel")
                {
                    // The primary path (pane as ProfileToolWindow -> .ViewModel) failed and a
                    // fallback caught it. That should never happen either — worth knowing about
                    // even though we recovered.
                    OeDiagnostics.Warn($"'Analyze Data' resolved the ViewModel via fallback path '{resolutionPath}' instead of the primary ProfileToolWindow.ViewModel accessor — recovered, but this indicates the pane was not a plain ProfileToolWindow with our own Content, which is unexpected.");
                }

                try
                {
                    if (!OeTableInfo.TryParseTableRef(node, out TableRef table))
                    {
                        OeDiagnostics.Warn($"'Analyze Data' was clicked but the node's URN could not be parsed into a table (Context='{node?.Context}', UrnPath='{node?.UrnPath}').");
                        viewModel.ReportObjectExplorerFailure("could not identify a table from the Object Explorer selection. Try clicking directly on the table node again.");
                        return;
                    }

                    if (OeTableInfo.TryBuildConnectionString(node, table.Database, out string connectionString, out _))
                    {
                        OeDiagnostics.Info($"'Analyze Data' opened {table.Schema}.{table.Name} on {table.Server}/{table.Database} using Object Explorer's own connection.");
                        viewModel.LoadFromObjectExplorer(table, connectionString);
                    }
                    else
                    {
                        OeDiagnostics.Info($"'Analyze Data' opened {table.Schema}.{table.Name} on {table.Server}/{table.Database} but could not reuse Object Explorer's connection (likely Entra/token-based) — prefilled target only, user must pick an auth method.");
                        viewModel.PrefillFromObjectExplorer(table);
                    }
                }
                catch (Exception ex)
                {
                    // We DO have a live ViewModel at this point (the whole point of the
                    // reordering above) — never let this vanish into FileAndForget's
                    // ActivityLog-only handling the way the tool-window "Go to source" bug did.
                    OeDiagnostics.Error("'Analyze Data' failed after the tool window was already open", ex);
                    viewModel.ReportObjectExplorerFailure(ex.Message);
                }
            }).FileAndForget("SsmsDataAnalyzer/OeContextBridge/AnalyzeNode");
        }

        /// <summary>
        /// Called by the shell (via ToolWindowProvider) to create the tool window's pane.
        /// Must run on the UI thread; AsyncPackage.ShowToolWindowAsync already guarantees that
        /// for callers that go through it.
        /// </summary>
        protected override WindowPane InstantiateToolWindow(Type toolWindowType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return base.InstantiateToolWindow(toolWindowType);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _oeBridge?.Dispose();
                _oeBridge = null;
            }
            base.Dispose(disposing);
        }
    }
}
