using System.Globalization;
using SsmsDataAnalyzer.Core.Aggregate;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Aggregate
{
    public class SelectionAggregatorTests
    {
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo HrHr = CultureInfo.GetCultureInfo("hr-HR");

        [Fact]
        public void ScreenshotCase_125And127()
        {
            var r = SelectionAggregator.Aggregate(new[] { "125", "127" }, EnUs);

            Assert.Equal(2, r.CellCount);
            Assert.Equal(2, r.Count);
            Assert.Equal(2, r.Distinct);
            Assert.True(r.HasNumericSummary);
            Assert.Equal("252", r.SumText);
            Assert.Equal("126", r.AverageText);
            Assert.Equal("125", r.MinText);
            Assert.Equal("127", r.MaxText);
            Assert.Equal("2", r.CountText);
            Assert.Equal("2", r.DistinctText);
            Assert.Null(r.ContextLine);
        }

        [Fact]
        public void NullsAreExcludedFromCountAndDistinctAndNumerics()
        {
            var r = SelectionAggregator.Aggregate(new[] { "125", "NULL", "127", "NULL" }, EnUs);

            Assert.Equal(4, r.CellCount);
            Assert.Equal(2, r.NullCount);
            Assert.Equal(2, r.Count);
            Assert.Equal(2, r.Distinct);
            Assert.True(r.HasNumericSummary);
            Assert.Equal("252", r.SumText);
            Assert.Contains("2 NULL", r.ContextLine);
        }

        [Fact]
        public void DuplicatesCountButDoNotInflateDistinct()
        {
            var r = SelectionAggregator.Aggregate(new[] { "5", "5", "5" }, EnUs);

            Assert.Equal(3, r.Count);
            Assert.Equal(1, r.Distinct);
            Assert.Equal("15", r.SumText);
            Assert.Equal("5", r.AverageText);
            Assert.Equal("5", r.MinText);
            Assert.Equal("5", r.MaxText);
        }

        [Fact]
        public void DecimalsUseMaxScaleSeenAmongInputs()
        {
            var r = SelectionAggregator.Aggregate(new[] { "1.5", "2.25" }, EnUs);

            Assert.Equal("3.75", r.SumText);
            Assert.Equal("1.5", r.MinText); // trimmed: 1.50 -> 1.5 needs only 1 decimal digit? see below
            Assert.Equal("2.25", r.MaxText);
        }

        [Fact]
        public void NegativeValues()
        {
            var r = SelectionAggregator.Aggregate(new[] { "-10", "5" }, EnUs);

            Assert.Equal("-5", r.SumText);
            Assert.Equal("-10", r.MinText);
            Assert.Equal("5", r.MaxText);
        }

        [Fact]
        public void ExponentFormIsParsedAsNumeric()
        {
            var r = SelectionAggregator.Aggregate(new[] { "1E2", "50" }, EnUs);

            Assert.True(r.HasNumericSummary);
            Assert.Equal("150", r.SumText);
        }

        [Fact]
        public void MixedNumericAndText_SumAndAverageUnavailable()
        {
            var r = SelectionAggregator.Aggregate(new[] { "1", "2", "abc" }, EnUs);

            Assert.False(r.HasNumericSummary);
            Assert.Equal("—", r.SumText);
            Assert.Equal("—", r.AverageText);
            Assert.Equal("1 value isn't a number", r.UnavailableReason);
            Assert.Contains("1 not numeric", r.ContextLine);
        }

        [Fact]
        public void AllDates_MinMaxByDate()
        {
            var r = SelectionAggregator.Aggregate(
                new[] { "2026-01-02 03:04:05.678", "2024-06-01 00:00:00.000" }, EnUs);

            Assert.False(r.HasNumericSummary);
            Assert.Equal("date", r.MinMaxKind);
            Assert.Equal("2024-06-01 00:00:00.000", r.MinText);
            Assert.Equal("2026-01-02 03:04:05.678", r.MaxText);
        }

        [Fact]
        public void AllText_MinMaxByText()
        {
            var r = SelectionAggregator.Aggregate(new[] { "banana", "apple", "cherry" }, EnUs);

            Assert.Equal("text", r.MinMaxKind);
            Assert.Equal("apple", r.MinText);
            Assert.Equal("cherry", r.MaxText);
        }

        [Fact]
        public void EmptySelection_ZerosAndPlaceholders()
        {
            var r = SelectionAggregator.Aggregate(new string[0], EnUs);

            Assert.Equal(0, r.CellCount);
            Assert.Equal(0, r.Count);
            Assert.Equal(0, r.Distinct);
            Assert.Equal("—", r.SumText);
            Assert.Equal("—", r.MinText);
            Assert.Null(r.MinMaxKind);
            Assert.Null(r.ContextLine);
        }

        [Fact]
        public void HrHrFormatting_UsesCommaDecimalAndPeriodThousands()
        {
            var r = SelectionAggregator.Aggregate(new[] { "1000000", "234567.5" }, HrHr);

            Assert.Equal("1.234.567,5", r.SumText);
        }

        [Fact]
        public void EnUsFormatting_UsesThousandsSeparators()
        {
            var r = SelectionAggregator.Aggregate(new[] { "1000000", "234567" }, EnUs);

            Assert.Equal("1,234,567", r.SumText);
        }

        [Fact]
        public void HugeSums_NoOverflow()
        {
            var r = SelectionAggregator.Aggregate(
                new[] { "79000000000000000000000000", "1" }, EnUs);

            Assert.True(r.HasNumericSummary);
            Assert.Equal("79,000,000,000,000,000,000,000,001", r.SumText);
        }
    }
}
