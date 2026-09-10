using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Data;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Pivot;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// Backs PivotView: one PivotResult snapshot (docs/pivot-plan.md section 3) turned into
    /// the transposed row list, the banner text, and the three Phase 2 filters (differing-only,
    /// hide-all-NULL, name substring). Filtering is done with a WPF ICollectionView predicate
    /// rather than rebuilding the list, so the toggles/filter box don't re-touch PivotResult at
    /// all after <see cref="Load(PivotResult)"/> — the whole point of the snapshot.
    ///
    /// Also owns the §12 FK-link lifecycle: values show immediately; if a PivotFkLinks is
    /// bound, ResolveAsync runs in the background and its result (or decline) updates the
    /// banner/marker/icon state once, ignoring stale results from a superseded snapshot.
    /// </summary>
    internal sealed class PivotViewModel : INotifyPropertyChanged
    {
        private readonly List<PivotRowItem> _allItems = new List<PivotRowItem>();

        private int _snapshotVersion;
        private CancellationTokenSource _fkCts;
        private string _baseBannerText = string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Filtered view PivotView's DataGrid binds ItemsSource to.</summary>
        public ICollectionView Items { get; }

        /// <summary>Pivoted grid rows (0-based), in DataGrid column order — PivotView uses
        /// this to build the "Row N" (1-based) column headers. Raised via ColumnsChanged
        /// whenever a new pivot is loaded, since the column COUNT itself changes per pivot.</summary>
        public IReadOnlyList<long> Rows { get; private set; } = Array.Empty<long>();

        /// <summary>Fired after Load — PivotView.xaml.cs rebuilds its dynamic "Row N" DataGrid
        /// columns in response, since AutoGenerateColumns=False means nothing else would.</summary>
        public event EventHandler ColumnsChanged;

        private string _banner = string.Empty;
        /// <summary>Base "Showing X of Y..." text plus, per §12, a " · resolving FK links…" or
        /// " · N FK links" suffix. The decline case is NOT folded in here — see FkStatusText.</summary>
        public string Banner
        {
            get => _banner;
            private set { _banner = value; OnPropertyChanged(); }
        }

        private string _fkStatusText = string.Empty;
        /// <summary>§12: "FK links unavailable: &lt;reason&gt;" when the whole pivot's FK
        /// resolution declined, else empty. Kept out of Banner so PivotView can render it as
        /// its own gray line instead of picking up the truncated-banner highlight styling.</summary>
        public string FkStatusText
        {
            get => _fkStatusText;
            private set { _fkStatusText = value; OnPropertyChanged(); }
        }

        private bool _truncated;
        /// <summary>True when the selection had more rows than the pivot row limit allowed —
        /// PivotView makes the banner visually noticeable (bold, VS alert-ish theme brush) when this is set.</summary>
        public bool Truncated
        {
            get => _truncated;
            private set { _truncated = value; OnPropertyChanged(); }
        }

        private PivotFkLinks _fkLinks;
        private PivotFkLinkMap _fkLinkMap;
        /// <summary>The resolved (or declined) FK map for the current snapshot, or null while
        /// unresolved / when the pivot has no FK engine at all.</summary>
        internal PivotFkLinkMap FkLinkMap
        {
            get => _fkLinkMap;
            private set { _fkLinkMap = value; OnPropertyChanged(); UpdateBannerAndStatus(); }
        }

        private bool _showOnlyDiffering;
        public bool ShowOnlyDiffering
        {
            get => _showOnlyDiffering;
            set { _showOnlyDiffering = value; OnPropertyChanged(); Items.Refresh(); }
        }

        private bool _hideAllNull;
        public bool HideAllNull
        {
            get => _hideAllNull;
            set { _hideAllNull = value; OnPropertyChanged(); Items.Refresh(); }
        }

        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set { _filterText = value ?? string.Empty; OnPropertyChanged(); Items.Refresh(); }
        }

        public PivotViewModel()
        {
            Items = CollectionViewSource.GetDefaultView(_allItems);
            Items.Filter = FilterPredicate;
        }

        /// <summary>(Re)points this view model at a fresh pivot snapshot, with no FK engine —
        /// existing behavior, unchanged.</summary>
        public void Load(PivotResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Load(result, null);
        }

        /// <summary>docs/pivot-plan.md §12: (re)points this view model at a fresh pivot
        /// snapshot. Values show immediately; if <paramref name="fkLinks"/> is not null, its
        /// ResolveAsync is started in the background with a token cancelled by the next Load
        /// (or CancelPendingResolve, called on window close). A result that arrives after a
        /// newer Load has started is ignored.</summary>
        public void Load(PivotResult result, PivotFkLinks fkLinks)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CancelPendingResolve();

            _snapshotVersion++;
            var snapshotVersion = _snapshotVersion;

            _allItems.Clear();
            Rows = result.Rows;

            for (var c = 0; c < result.Columns.Count; c++)
            {
                var column = result.Columns[c];
                // result.Values is [columnIndex][rowIndex] (frozen interface, plan §10) — this
                // row's Values IS the array for that column, no copying needed.
                _allItems.Add(new PivotRowItem(column.GridOrdinal, column.DisplayName, column.Differs, column.AllNull, result.Values[c]));
            }

            Truncated = result.Truncated;
            _baseBannerText = string.Format(
                CultureInfo.InvariantCulture,
                "Showing {0} of {1} selected rows · {2} columns · {3} differ",
                result.Rows.Count,
                result.TotalSelectedRows,
                result.Columns.Count,
                result.DifferingColumnCount);

            _fkLinks = fkLinks;
            _fkLinkMap = null;
            UpdateBannerAndStatus();

            // Fire ColumnsChanged before Items.Refresh(): the code-behind rebuilds the "Row N"
            // DataGrid columns first, so by the time filtered rows repaint the bindings they
            // reference already exist.
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            Items.Refresh();

            if (fkLinks != null)
            {
                var cts = new CancellationTokenSource();
                _fkCts = cts;

                fkLinks.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        var map = await fkLinks.ResolveAsync(cts.Token).ConfigureAwait(true);
                        ApplyResolved(snapshotVersion, map);
                    }
                    catch (OperationCanceledException)
                    {
                        // Superseded by a newer Bind(), or the window closed — nothing to show.
                    }
                }).FileAndForget("SsmsDataAnalyzer/Pivot/FkLinks/Resolve");
            }
        }

        /// <summary>Cancels and disposes any in-flight ResolveAsync. Called at the start of
        /// every Load and by PivotToolWindow's close/dispose path (§12).</summary>
        internal void CancelPendingResolve()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _fkCts?.Cancel();
            _fkCts?.Dispose();
            _fkCts = null;
        }

        /// <summary>§12 icon click / context-menu handler: fire-and-forget "Go to source" for
        /// one pivoted cell.</summary>
        internal void GoToSource(PivotRowItem row, int rowIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var map = FkLinkMap;
            if (map == null || row == null) return;
            if (rowIndex < 0 || rowIndex >= row.Values.Length) return;
            map.BeginGo(row.GridOrdinal, row.Values[rowIndex]);
        }

        private void ApplyResolved(int snapshotVersion, PivotFkLinkMap map)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (snapshotVersion != _snapshotVersion) return; // stale — a newer pivot is showing

            FkLinkMap = map;

            if (map.DeclineReason != null) return;

            foreach (var item in _allItems)
            {
                bool isLink = map.IsLinkColumn(item.GridOrdinal);
                string targetText = isLink ? map.GetTargetText(item.GridOrdinal) : null;
                bool[] canGo = null;
                if (isLink)
                {
                    canGo = new bool[item.Values.Length];
                    for (int i = 0; i < canGo.Length; i++)
                        canGo[i] = map.CanGo(item.GridOrdinal, item.Values[i]);
                }
                item.ApplyFkColumn(isLink, targetText, canGo);
            }
        }

        private void UpdateBannerAndStatus()
        {
            string suffix = string.Empty;
            string status = string.Empty;

            if (_fkLinks != null)
            {
                if (_fkLinkMap == null)
                {
                    suffix = " · resolving FK links…";
                }
                else if (_fkLinkMap.DeclineReason != null)
                {
                    const string goToSourcePrefix = "Go to source: ";
                    var reason = _fkLinkMap.DeclineReason;
                    if (reason.StartsWith(goToSourcePrefix, StringComparison.Ordinal))
                        reason = reason.Substring(goToSourcePrefix.Length);
                    status = "FK links unavailable: " + reason;
                }
                else if (_fkLinkMap.LinkColumnCount > 0)
                {
                    suffix = string.Format(CultureInfo.InvariantCulture, " · {0} FK links", _fkLinkMap.LinkColumnCount);
                }
            }

            Banner = _baseBannerText + suffix;
            FkStatusText = status;
        }

        private bool FilterPredicate(object obj)
        {
            var item = (PivotRowItem)obj;
            if (ShowOnlyDiffering && !item.Differs) return false;
            if (HideAllNull && item.AllNull) return false;
            if (FilterText.Length > 0 && item.DisplayName.IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
