using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Vsix.GoToSource;

namespace SsmsDataAnalyzer.Vsix.ToolWindow
{
    /// <summary>
    /// Flattens a <see cref="ColumnProfile"/> (nested under <see cref="ColumnMeta"/>) into a
    /// single row shape that binds cleanly to a WPF <c>DataGrid</c>, and re-flattens in place
    /// as progress updates arrive so existing row objects — not the whole collection — mutate,
    /// which keeps sort/selection state stable while a profile run is in flight.
    ///
    /// Per CONTRACT.md: <see cref="Distinct"/> is a real count or blank — never an
    /// approximation. When <see cref="ColumnProfile.SkipReason"/> is set (e.g. sampling is
    /// active), <see cref="DistinctDisplay"/> renders blank and <see cref="SkipReason"/> is
    /// surfaced as a tooltip by the view, not as a number.
    /// </summary>
    public sealed class ColumnProfileRow : ObservableObject
    {
        private ColumnProfile _profile;

        public ColumnProfileRow(ColumnProfile profile)
        {
            Apply(profile);
        }

        public void Apply(ColumnProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));

            // Deliberately NOT OnPropertyChanged(string.Empty). That "all properties changed"
            // signal is correct per INotifyPropertyChanged's contract and works fine for
            // simple ItemsControl/ListBox bindings, but WPF's DataGrid has a long-documented
            // history of NOT reliably re-pulling DataGridBoundColumn cell bindings from it —
            // particularly once EnableRowVirtualization has recycled a row's containers. That
            // produced exactly the symptom reported against a real 57-column table: most rows
            // rendering with empty Column/Type/Filled/... cells (only the two most recently
            // realized rows showed real content), even though the underlying data — verified
            // by a headless harness driving this exact class against a 162-column table — was
            // always fully and correctly populated. Raising one explicit event per bound
            // property is the form every WPF binding path is guaranteed to honor.
            OnPropertyChanged(nameof(ColumnId));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(TypeName));
            OnPropertyChanged(nameof(Collation));
            OnPropertyChanged(nameof(IsNullable));
            OnPropertyChanged(nameof(IsIdentity));
            OnPropertyChanged(nameof(IsPrimaryKey));
            OnPropertyChanged(nameof(Filled));
            OnPropertyChanged(nameof(FillPercent));
            OnPropertyChanged(nameof(Blank));
            OnPropertyChanged(nameof(Distinct));
            OnPropertyChanged(nameof(DistinctDisplay));
            OnPropertyChanged(nameof(SkipReason));
            OnPropertyChanged(nameof(DistinctPercent));
            OnPropertyChanged(nameof(LastFillDate));
            OnPropertyChanged(nameof(MinValue));
            OnPropertyChanged(nameof(MaxValue));
            OnPropertyChanged(nameof(AvgByteLength));
            OnPropertyChanged(nameof(Flags));
            OnPropertyChanged(nameof(FlagsDisplay));
            OnPropertyChanged(nameof(Profile));
        }

        public int? ColumnId => _profile.Meta?.ColumnId;
        public string Name => _profile.Meta?.Name;
        public string TypeName => _profile.Meta?.TypeName;
        public string Collation => _profile.Meta?.Collation;
        public bool IsNullable => _profile.Meta?.IsNullable ?? false;
        public bool IsIdentity => _profile.Meta?.IsIdentity ?? false;
        public bool IsPrimaryKey => _profile.Meta?.IsPrimaryKey ?? false;

        public long? Filled => _profile.FilledCount;
        public double? FillPercent => Filled.HasValue && _profile.Meta != null && FilledCountBase > 0
            ? (double)Filled.Value / FilledCountBase * 100.0
            : (double?)null;

        // Set by the view model from TableProfile.TotalRows since ColumnProfile itself
        // doesn't carry the denominator.
        public long FilledCountBase { get; set; }

        public long? Blank => _profile.BlankCount;

        public long? Distinct => _profile.DistinctCount;
        public string DistinctDisplay => _profile.DistinctCount?.ToString() ?? (_profile.SkipReason != null ? "—" : "…");
        public string SkipReason => _profile.SkipReason;

        public double? DistinctPercent => Distinct.HasValue && Filled.HasValue && Filled.Value > 0
            ? (double)Distinct.Value / Filled.Value * 100.0
            : (double?)null;

        public DateTime? LastFillDate => _profile.LastFillDate;
        public object MinValue => _profile.MinValue;
        public object MaxValue => _profile.MaxValue;
        public double? AvgByteLength => _profile.AvgByteLength;

        public ColumnFlag Flags => _profile.Flags;
        public string FlagsDisplay => Flags == ColumnFlag.None ? string.Empty : Flags.ToString();

        public ColumnProfile Profile => _profile;

        // ---- Go to source (CONTRACT.md Amendment 14/15) ----------------------------------
        //
        // Gating per Amendment 15's revised rule: the table jump is offered whenever
        // ReferencedTable != null - this covers BOTH single-column FKs AND composite FKs
        // (a composite FK belongs to exactly one constraint referencing exactly one table,
        // so the table is unambiguous even though which column to filter on is not). The
        // value jump additionally requires ReferencedColumn != null (single-column FKs
        // only) and a real, non-null Min/Max value that can be safely rendered as a SQL
        // literal. NEVER gate on IsForeignKey alone - both composite FKs and columns
        // participating in multiple FKs set it true.
        public bool CanGoToSourceTable => _profile.Meta?.ReferencedTable != null;

        public string ReferencedQualifiedName => _profile.Meta?.ReferencedQualifiedName;

        public bool CanGoToSourceForMin => TryFormatValueLiteral(MinValue, out _);
        public bool CanGoToSourceForMax => TryFormatValueLiteral(MaxValue, out _);

        /// <summary>
        /// Builds the WHERE-clause literal for a Min/Max value jump, or returns false when
        /// unavailable - either this column's FK is ambiguous (ReferencedColumn == null:
        /// a composite FK or a column in multiple FKs), the cell itself is NULL, or the
        /// value's runtime type can't be safely rendered as a SQL literal
        /// (SqlLiteralFormatter withholds rather than guessing - see its own docs on why
        /// Core's display-only ProfileFormat.Value must never be used here instead).
        /// </summary>
        public bool TryFormatValueLiteral(object value, out string literal)
        {
            literal = null;
            if (_profile.Meta?.ReferencedColumn == null) return false;
            if (value == null) return false;
            return SqlLiteralFormatter.TryFormat(value, _profile.Meta, out literal);
        }

        // ---- Find-in-grid (Ctrl+F) -------------------------------------------------------
        //
        // Column keys here are the exact DataGridColumn.Header strings used both by
        // GetSearchableCellText() (what gets searched) and ProfileView.xaml's per-column
        // CellStyle triggers (what gets highlighted) — one source of truth, so the two can
        // never drift apart.
        //
        // Highlighting is driven entirely by this BOUND state (MatchedColumns /
        // CurrentMatchColumn), never by reaching into DataGridCell containers directly.
        // DataGrid virtualization recycles row/cell containers as the user scrolls; a
        // container reads whatever ColumnProfileRow it's currently bound to fresh, on reuse,
        // so recycling is harmless here — this is deliberately the same lesson the grid's own
        // rendering bugs already taught (see Apply(), above).
        private static readonly ISet<string> NoMatches = new HashSet<string>();

        public ISet<string> MatchedColumns { get; private set; } = NoMatches;
        public string CurrentMatchColumn { get; private set; }

        public void SetSearchState(ISet<string> matchedColumns, string currentMatchColumn)
        {
            MatchedColumns = matchedColumns ?? NoMatches;
            CurrentMatchColumn = currentMatchColumn;
            OnPropertyChanged(nameof(MatchedColumns));
            OnPropertyChanged(nameof(CurrentMatchColumn));
        }

        public void ClearSearchState() => SetSearchState(null, null);

        /// <summary>
        /// The RENDERED display text of every text/numeric column find-in-grid searches —
        /// deliberately excludes the Null/Ident/PK checkbox columns and Distinct %, matching
        /// exactly what the user asked to search. Each value here is built with the SAME
        /// null-handling / format string as the corresponding DataGridTextColumn in
        /// ProfileView.xaml, so "what you see is what you search" is actually true, not just
        /// asserted.
        /// </summary>
        public IReadOnlyDictionary<string, string> GetSearchableCellText()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Column"] = Name ?? string.Empty,
                ["Type"] = TypeName ?? string.Empty,
                ["Collation"] = Collation ?? "—",
                ["Filled"] = Filled?.ToString() ?? "—",
                ["Fill %"] = FillPercent?.ToString("0.0") ?? "—",
                ["Blank"] = Blank?.ToString() ?? "—",
                ["Distinct"] = DistinctDisplay ?? string.Empty,
                ["Last Fill"] = LastFillDate?.ToString("yyyy-MM-dd") ?? "—",
                ["Min"] = MinValue?.ToString() ?? string.Empty,
                ["Max"] = MaxValue?.ToString() ?? string.Empty,
                ["Avg Len"] = AvgByteLength?.ToString("0.0") ?? "—",
                ["Flags"] = FlagsDisplay ?? string.Empty
            };
        }
    }
}
