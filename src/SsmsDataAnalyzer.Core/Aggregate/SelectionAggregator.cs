using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SsmsDataAnalyzer.Core.Aggregate
{
    /// <summary>
    /// Immutable result of <see cref="SelectionAggregator.Aggregate"/> — every number already
    /// formatted with the caller's output culture, ready to bind straight into WPF (all
    /// properties are read-only, so bindings must stay Mode=OneWay per docs/pivot-plan.md
    /// §11's WPF pitfall).
    /// </summary>
    public sealed class SelectionAggregationResult
    {
        /// <summary>Every selected cell, NULL or not.</summary>
        public int CellCount { get; }

        /// <summary>Cells whose display text is exactly "NULL" (same ambiguity
        /// PeekValueFormatter/PivotBuilder already live with — a text column literally
        /// containing the word "NULL" is indistinguishable from a real NULL here).</summary>
        public int NullCount { get; }

        /// <summary>Non-NULL cells — what SQL's COUNT(col) would report.</summary>
        public int Count { get; }

        /// <summary>Distinct non-NULL display texts (ordinal comparison).</summary>
        public int Distinct { get; }

        public int NumericCount { get; }
        public int NonNumericCount { get; }

        /// <summary>True when Sum/Average/Min/Max were computed numerically — requires at
        /// least one numeric non-NULL value AND zero non-numeric ones.</summary>
        public bool HasNumericSummary { get; }

        /// <summary>"numeric", "date", or "text" — which comparison Min/Max used. Null when
        /// there were no non-NULL values at all.</summary>
        public string MinMaxKind { get; }

        /// <summary>"—" when unavailable (mixed/non-numeric selection).</summary>
        public string SumText { get; }

        /// <summary>"—" when unavailable (mixed/non-numeric selection).</summary>
        public string AverageText { get; }

        public string MinText { get; }
        public string MaxText { get; }

        public string CountText { get; }
        public string DistinctText { get; }
        public string CellCountText { get; }

        /// <summary>Why Sum/Average are "—", e.g. "3 values aren't numbers". Null when they
        /// were computed, or when there was nothing to summarize at all.</summary>
        public string UnavailableReason { get; }

        /// <summary>Small gray context line ("12 cells selected · 2 NULL · 3 not numeric"),
        /// or null when there is nothing extra worth saying (no NULLs, nothing non-numeric).</summary>
        public string ContextLine { get; }

        internal SelectionAggregationResult(
            int cellCount, int nullCount, int count, int distinct,
            int numericCount, int nonNumericCount, bool hasNumericSummary, string minMaxKind,
            string sumText, string averageText, string minText, string maxText,
            string countText, string distinctText, string cellCountText,
            string unavailableReason, string contextLine)
        {
            CellCount = cellCount;
            NullCount = nullCount;
            Count = count;
            Distinct = distinct;
            NumericCount = numericCount;
            NonNumericCount = nonNumericCount;
            HasNumericSummary = hasNumericSummary;
            MinMaxKind = minMaxKind;
            SumText = sumText;
            AverageText = averageText;
            MinText = minText;
            MaxText = maxText;
            CountText = countText;
            DistinctText = distinctText;
            CellCountText = cellCountText;
            UnavailableReason = unavailableReason;
            ContextLine = contextLine;
        }
    }

    /// <summary>
    /// "Aggregate selection…" — pure logic (no GridControl/IGridStorage reference here, so it
    /// is unit-testable without SSMS running). Callers pass the grid's own display text
    /// (IGridStorage.GetCellDataAsString, same source GridFindState/PivotBuilder already use)
    /// for every selected cell, plus the culture to format the output numbers in — the grid
    /// itself always speaks invariant '.' decimals (see the numeric-parse note below), but
    /// what the *user* wants to read back is their own Windows regional format.
    /// </summary>
    public static class SelectionAggregator
    {
        private const string NullDisplayText = "NULL"; // same sentinel PivotBuilder/PeekValueFormatter use
        private const string PlaceholderText = "—";

        public static SelectionAggregationResult Aggregate(IEnumerable<string> displayTexts, CultureInfo outputCulture)
        {
            if (displayTexts == null) throw new ArgumentNullException(nameof(displayTexts));
            if (outputCulture == null) throw new ArgumentNullException(nameof(outputCulture));

            int cellCount = 0;
            int nullCount = 0;
            var nonNullValues = new List<string>();
            var distinctSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in displayTexts)
            {
                cellCount++;
                var text = raw ?? string.Empty;
                if (text == NullDisplayText)
                {
                    nullCount++;
                    continue;
                }
                nonNullValues.Add(text);
                distinctSet.Add(text);
            }

            int count = nonNullValues.Count;
            int distinct = distinctSet.Count;

            // Numeric parse: the grid always renders numbers with an invariant '.' decimal
            // point regardless of the user's regional settings (same convention
            // PeekValueFormatter.ToDisplayText relies on for its own invariant-culture tests),
            // so parsing is always InvariantCulture even though the OUTPUT below uses
            // outputCulture. NumberStyles.Float also accepts exponent forms ("1.25E2").
            //
            // A value outside System.Decimal's range (~7.9e28) is deliberately treated as
            // NON-numeric rather than falling back to double: SQL Server's own numeric types
            // (decimal/money/bigint/float within normal use) stay well inside that range in
            // practice, and silently switching Sum's accumulator to double for one outlier
            // value would reintroduce the rounding error decimal was chosen to avoid for
            // every other value in the same selection.
            var numericValues = new List<decimal>();
            int numericCount = 0;
            int nonNumericCount = 0;
            int maxScale = 0;

            foreach (var value in nonNullValues)
            {
                var trimmed = value.Trim();
                if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    numericValues.Add(d);
                    numericCount++;
                    int scale = DecimalScale(d);
                    if (scale > maxScale) maxScale = scale;
                }
                else
                {
                    nonNumericCount++;
                }
            }

            bool hasNumericSummary = numericCount > 0 && nonNumericCount == 0;

            string sumText = PlaceholderText;
            string averageText = PlaceholderText;
            string minText = PlaceholderText;
            string maxText = PlaceholderText;
            string minMaxKind = null;
            string unavailableReason = null;

            if (hasNumericSummary)
            {
                decimal sum = 0m;
                foreach (var d in numericValues) sum += d;
                decimal average = sum / numericValues.Count;

                int averageCap = Math.Min(Math.Max(maxScale, 2), 6);

                sumText = FormatTrimmed(sum, maxScale, outputCulture);
                averageText = FormatTrimmed(average, averageCap, outputCulture);
                minText = FormatTrimmed(numericValues.Min(), maxScale, outputCulture);
                maxText = FormatTrimmed(numericValues.Max(), maxScale, outputCulture);
                minMaxKind = "numeric";
            }
            else if (count > 0)
            {
                unavailableReason = nonNumericCount == 1
                    ? "1 value isn't a number"
                    : nonNumericCount + " values aren't numbers";

                // Min/Max by date only when EVERY non-NULL value parses as one; otherwise text.
                var dates = new List<DateTime>(nonNullValues.Count);
                bool allDates = true;
                foreach (var value in nonNullValues)
                {
                    if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        dates.Add(dt);
                    else { allDates = false; break; }
                }

                if (allDates)
                {
                    minMaxKind = "date";
                    minText = dates.Min().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    maxText = dates.Max().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
                else
                {
                    minMaxKind = "text";
                    var comparer = StringComparer.Create(outputCulture, ignoreCase: false);
                    minText = nonNullValues.OrderBy(v => v, comparer).First();
                    maxText = nonNullValues.OrderBy(v => v, comparer).Last();
                }
            }

            string countText = count.ToString("N0", outputCulture);
            string distinctText = distinct.ToString("N0", outputCulture);
            string cellCountText = cellCount.ToString("N0", outputCulture);

            string contextLine = null;
            if (nullCount > 0 || nonNumericCount > 0)
            {
                var parts = new List<string>
                {
                    cellCountText + " cell" + (cellCount == 1 ? "" : "s") + " selected"
                };
                if (nullCount > 0) parts.Add(nullCount.ToString("N0", outputCulture) + " NULL");
                if (nonNumericCount > 0) parts.Add(nonNumericCount.ToString("N0", outputCulture) + " not numeric");
                contextLine = string.Join(" · ", parts);
            }

            return new SelectionAggregationResult(
                cellCount, nullCount, count, distinct,
                numericCount, nonNumericCount, hasNumericSummary, minMaxKind,
                sumText, averageText, minText, maxText,
                countText, distinctText, cellCountText,
                unavailableReason, contextLine);
        }

        /// <summary>The scale (digits after the decimal point) System.Decimal itself parsed
        /// the value with — e.g. "252" → 0, "125.10" → 2. Read straight from the decimal's own
        /// bit representation rather than re-deriving it from text, so it matches exactly what
        /// decimal.TryParse actually stored.</summary>
        private static int DecimalScale(decimal value)
        {
            return (decimal.GetBits(value)[3] >> 16) & 0x7F;
        }

        /// <summary>Formats with "N{decimals}" using outputCulture (thousands separators +
        /// the culture's own decimal point, per the lead's design), then trims trailing zero
        /// decimals down to the minimum that still round-trips — so an exact integer result
        /// (e.g. an average of 126.00) prints as "126", not "126.00", while a genuine fraction
        /// (e.g. 1234567.5) keeps exactly the digits it needs.</summary>
        private static string FormatTrimmed(decimal value, int maxDecimals, CultureInfo culture)
        {
            decimal rounded = Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero);
            int scale = maxDecimals;
            while (scale > 0)
            {
                decimal test = Math.Round(rounded, scale - 1, MidpointRounding.AwayFromZero);
                if (test != rounded) break;
                scale--;
            }
            return rounded.ToString("N" + scale, culture);
        }
    }
}
