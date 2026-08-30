using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SsmsDataAnalyzer.Vsix.ToolWindow
{
    /// <summary>
    /// Find-in-grid (Ctrl+F), Notepad-style, per the user's own request. Searches the
    /// RENDERED display text of every visible text column
    /// (ColumnProfileRow.GetSearchableCellText() is the single source of truth both this
    /// class and ProfileView.xaml's CellStyle triggers read, so "what's searched" and "what's
    /// highlighted" can never drift apart) — case-insensitive substring, no regex, no options
    /// panel, matching how lean the rest of this window was asked to be.
    ///
    /// Highlighting is driven entirely through bound state on each row
    /// (ColumnProfileRow.MatchedColumns / CurrentMatchColumn), never by walking the DataGrid's
    /// visual tree or touching a DataGridCell container directly — row/cell virtualization
    /// recycles containers as the user scrolls, and a recycled container reads its state
    /// fresh from whatever row it's currently bound to, so this cannot smear a stale
    /// highlight onto the wrong row. That is precisely the bug class (correct-looking code,
    /// no notification / wrong layer) that cost four rounds on the grid itself.
    /// </summary>
    public sealed class GridSearchViewModel : ObservableObject
    {
        private readonly ObservableCollection<ColumnProfileRow> _rows;
        private readonly List<(ColumnProfileRow Row, string ColumnKey)> _matches = new List<(ColumnProfileRow, string)>();

        private string _searchText = string.Empty;
        private bool _isOpen;
        private int _currentIndex = -1;
        private string _matchCountDisplay = string.Empty;

        public GridSearchViewModel(ObservableCollection<ColumnProfileRow> rows)
        {
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));

            OpenCommand = new RelayCommand(_ => Open());
            NextCommand = new RelayCommand(_ => MoveNext(), _ => _matches.Count > 0);
            PreviousCommand = new RelayCommand(_ => MovePrevious(), _ => _matches.Count > 0);
            CloseCommand = new RelayCommand(_ => Close());
        }

        /// <summary>Raised when stepping to a match, so the view can scroll it into view — the only view-facing side effect this class produces.</summary>
        public event EventHandler<ColumnProfileRow> ScrollToRowRequested;

        public RelayCommand OpenCommand { get; }
        public RelayCommand NextCommand { get; }
        public RelayCommand PreviousCommand { get; }
        public RelayCommand CloseCommand { get; }

        public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    Rescan();
                }
            }
        }

        /// <summary>"3 of 17", "No matches", or empty when the box is empty/closed.</summary>
        public string MatchCountDisplay { get => _matchCountDisplay; private set => SetProperty(ref _matchCountDisplay, value); }

        public void Open()
        {
            IsOpen = true;
            Rescan();
        }

        public void Close()
        {
            IsOpen = false;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            ClearAllHighlights();
            _matches.Clear();
            _currentIndex = -1;
            MatchCountDisplay = string.Empty;
            RelayCommand.RaiseCanExecuteChangedForAll();
        }

        /// <summary>Re-runs the search against the current Rows — called on every SearchText change and whenever a profiling run replaces the row data while the panel is open.</summary>
        public void Rescan()
        {
            ClearAllHighlights();
            _matches.Clear();
            _currentIndex = -1;

            if (!string.IsNullOrEmpty(SearchText))
            {
                foreach (var row in _rows)
                {
                    HashSet<string> rowMatches = null;
                    foreach (var cell in row.GetSearchableCellText())
                    {
                        if (cell.Value != null && cell.Value.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (rowMatches == null) rowMatches = new HashSet<string>();
                            rowMatches.Add(cell.Key);
                            _matches.Add((row, cell.Key));
                        }
                    }

                    if (rowMatches != null)
                    {
                        row.SetSearchState(rowMatches, null);
                    }
                }
            }

            if (_matches.Count > 0)
            {
                _currentIndex = 0;
                ApplyCurrentMatch(scrollIntoView: true);
            }

            UpdateMatchCountDisplay();
            RelayCommand.RaiseCanExecuteChangedForAll();
        }

        public void MoveNext()
        {
            if (_matches.Count == 0) return;
            _currentIndex = (_currentIndex + 1) % _matches.Count; // wrap
            ApplyCurrentMatch(scrollIntoView: true);
            UpdateMatchCountDisplay();
        }

        public void MovePrevious()
        {
            if (_matches.Count == 0) return;
            _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count; // wrap
            ApplyCurrentMatch(scrollIntoView: true);
            UpdateMatchCountDisplay();
        }

        private void ApplyCurrentMatch(bool scrollIntoView)
        {
            // At most one row carries a non-null CurrentMatchColumn at a time; clear it
            // before (possibly) setting a new one. Row counts here are small (hundreds at
            // most — a wide table's column count), so an O(rows) sweep on each step is cheap.
            foreach (var row in _rows)
            {
                if (row.CurrentMatchColumn != null)
                {
                    row.SetSearchState(row.MatchedColumns, null);
                }
            }

            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;

            var current = _matches[_currentIndex];
            current.Row.SetSearchState(current.Row.MatchedColumns, current.ColumnKey);

            if (scrollIntoView)
            {
                ScrollToRowRequested?.Invoke(this, current.Row);
            }
        }

        private void UpdateMatchCountDisplay()
        {
            MatchCountDisplay = _matches.Count == 0
                ? (string.IsNullOrEmpty(SearchText) ? string.Empty : "No matches")
                : $"{_currentIndex + 1} of {_matches.Count}";
        }

        private void ClearAllHighlights()
        {
            foreach (var row in _rows)
            {
                if (row.MatchedColumns.Count > 0 || row.CurrentMatchColumn != null)
                {
                    row.ClearSearchState();
                }
            }
        }
    }
}
