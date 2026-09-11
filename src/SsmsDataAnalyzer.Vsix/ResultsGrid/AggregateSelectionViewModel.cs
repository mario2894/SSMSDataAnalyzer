using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using SsmsDataAnalyzer.Core.Aggregate;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>One label/value line in the popup ("Count" / "2"). Immutable — bindings to it
    /// must stay Mode=OneWay (docs/pivot-plan.md §11's WPF pitfall).</summary>
    internal sealed class AggregateRow
    {
        public string Label { get; }
        public string Value { get; }

        public AggregateRow(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    /// <summary>Backs AggregateSelectionView: turns one SelectionAggregationResult snapshot
    /// into the six label/value rows the popup shows, plus the small gray context line.</summary>
    internal sealed class AggregateSelectionViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<AggregateRow> Rows { get; } = new ObservableCollection<AggregateRow>();

        private string _contextLine;
        public string ContextLine
        {
            get => _contextLine;
            private set
            {
                if (_contextLine == value) return;
                _contextLine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasContextLine));
            }
        }

        public bool HasContextLine => !string.IsNullOrEmpty(ContextLine);

        public void Bind(SelectionAggregationResult result)
        {
            Rows.Clear();
            Rows.Add(new AggregateRow("Count", result.CountText));
            Rows.Add(new AggregateRow("Distinct", result.DistinctText));
            Rows.Add(new AggregateRow("Sum", result.SumText));
            Rows.Add(new AggregateRow("Average", result.AverageText));
            Rows.Add(new AggregateRow("Min", result.MinText));
            Rows.Add(new AggregateRow("Max", result.MaxText));
            ContextLine = result.ContextLine;
        }

        /// <summary>Ctrl+C's payload: every row as "Label\tValue", one per line — plain text
        /// that pastes cleanly into Excel or an email.</summary>
        public string CopyAllText => string.Join(Environment.NewLine, Rows.Select(r => r.Label + "\t" + r.Value));

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
