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
        /// <summary>docs/pivot-plan.md §4 item 11: the default "Header:" chooser entry —
        /// value columns keep today's "Row N" header.</summary>
        internal const string RowNumberHeaderOption = "Row number";

        /// <summary>Trim length for a header built from a column's value (item 11) — the full
        /// text still appears in the header tooltip.</summary>
        private const int HeaderValueMaxLength = 30;

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

        private IReadOnlyList<string> _headerColumnOptions = new[] { RowNumberHeaderOption };
        /// <summary>docs/pivot-plan.md §4 item 11: "Row number" first, then every pivot
        /// column's DisplayName, in grid order. Rebuilt on every <see cref="Load(PivotResult)"/>.</summary>
        public IReadOnlyList<string> HeaderColumnOptions
        {
            get => _headerColumnOptions;
            private set { _headerColumnOptions = value; OnPropertyChanged(); }
        }

        private string _selectedHeaderOption = RowNumberHeaderOption;
        /// <summary>Which column's value labels the value-column headers (item 11). Not part
        /// of the snapshot and never persisted — reset to "Row number" on every Load.
        /// Changing it only relabels headers (see PivotView.xaml.cs RefreshColumnHeaders); it
        /// never touches _allItems, so FK bindings/icons are unaffected.</summary>
        public string SelectedHeaderOption
        {
            get => _selectedHeaderOption;
            set
            {
                var next = string.IsNullOrEmpty(value) ? RowNumberHeaderOption : value;
                if (string.Equals(_selectedHeaderOption, next, StringComparison.Ordinal)) return;
                _selectedHeaderOption = next;
                OnPropertyChanged();
                HeaderOptionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Fired when <see cref="SelectedHeaderOption"/> changes — PivotView.xaml.cs
        /// relabels the existing "Row N"/value columns' headers in response (item 11); no
        /// column rebuild, no data reload.</summary>
        public event EventHandler HeaderOptionChanged;

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

            // item 11: a fresh pivot always starts on "Row number" (not persisted across
            // Loads) and offers this snapshot's own columns as header choices.
            var headerOptions = new List<string>(_allItems.Count + 1) { RowNumberHeaderOption };
            foreach (var item in _allItems) headerOptions.Add(item.DisplayName);
            HeaderColumnOptions = headerOptions;
            if (!string.Equals(_selectedHeaderOption, RowNumberHeaderOption, StringComparison.Ordinal))
            {
                _selectedHeaderOption = RowNumberHeaderOption;
                OnPropertyChanged(nameof(SelectedHeaderOption));
            }

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

        /// <summary>docs/pivot-plan.md §4 item 11: the header text/tooltip for one value
        /// column, honoring <see cref="SelectedHeaderOption"/>. <paramref name="rowIndex"/> is
        /// the 0-based position in <see cref="Rows"/>/PivotRowItem.Values; <paramref
        /// name="gridRow"/> is that same row's 0-based grid row number (Rows[rowIndex]).
        /// Pure — reads _allItems, never rebuilds it.</summary>
        internal PivotColumnHeaderInfo GetHeaderInfo(int rowIndex, long gridRow)
        {
            var rowLabel = "Row " + (gridRow + 1).ToString(CultureInfo.InvariantCulture);
            if (string.Equals(_selectedHeaderOption, RowNumberHeaderOption, StringComparison.Ordinal))
                return new PivotColumnHeaderInfo(rowLabel, null);

            foreach (var item in _allItems)
            {
                if (!string.Equals(item.DisplayName, _selectedHeaderOption, StringComparison.Ordinal)) continue;
                if (rowIndex < 0 || rowIndex >= item.Values.Length) break;

                var full = item.Values[rowIndex] ?? string.Empty;
                var trimmed = full.Length > HeaderValueMaxLength
                    ? full.Substring(0, HeaderValueMaxLength) + "…"
                    : full;
                var text = item.DisplayName + " = " + trimmed;
                var tooltip = item.DisplayName + " = " + full + " · " + rowLabel;
                return new PivotColumnHeaderInfo(text, tooltip);
            }

            // Chosen column no longer exists in this snapshot (shouldn't happen — options are
            // rebuilt every Load) — fall back to the default rather than throw.
            return new PivotColumnHeaderInfo(rowLabel, null);
        }

        /// <summary>docs/pivot-plan.md §4 item 14: the currently VISIBLE pivot (i.e. after
        /// Show-only-differing / Hide-all-NULL / Filter), in the current header labels (item
        /// 11), as header texts + rows of cell strings ready for PivotMarkdown.Build. Never
        /// touches the clipboard itself — PivotView.xaml.cs owns that.</summary>
        internal void GetVisibleMarkdownData(out List<string> headers, out List<IReadOnlyList<string>> rows)
        {
            headers = new List<string> { "Column" };
            for (var i = 0; i < Rows.Count; i++)
                headers.Add(GetHeaderInfo(i, Rows[i]).Text);

            rows = new List<IReadOnlyList<string>>();
            foreach (var obj in Items)
            {
                if (!(obj is PivotRowItem item)) continue;
                var row = new List<string>(item.Values.Length + 1) { item.DisplayName };
                row.AddRange(item.Values);
                rows.Add(row);
            }
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

    /// <summary>docs/pivot-plan.md §4 item 11: one value column's header (Text) plus its
    /// optional tooltip (full untrimmed text + "Row N"; null when the default "Row N" header
    /// is in effect). Immutable — PivotView.xaml.cs assigns a fresh instance to
    /// DataGridColumn.Header each time the header relabels; matched by an implicit DataTemplate
    /// in PivotView.xaml (DataType lookup), so no column/template rebuild is needed.</summary>
    internal sealed class PivotColumnHeaderInfo
    {
        public PivotColumnHeaderInfo(string text, string tooltip)
        {
            Text = text;
            Tooltip = tooltip;
        }

        public string Text { get; }
        public string Tooltip { get; }
    }
}
