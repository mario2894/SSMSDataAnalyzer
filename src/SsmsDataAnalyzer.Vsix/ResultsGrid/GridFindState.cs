using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>One matched cell — GRID column index (same convention as
    /// IGridStorage.GetCellDataAsString; v0.8.0, was IGridResultSet.GetCellData — see
    /// docs/newer-grid-api.md).</summary>
    internal struct GridFindMatch
    {
        public long Row;
        public int GridCol;
    }

    /// <summary>
    /// User request: "Find... on right click in result grid of SSMS, we have it already in
    /// tool and it works great." Same UX as the tool window's find (case-insensitive
    /// substring, all columns, N-of-M, wrap-around) — but the DATA and PAINT model are both
    /// completely different here, so this is a fresh implementation, not a port.
    ///
    /// Search corpus: docs/resultsgrid-api.md + this project's own spike established that
    /// SSMS's grid storage (QEDiskStorageView / QEStorageViewOnReader) spools the FULL result
    /// set for random access, not just what has been scrolled into view — so unlike a naive
    /// "search the rendered rows" approach, this genuinely searches every row up to
    /// TotalNumberOfRows. It reads via GetCellDataAsString (the same truncated-for-display
    /// text the user is actually looking at on screen — this is a find-what-I-see feature,
    /// not a SQL-literal-generation one, so the display formatter's truncation is an
    /// acceptable, honestly-scoped limitation here, unlike the "Go to source" value jump
    /// where the same truncation would be a correctness bug).
    ///
    /// Chunked, not fire-and-forget: IGridStorage has no async surface and this project has
    /// no live-host evidence that IGridStorage is safe to read from a background thread while
    /// the UI paints concurrently. So the scan runs ON the UI thread, yielding periodically
    /// via Task.Yield() so a million-row scan does not freeze SSMS — slower than a background
    /// thread would be, but provably safe without a live host to test against.
    /// </summary>
    internal sealed class GridFindState
    {
        /// <summary>Lead's refinement: cap the tracked match set so a wildcard-ish search
        /// against a huge result set cannot grow without bound. When hit, the counter must
        /// say so honestly rather than silently presenting a partial result as complete.</summary>
        public const int MaxMatches = 10_000;

        public readonly GridControl Grid;
        private readonly IGridStorage _resultSet;

        public List<GridFindMatch> Matches { get; } = new List<GridFindMatch>();
        public int CurrentIndex { get; private set; } = -1;
        public bool Capped { get; private set; }
        public bool IsSearching { get; private set; }

        /// <summary>
        /// Whether CustomizeCellGDIObjectsEventArgs.ColumnIndex actually uses the same GRID
        /// column-index convention as GetCellData — the "highest-risk unknown" the lead
        /// flagged. Null until the first self-check runs (on the first repaint of a known
        /// match cell); the paint handler must not trust ColumnIndex for anything until this
        /// is true, and must permanently stop trying if it comes back false.
        /// </summary>
        public bool? ColumnIndexConventionVerified { get; set; }

        public GridFindState(GridControl grid, IGridStorage resultSet)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _resultSet = resultSet ?? throw new ArgumentNullException(nameof(resultSet));
        }

        /// <summary>True if the grid's OWN result set has been replaced since this state was
        /// built (a re-run reusing the same GridControl instance) — the lead's "multiple
        /// grids/tabs" note: rather than silently searching stale data, callers must check
        /// this and treat it the same as the grid being gone.</summary>
        public bool IsStale => !ReferenceEquals(Grid.GridStorage, _resultSet);

        public async Task SearchAsync(string searchText, Action<int> onProgressRow, CancellationToken cancellationToken)
        {
            Matches.Clear();
            CurrentIndex = -1;
            Capped = false;
            ColumnIndexConventionVerified = null;

            if (string.IsNullOrEmpty(searchText)) return;

            IsSearching = true;
            try
            {
                long totalRows = _resultSet.NumRows();
                // ColumnsNumber counts the row-number gutter too (v0.8.0, re-verified live —
                // see GridClickCapture's class doc comment), hence the -1.
                int dataCols = Grid.ColumnsNumber - 1;

                for (long row = 0; row < totalRows; row++)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (IsStale) return; // grid was re-populated mid-search — stop, do not report matches against the old data

                    for (int dataCol = 0; dataCol < dataCols; dataCol++)
                    {
                        int gridCol = dataCol + 1; // GetCellData/GetCellDataAsString convention: grid index, 0 = gutter
                        string text;
                        try
                        {
                            text = _resultSet.GetCellDataAsString(row, gridCol);
                        }
                        catch
                        {
                            continue; // an unreadable cell should not abort the whole search
                        }

                        if (text != null && text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Matches.Add(new GridFindMatch { Row = row, GridCol = gridCol });
                            if (Matches.Count >= MaxMatches)
                            {
                                Capped = true;
                                if (Matches.Count > 0) CurrentIndex = 0;
                                return;
                            }
                        }
                    }

                    if (row % 25 == 0)
                    {
                        onProgressRow?.Invoke((int)row);
                        await Task.Yield();
                    }
                }

                if (Matches.Count > 0) CurrentIndex = 0;
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>
        /// v0.7.1: typing must NOT trigger a search (a single keystroke used to launch a
        /// full chunked scan of the entire result set, making the box unusable — see
        /// SearchAsync's own doc comment). Called on every text change instead, to drop the
        /// now-stale match set and let the UI show an explicit "not searched yet" state
        /// rather than leaving old highlights/counts that no longer describe the current box
        /// contents.
        /// </summary>
        public void Clear()
        {
            Matches.Clear();
            CurrentIndex = -1;
            Capped = false;
            ColumnIndexConventionVerified = null;
        }

        public bool TryGetCurrent(out GridFindMatch match)
        {
            if (CurrentIndex < 0 || CurrentIndex >= Matches.Count) { match = default; return false; }
            match = Matches[CurrentIndex];
            return true;
        }

        public void MoveNext()
        {
            if (Matches.Count == 0) return;
            CurrentIndex = (CurrentIndex + 1) % Matches.Count; // wrap-around
        }

        public void MovePrevious()
        {
            if (Matches.Count == 0) return;
            CurrentIndex = (CurrentIndex - 1 + Matches.Count) % Matches.Count; // wrap-around
        }

        /// <summary>Cheap, non-allocating membership check for the paint handler — called once
        /// per visible cell per repaint, so this must stay fast (a List scan is fine at
        /// MaxMatches scale for a grid that only paints ~40 visible rows at a time).</summary>
        public bool IsMatch(long row, int gridCol)
        {
            for (int i = 0; i < Matches.Count; i++)
            {
                var m = Matches[i];
                if (m.Row == row && m.GridCol == gridCol) return true;
            }
            return false;
        }

        public bool IsCurrent(long row, int gridCol)
        {
            if (CurrentIndex < 0 || CurrentIndex >= Matches.Count) return false;
            var m = Matches[CurrentIndex];
            return m.Row == row && m.GridCol == gridCol;
        }
    }
}
