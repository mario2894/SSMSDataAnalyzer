using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SsmsDataAnalyzer.Core;
using SsmsDataAnalyzer.Core.Export;
using SsmsDataAnalyzer.Core.Metadata;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.Options;

namespace SsmsDataAnalyzer.Vsix.ToolWindow
{
    /// <summary>
    /// Drives a single profiling run against Core's <see cref="ITableProfiler"/>.
    ///
    /// Threading contract (see CONTRACT.md + VS threading rules):
    /// - <see cref="RunAsync"/> itself runs on a background thread; the profiler's SQL work
    ///   never touches the UI thread.
    /// - Progress callbacks arrive on whatever thread the profiler reports from, so every
    ///   mutation of bound state is marshalled back to the UI thread before touching
    ///   <see cref="Rows"/> or any other UI-bound property.
    /// - Cancel requests <see cref="CancellationTokenSource.Cancel"/> and nothing else -
    ///   the profiler is responsible (per CONTRACT.md) for returning the partial
    ///   <see cref="TableProfile"/> built so far rather than throwing the result away.
    ///
    /// Connection handling (CONTRACT.md Amendment 13, latest revision — Object Explorer is
    /// now the ONLY entry point, per the user's explicit request: "i dont want possability to
    /// analyze anything outside of object explorer"):
    /// - <see cref="LoadFromObjectExplorer"/> is called by the Object Explorer bridge
    ///   (ObjectExplorer/OeContextBridge.cs, via DataAnalyzerPackage) with a connection string
    ///   already built from the clicked node's own SqlConnectionInfo - whatever auth SSMS
    ///   itself used (Windows, SQL login, Entra, MFA) just works, because we never re-derive
    ///   it or ask the user to.
    /// - The manual-entry / explicit-auth-picker UI (server/database/schema/table textboxes,
    ///   Windows/SQL-login/Entra picker, username, password, TrustServerCertificate checkbox)
    ///   has been REMOVED — not disabled, removed — at the user's request. There is now no
    ///   code path in this class that can ever ask for or hold a password: no field, no
    ///   property, nothing to remove-with-care later. If a standalone entry point is ever
    ///   reintroduced, it needs its own credential-handling review against CONTRACT.md
    ///   Amendment 13's rules; do not casually resurrect AuthMode/SetPassword from history.
    /// - <see cref="PrefillFromObjectExplorer"/> is the fallback for connections that cannot
    ///   be reused (currently: Entra/token-based - see OeTableInfo.TryBuildConnectionString).
    ///   With no auth picker to fall back to, this must not leave the user stuck: it shows an
    ///   explicit "connection unavailable" message and Run stays disabled (see
    ///   <see cref="CanRun"/>) rather than attempting a connection string we know is wrong.
    /// </summary>
    public sealed class ProfileViewModel : ObservableObject, IDisposable
    {
        private readonly ITableProfiler _profiler;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        /// <summary>
        /// Exposes the same instance JoinableTaskFactory this view model already uses
        /// internally (never the ambient ThreadHelper.JoinableTaskFactory — VSTHRD012 wants an
        /// explicit instance) so ProfileView.xaml.cs's code-behind can post view-only work
        /// (e.g. focusing a control after a Visibility binding applies) back to the UI thread
        /// through the same JTF, instead of reaching for Dispatcher.BeginInvoke or a second,
        /// ambient JTF of its own.
        /// </summary>
        public JoinableTaskFactory JoinableTaskFactory => _joinableTaskFactory;

        private CancellationTokenSource _cts;
        private string _server;
        private string _database;
        private string _schema;
        private string _table;
        // Blank until a run starts/completes: the same guidance text already appears
        // prominently in HeaderSummary when no table has been picked yet (see its getter
        // below), so this line would otherwise just repeat it.
        private string _statusMessage = "";
        private bool _isRunning;
        private bool _isIndeterminate;
        private double _progressPercent;
        private string _dateCreatedColumn;
        private long _totalRows;
        private TimeSpan _elapsed;
        private bool _wasSampled;

        // The ONLY source of a connection now (Object Explorer). Null until
        // LoadFromObjectExplorer runs; RunAsync/CanRun refuse to proceed without it, so there
        // is no path left that guesses at or reconstructs a connection string.
        private string _externalConnectionString;

        public ProfileViewModel() : this(new TableProfiler(), ThreadHelper.JoinableTaskFactory)
        {
        }

        public ProfileViewModel(ITableProfiler profiler, JoinableTaskFactory joinableTaskFactory)
        {
            _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
            _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));

            Rows = new ObservableCollection<ColumnProfileRow>();
            GridSearch = new GridSearchViewModel(Rows);

            RunCommand = new RelayCommand(_ => StartRun(), _ => CanRun());
            CancelCommand = new RelayCommand(_ => CancelRun(), _ => IsRunning);

            // PLAN.md's tool-window UX always included this, and it doubles as the way to see
            // the profiled data independently of the DataGrid (useful if a rendering-layer bug
            // and a data-layer bug ever need telling apart again).
            CopyMarkdownCommand = new RelayCommand(_ => CopyToClipboard(MarkdownExporter.Export(_lastProfile)), _ => _lastProfile != null);
            CopyCsvCommand = new RelayCommand(_ => CopyToClipboard(CsvExporter.Export(_lastProfile)), _ => _lastProfile != null);
        }

        public ObservableCollection<ColumnProfileRow> Rows { get; }

        /// <summary>Find-in-grid (Ctrl+F / right-click "Find..." on the grid).</summary>
        public GridSearchViewModel GridSearch { get; }

        public RelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand CopyMarkdownCommand { get; }
        public RelayCommand CopyCsvCommand { get; }

        // Snapshot of the most recently applied result (partial or complete) — export/copy
        // reads this directly rather than reconstructing a TableProfile from Rows, so it is
        // always exactly what Core produced.
        private TableProfile _lastProfile;

        // Server/Database/Schema/Table are read-only to the outside world now — the only
        // writers are LoadFromObjectExplorer and PrefillFromObjectExplorer, both driven by
        // Object Explorer. No UI binds to these as editable; the compact header displays them
        // via HeaderSummary.
        public string Server { get => _server; private set => SetProperty(ref _server, value); }
        public string Database { get => _database; private set => SetProperty(ref _database, value); }
        public string Schema { get => _schema; private set => SetProperty(ref _schema, value); }
        public string Table { get => _table; private set => SetProperty(ref _table, value); }

        public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
        public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
        public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }
        public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
        public string DateCreatedColumn { get => _dateCreatedColumn; private set => SetProperty(ref _dateCreatedColumn, value); }
        public long TotalRows { get => _totalRows; private set => SetProperty(ref _totalRows, value); }
        public TimeSpan Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
        public bool WasSampled { get => _wasSampled; private set => SetProperty(ref _wasSampled, value); }

        /// <summary>Whether a real, reusable connection was inherited from Object Explorer (vs. prefilled-only — see PrefillFromObjectExplorer).</summary>
        public bool IsUsingInheritedConnection => _externalConnectionString != null;

        /// <summary>
        /// The single compact header line: table identity, row count, connection source, and
        /// (space permitting) DateCreated column / flag summary / elapsed time. Deliberately
        /// ONE property producing ONE string, with the least-important pieces appended LAST —
        /// TextTrimming="CharacterEllipsis" in the view then drops elapsed first, then the
        /// flag summary, then DateCreated, before ever touching table identity or row count,
        /// without any custom responsive-layout code.
        /// </summary>
        public string HeaderSummary
        {
            get
            {
                if (string.IsNullOrEmpty(Table))
                {
                    return "Right-click a table in Object Explorer and choose \"Analyze Data...\".";
                }

                var sb = new StringBuilder();
                sb.Append(Server).Append(" / ").Append(Database).Append(" / ").Append(Schema).Append('.').Append(Table);

                if (Rows.Count > 0)
                {
                    sb.Append("  —  ").Append(TotalRows.ToString("N0")).Append(" rows");
                    if (WasSampled)
                    {
                        // Pre-existing indicator (was a separate "Sampled" badge in the old
                        // footer), folded in here rather than dropped — it matters for
                        // correctness, not just presentation: CONTRACT.md forbids showing a
                        // guessed distinct count on sampled data, so the user needs to know
                        // why Distinct is blank on every row, not just per-cell.
                        sb.Append(" (sampled)");
                    }
                }

                sb.Append(IsUsingInheritedConnection
                    ? "  ·  via Object Explorer"
                    : "  ·  connection unavailable — reconnect in Object Explorer with a reusable sign-in, then choose Analyze Data again");

                if (!string.IsNullOrEmpty(DateCreatedColumn) && DateCreatedColumn != "n/a")
                {
                    sb.Append("  ·  DateCreated: ").Append(DateCreatedColumn);
                }

                var flagSummary = ColumnFlagSummary;
                if (!string.IsNullOrEmpty(flagSummary))
                {
                    sb.Append("  ·  ").Append(flagSummary);
                }

                if (Elapsed > TimeSpan.Zero)
                {
                    sb.Append("  ·  ").Append(Elapsed.TotalSeconds.ToString("0.0")).Append("s");
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// "57 columns — 3 dead, 8 sparse, 2 constant, 1 unique" — derived entirely from
        /// ColumnFlag values Core already computes (CONTRACT.md's ColumnProfile.Flags). No new
        /// query, no new engine work: this answers "is this table full of unused columns?"
        /// without scanning every row by hand.
        /// </summary>
        private string ColumnFlagSummary
        {
            get
            {
                if (Rows.Count == 0) return null;

                int dead = 0, sparse = 0, constant = 0, unique = 0;
                foreach (var row in Rows)
                {
                    var flags = row.Flags;
                    if ((flags & ColumnFlag.Dead) != 0) dead++;
                    if ((flags & ColumnFlag.Sparse) != 0) sparse++;
                    if ((flags & ColumnFlag.Constant) != 0) constant++;
                    if ((flags & ColumnFlag.Unique) != 0) unique++;
                }

                if (dead == 0 && sparse == 0 && constant == 0 && unique == 0)
                {
                    return $"{Rows.Count} columns";
                }

                var parts = new System.Collections.Generic.List<string>();
                if (dead > 0) parts.Add($"{dead} dead");
                if (sparse > 0) parts.Add($"{sparse} sparse");
                if (constant > 0) parts.Add($"{constant} constant");
                if (unique > 0) parts.Add($"{unique} unique");

                return $"{Rows.Count} columns — {string.Join(", ", parts)}";
            }
        }

        /// <summary>
        /// CONTRACT.md Amendment 13, the real fix: called by the Object Explorer bridge
        /// (ObjectExplorer/OeContextBridge.cs) with a connection string already built from
        /// the clicked node's own SqlConnectionInfo - whatever auth SSMS itself used for that
        /// connection (Windows, SQL login, Entra, MFA) is reused verbatim; we never see or
        /// re-derive the credential. <paramref name="connectionString"/> must already have
        /// been built without embedding it anywhere logged.
        /// </summary>
        public void LoadFromObjectExplorer(TableRef table, string connectionString)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (string.IsNullOrEmpty(connectionString)) throw new ArgumentNullException(nameof(connectionString));

            Server = table.Server;
            Database = table.Database;
            Schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema;
            Table = table.Name;
            _externalConnectionString = connectionString;
            OnPropertyChanged(nameof(IsUsingInheritedConnection));
            OnPropertyChanged(nameof(HeaderSummary));
            StatusMessage = "Connected via Object Explorer - using its existing connection.";

            StartRun();
        }

        /// <summary>
        /// Fallback: the clicked Object Explorer node identified a table but its connection
        /// couldn't be reused automatically (docs/oe-api.md section 4.1 - currently
        /// Entra/token-based connections). With no auth picker to hand the user, this must not
        /// leave them stuck on a broken/blank window: it shows the table identity so they know
        /// the click WAS received, states plainly that the connection could not be reused, and
        /// leaves Run disabled (see CanRun) rather than attempting a connection string we
        /// already know is wrong.
        /// </summary>
        public void PrefillFromObjectExplorer(TableRef table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));

            Server = table.Server;
            Database = table.Database;
            Schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema;
            Table = table.Name;
            _externalConnectionString = null;
            OnPropertyChanged(nameof(IsUsingInheritedConnection));
            OnPropertyChanged(nameof(HeaderSummary));
            StatusMessage = $"Object Explorer's connection to {Schema}.{Table} could not be reused automatically (likely an Entra/token-based sign-in). Reconnect in Object Explorer using a reusable authentication mode, then choose Analyze Data again.";
        }

        /// <summary>
        /// v0.5.2 field report ("no more work" — a right-click that visibly did nothing):
        /// the package's Object Explorer bridge (DataAnalyzerPackage.OnAnalyzeObjectExplorerNode)
        /// now opens/resolves this ViewModel FIRST, before attempting anything that can fail
        /// (parsing the clicked node, building a connection string), specifically so any such
        /// failure has somewhere to report to instead of leaving the window's default empty
        /// state indistinguishable from "nothing was clicked yet." Never throws; only ever
        /// called from a guarded context.
        /// </summary>
        public void ReportObjectExplorerFailure(string reason)
        {
            StatusMessage = $"'Analyze Data' click did not complete: {reason}";
        }

        private bool CanRun() =>
            !IsRunning
            && _externalConnectionString != null
            && !string.IsNullOrWhiteSpace(Table);

        private void StartRun()
        {
            // Fire-and-forget onto the JTF from a UI-thread command invocation; the awaited
            // work below is what actually leaves the UI thread.
            _joinableTaskFactory.RunAsync(RunAsync).FileAndForget("SsmsDataAnalyzer/ProfileViewModel/Run");
        }

        public async Task RunAsync()
        {
            // Invoked via JoinableTaskFactory.RunAsync from a WPF command callback (already
            // on the UI thread), but VSTHRD109 flags asserting-then-throwing inside an async
            // method - switch explicitly instead, which is correct regardless of caller.
            await _joinableTaskFactory.SwitchToMainThreadAsync();

            if (_externalConnectionString == null)
            {
                // Should be unreachable — RunCommand's CanExecute (CanRun) already requires
                // this — but RunAsync is also called directly (e.g. by harnesses, or a future
                // caller), so refuse loudly rather than silently attempting a null connection.
                StatusMessage = "No connection available yet — right-click a table in Object Explorer and choose Analyze Data.";
                return;
            }

            var table = new TableRef
            {
                Server = Server,
                Database = Database,
                Schema = string.IsNullOrWhiteSpace(Schema) ? "dbo" : Schema,
                Name = Table
            };

            // The only connection source now is what Object Explorer handed us. Built once
            // per run and never stored again after this point; it is a local, passed straight
            // into Core, and goes out of scope when RunAsync returns. Nothing here logs it,
            // includes it in StatusMessage, or returns it to a caller.
            var connectionString = _externalConnectionString;

            // Read fresh from Tools > Options on every run (not cached on the ViewModel) so a
            // change takes effect on the very next run without restarting SSMS.
            var options = OptionsAccessor.GetCurrent();

            // IsRunning flips true HERE — synchronously, before the guardrail's own await —
            // not after it. The guardrail below does real (if cheap) async I/O; leaving
            // IsRunning false until it finishes would open a re-entrancy window where CanRun()
            // still permits a second click to start a second, concurrent run during the
            // preflight, and would leave the UI showing no "busy" feedback while a real
            // network round-trip is in flight. A harness that polls IsRunning to know when a
            // run has finished caught this ordering bug directly (it deadlocked mid-guardrail
            // when IsRunning was set only after the guardrail returned).
            Rows.Clear();
            _lastProfile = null;
            IsRunning = true;
            IsIndeterminate = true;
            ProgressPercent = 0;
            StatusMessage = "Checking table size...";
            WasSampled = false;
            RelayCommand.RaiseCanExecuteChangedForAll();

            // Large-table guardrail: a cheap, catalog-only metadata read (never touches the
            // table's data) states the REAL cost — row estimate and the actual distinct-pass
            // plan Core would run — rather than a guess, and lets the user back out before a
            // single expensive query is issued. Below the threshold this returns true
            // immediately with no I/O and no prompt, so the common case stays frictionless.
            if (!await ConfirmLargeTableIfNeededAsync(table, connectionString, options).ConfigureAwait(true))
            {
                IsRunning = false;
                IsIndeterminate = false;
                StatusMessage = "Cancelled — table is above the configured large-table threshold.";
                RelayCommand.RaiseCanExecuteChangedForAll();
                return;
            }

            StatusMessage = "Reading metadata...";

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var progress = new Progress<ProfileProgress>(OnProgressUiThread);
            var stopwatch = Stopwatch.StartNew();

            // Run the profiling work on a threadpool thread; RunAsync/StartRun are invoked
            // from the UI thread (command execution), but ProfileAsync must not run there -
            // it issues blocking ADO.NET calls under the hood. Progress callbacks captured
            // the UI SynchronizationContext above (via Progress<T>'s ctor), so they still
            // marshal back correctly from inside Task.Run.
            TableProfile result = null;
            string error = null;
            try
            {
                result = await Task.Run(
                    () => _profiler.ProfileAsync(connectionString, table, options, progress, _cts.Token),
                    _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Credential rule: surface ONLY ex.Message. ADO.NET/SqlClient exceptions do
                // not embed the connection string or password in .Message, but we deliberately
                // never touch `connectionString` here at all - not even to redact it - so a
                // future edit to this catch block cannot accidentally start leaking it.
                error = ex.Message;
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            stopwatch.Stop();
            Elapsed = stopwatch.Elapsed;
            IsRunning = false;
            IsIndeterminate = false;
            OnPropertyChanged(nameof(HeaderSummary));

            if (error != null)
            {
                StatusMessage = $"Failed: {error}";
                return;
            }

            if (result != null)
            {
                ApplySnapshot(result);
                StatusMessage = _cts.IsCancellationRequested
                    ? $"Cancelled - showing partial results ({Rows.Count} columns)."
                    : $"Done - {Rows.Count} columns profiled in {Elapsed.TotalSeconds:0.0}s.";

                if (!_cts.IsCancellationRequested)
                {
                    AssertRowInvariant(result);
                }
            }
        }

        /// <summary>
        /// CONTRACT.md's large-table guardrail: LargeTableThreshold (Tools > Options) was
        /// wired up but never read — a 50M-row table would launch exact COUNT(DISTINCT)
        /// passes with zero warning, which is the most expensive thing this tool can do.
        /// Reads pass-0 metadata only (SchemaReader — catalog-only, never touches the table's
        /// data pages) off the UI thread, and if the estimate exceeds the threshold, states
        /// the REAL cost via DistinctPlanner's actual plan (not a guess) and lets the user
        /// back out. Returns true immediately, with no I/O, when below the threshold — the
        /// common case stays frictionless.
        /// </summary>
        private async Task<bool> ConfirmLargeTableIfNeededAsync(TableRef table, string connectionString, ProfileOptions options)
        {
            TableSchema schema;
            try
            {
                // Never block the UI thread on metadata I/O, exactly like the real profiling
                // run below — a short-lived, dedicated token so a slow/unreachable server
                // can't hang the preflight indefinitely.
                using (var preflightCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(30, options.QueryTimeoutSeconds))))
                {
                    schema = await Task.Run(async () =>
                    {
                        using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
                        {
                            await conn.OpenAsync(preflightCts.Token).ConfigureAwait(false);
                            return await new SchemaReader().ReadAsync(conn, table, options, preflightCts.Token).ConfigureAwait(false);
                        }
                    }, preflightCts.Token).ConfigureAwait(true);
                }
            }
            catch (Exception)
            {
                // This preflight is a courtesy, not a requirement for correctness — if
                // metadata can't be read here (permissions, a transient network blip), let
                // the real run surface the actual error instead of blocking the user on a
                // guardrail that isn't the point of their click.
                return true;
            }

            if (schema.EstimatedRows <= options.LargeTableThreshold)
            {
                return true;
            }

            // Cheap, in-memory, no I/O — the exact plan Core would run, so the preview states
            // the real cost rather than a guess.
            var plan = DistinctPlanner.Plan(table, schema.Columns, options);
            int indexBacked = plan.Queries.Count(q => q.Kind == DistinctQueryKind.IndexBacked);
            int batched = plan.Queries.Count(q => q.Kind == DistinctQueryKind.Batched);
            int lob = plan.Queries.Count(q => q.Kind == DistinctQueryKind.Lob);

            var message =
                $"{table.Schema}.{table.Name} has an estimated {schema.EstimatedRows:N0} rows " +
                $"(configured large-table threshold: {options.LargeTableThreshold:N0}).\n\n" +
                $"Exact distinct counts will run {plan.TotalQueries} " +
                $"{(plan.TotalQueries == 1 ? "query" : "queries")} " +
                $"({indexBacked} index-backed, {batched} batched, {lob} LOB), each with a " +
                $"{options.QueryTimeoutSeconds}s timeout.\n\n" +
                "Continue?";

            var result = MessageBox.Show(message, "Large table — Analyze Data",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }

        private void CancelRun()
        {
            _cts?.Cancel();
            StatusMessage = "Cancelling... partial results will be kept.";
        }

        /// <summary>
        /// Progress callback. <see cref="Progress{T}"/> already marshals back to the
        /// SynchronizationContext captured when it was constructed (the UI thread, since we
        /// construct it inside <see cref="RunAsync"/> before the first UI-thread await), but
        /// we assert the thread explicitly since correctness here matters more than the
        /// convenience of trusting that capture silently.
        /// </summary>
        private void OnProgressUiThread(ProfileProgress p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (p == null) return;

            IsIndeterminate = p.TotalUnits <= 0;
            if (p.TotalUnits > 0)
            {
                ProgressPercent = Math.Min(100.0, 100.0 * p.CompletedUnits / p.TotalUnits);
            }

            StatusMessage = string.IsNullOrEmpty(p.CurrentDetail)
                ? $"{p.Stage} ({p.CompletedUnits}/{Math.Max(p.TotalUnits, 1)})"
                : $"{p.Stage}: {p.CurrentDetail}";

            if (p.Snapshot != null)
            {
                ApplySnapshot(p.Snapshot);
            }
        }

        private void ApplySnapshot(TableProfile snapshot)
        {
            _lastProfile = snapshot;
            RelayCommand.RaiseCanExecuteChangedForAll();

            TotalRows = snapshot.TotalRows;
            DateCreatedColumn = snapshot.DateCreatedColumn ?? "n/a";
            WasSampled = snapshot.WasSampled;

            var incoming = snapshot.Columns ?? Array.Empty<ColumnProfile>().ToList();

            // Matched by ColumnMeta.ColumnId (a stable ordinal, part of CONTRACT.md), not by
            // Name: a name-based match would silently misbehave for any progressive snapshot
            // entry whose Meta or Name was null. Core never actually produces that today
            // (verified by a headless harness), but ColumnId is the correct match key
            // regardless and costs nothing to use.
            for (int i = 0; i < incoming.Count; i++)
            {
                var col = incoming[i];
                var columnId = col.Meta?.ColumnId;
                var existing = columnId.HasValue
                    ? Rows.FirstOrDefault(r => r.ColumnId == columnId.Value)
                    : null;
                if (existing != null)
                {
                    existing.FilledCountBase = snapshot.TotalRows;
                    existing.Apply(col);
                }
                else
                {
                    var row = new ColumnProfileRow(col) { FilledCountBase = snapshot.TotalRows };
                    Rows.Add(row);
                }
            }

            OnPropertyChanged(nameof(HeaderSummary));

            // Progressive snapshots fill in cell values as passes complete; if find-in-grid is
            // open, keep its match list current rather than leaving it stale from before this
            // data arrived.
            if (GridSearch.IsOpen)
            {
                GridSearch.Rescan();
            }
        }

        /// <summary>
        /// Defensive invariant check after a completed (non-cancelled) run: the grid must have
        /// exactly one row per profiled column, and every row must have a real name. A
        /// violation here would mean Core's snapshots are no longer carrying Meta/ColumnId for
        /// every column as CONTRACT.md requires - that is a Core defect, not something this
        /// layer can repair, so it is surfaced as a warning rather than silently hidden or
        /// thrown past the UI.
        /// </summary>
        private void AssertRowInvariant(TableProfile snapshot)
        {
            var expectedCount = snapshot.Columns?.Count ?? 0;
            if (Rows.Count == expectedCount && Rows.All(r => !string.IsNullOrEmpty(r.Name)))
            {
                return;
            }

            var message = $"Row/column mismatch after profiling: expected {expectedCount} columns, grid has {Rows.Count} row(s), " +
                           $"{Rows.Count(r => string.IsNullOrEmpty(r.Name))} with a blank name. This indicates Core did not carry " +
                           "column identity (ColumnMeta.ColumnId/Name) through every progressive snapshot - report to Agent A.";
            System.Diagnostics.Debug.Fail(message);
            StatusMessage = $"Done, but with a data-integrity warning: {message}";
        }

        /// <summary>
        /// PLAN.md's tool-window UX ("Copy as Markdown", "Export CSV") plus a way to see the
        /// profiled data independently of the DataGrid — bypasses it entirely.
        /// </summary>
        private void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                StatusMessage = "Copied to clipboard.";
            }
            catch (Exception ex)
            {
                // Clipboard access can transiently fail (another process holding it open) -
                // never worth crashing the tool window over.
                StatusMessage = $"Could not copy to clipboard: {ex.Message}";
            }
        }

        /// <summary>
        /// CONTRACT.md Amendment 14/15 "Go to source table": opens a new, unexecuted query
        /// window listing the FK's target table. The caller (ProfileView's context menu, via
        /// ColumnProfileRow.CanGoToSourceTable) already gated this on ReferencedTable != null
        /// — which per Amendment 15 covers BOTH single-column and composite FKs — but this
        /// method re-checks rather than trusting the caller, since it is the one place that
        /// actually builds and sends SQL.
        ///
        /// Lead's hardening (v0.4.0 field report — a click that silently did nothing): every
        /// return path, including an exception from ANYWHERE in this method (a bad
        /// UIConnectionInfo, the reopened SqlConnection failing, ServiceCache.ScriptFactory not
        /// being wired at all), MUST land in StatusMessage. A .FileAndForget-wrapped caller
        /// only puts unhandled faults in the SSMS ActivityLog — invisible to the user — so
        /// nothing here is allowed to propagate past this method uncaught.
        /// </summary>
        public async Task GoToSourceTableAsync(ColumnProfileRow row)
        {
            try
            {
                if (row?.ReferencedQualifiedName == null)
                {
                    StatusMessage = "Could not open a query window: this column has no single foreign-key target to go to.";
                    return;
                }
                if (_externalConnectionString == null)
                {
                    StatusMessage = "Could not open a query window: no active connection for this table.";
                    return;
                }

                var sql = $"SELECT TOP (1000) * FROM {row.ReferencedQualifiedName};";
                var result = await QueryWindowAccessor.TryOpenAsync(sql, _externalConnectionString).ConfigureAwait(true);
                StatusMessage = result.Success
                    ? $"Opened a new query window for {row.ReferencedQualifiedName}."
                    : $"Could not open a query window: {result.Reason}";
            }
            catch (Exception ex)
            {
                // v0.7.6: SqlScriptEditorControl/IScriptFactory (the query-window-opening path,
                // shared with the results-grid "Go to source") live in assemblies that have
                // shown the SAME cross-22.x-build gap as IGridResultSet -- see
                // ResultsGrid.ResultsGridCapability's doc comment. This path is already
                // isolated from a modal-dialog crash by the async/Task indirection through
                // QueryWindowAccessor (a JIT failure inside an awaited async call surfaces as
                // a normal caught exception here, not a synchronous throw) -- this just makes
                // the MESSAGE as actionable as the results-grid one when that specific class
                // of failure is what happened, instead of a raw exception string.
                var compat = ResultsGrid.ResultsGridCapability.DescribeIfCompatibilityException(ex, "Go to source");
                StatusMessage = compat ?? $"Could not open a query window: {ex.Message}";
            }
        }

        /// <summary>
        /// CONTRACT.md Amendment 14/15 "Go to source for this value": gated by the caller
        /// (ColumnProfileRow.CanGoToSourceForMin/Max) on ReferencedColumn != null — single-
        /// column FKs only, per Amendment 15 — plus a real, safely-formattable Min/Max value.
        /// Re-derives and re-validates everything here rather than trusting the caller. Same
        /// "never fail silently" hardening as GoToSourceTableAsync above.
        ///
        /// Lead's follow-up (v0.5.0 "the item appeared, then refused" report): NULL and
        /// unsupported-type are different problems with different fixes, so they get
        /// different messages — never the single generic "this value can't be safely turned
        /// into SQL" that told the user nothing about which one it was.
        /// </summary>
        public async Task GoToSourceForValueAsync(ColumnProfileRow row, bool isMin)
        {
            try
            {
                if (row?.ReferencedQualifiedName == null)
                {
                    StatusMessage = "Could not open a query window: this column has no single foreign-key target to go to.";
                    return;
                }
                if (_externalConnectionString == null)
                {
                    StatusMessage = "Could not open a query window: no active connection for this table.";
                    return;
                }

                var meta = row.Profile?.Meta;
                if (meta?.ReferencedColumn == null)
                {
                    StatusMessage = "Could not open a query window: this column's foreign key does not resolve to a single column.";
                    return;
                }

                var value = isMin ? row.MinValue : row.MaxValue;
                var whichLabel = isMin ? "Min" : "Max";

                if (SqlLiteralFormatter.IsEffectivelyNull(value))
                {
                    StatusMessage = $"Go to source: [{row.Name}] {whichLabel} is NULL, so there's no value to filter by.";
                    return;
                }
                if (!row.TryFormatValueLiteral(value, out var literal))
                {
                    StatusMessage = $"Go to source: [{row.Name}] {whichLabel} has type {value.GetType().Name} which can't be rendered as a SQL literal.";
                    return;
                }

                var sql = $"SELECT * FROM {row.ReferencedQualifiedName} WHERE {SqlIdentifier.Bracket(meta.ReferencedColumn)} = {literal};";
                var result = await QueryWindowAccessor.TryOpenAsync(sql, _externalConnectionString).ConfigureAwait(true);
                StatusMessage = result.Success
                    ? $"Opened a new query window filtered on {meta.ReferencedColumn}."
                    : $"Could not open a query window: {result.Reason}";
            }
            catch (Exception ex)
            {
                // v0.7.6: SqlScriptEditorControl/IScriptFactory (the query-window-opening path,
                // shared with the results-grid "Go to source") live in assemblies that have
                // shown the SAME cross-22.x-build gap as IGridResultSet -- see
                // ResultsGrid.ResultsGridCapability's doc comment. This path is already
                // isolated from a modal-dialog crash by the async/Task indirection through
                // QueryWindowAccessor (a JIT failure inside an awaited async call surfaces as
                // a normal caught exception here, not a synchronous throw) -- this just makes
                // the MESSAGE as actionable as the results-grid one when that specific class
                // of failure is what happened, instead of a raw exception string.
                var compat = ResultsGrid.ResultsGridCapability.DescribeIfCompatibilityException(ex, "Go to source");
                StatusMessage = compat ?? $"Could not open a query window: {ex.Message}";
            }
        }

        /// <summary>
        /// Fire-and-forget entry points for the context-menu Click handlers in ProfileView.xaml.cs,
        /// which are void event handlers and can't await directly. Routed through
        /// ThreadHelper.JoinableTaskFactory.RunAsync (never a bare "async void" or unobserved
        /// Task) so the work is tracked and exceptions surface through JTF's fault handling
        /// instead of vanishing — same VSTHRD001/VSTHRD110-clean pattern used elsewhere in this
        /// package.
        /// </summary>
        public void GoToSourceTableAsyncFireAndForget(ColumnProfileRow row)
        {
            _joinableTaskFactory.RunAsync(() => GoToSourceTableAsync(row))
                .FileAndForget("SsmsDataAnalyzer/ProfileViewModel/GoToSourceTable");
        }

        public void GoToSourceForValueAsyncFireAndForget(ColumnProfileRow row, bool isMin)
        {
            _joinableTaskFactory.RunAsync(() => GoToSourceForValueAsync(row, isMin))
                .FileAndForget("SsmsDataAnalyzer/ProfileViewModel/GoToSourceForValue");
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
