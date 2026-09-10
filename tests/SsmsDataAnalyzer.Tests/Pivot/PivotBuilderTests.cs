using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.Pivot;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Pivot
{
    public class PivotBuilderTests
    {
        private static readonly string[] Columns = { "Id", "Name" };

        private static Func<long, int, string> Cell(Dictionary<(long, int), string> data)
        {
            return (row, ordinal) => data.TryGetValue((row, ordinal), out var v) ? v : "x";
        }

        [Fact]
        public void Build_NullColumnNames_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PivotBuilder.Build(null, new[] { new RowRange(0, 0) }, 10, (r, c) => ""));
        }

        [Fact]
        public void Build_NullReadCell_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PivotBuilder.Build(Columns, new[] { new RowRange(0, 0) }, 10, null));
        }

        [Fact]
        public void Build_EmptySelection_ProducesEmptyResultWithoutException()
        {
            var result = PivotBuilder.Build(Columns, new RowRange[0], 10, (r, c) => "v");

            Assert.Empty(result.Rows);
            Assert.Equal(2, result.Columns.Count);
            Assert.Empty(result.Values[0]);
            Assert.Empty(result.Values[1]);
            Assert.False(result.Truncated);
            Assert.Equal(0, result.TotalSelectedRows);
        }

        [Fact]
        public void Build_NullSelection_ProducesEmptyResultWithoutException()
        {
            var result = PivotBuilder.Build(Columns, null, 10, (r, c) => "v");
            Assert.Empty(result.Rows);
        }

        [Theory]
        [InlineData(0, PivotBuilder.MinRowLimit)]
        [InlineData(10000, PivotBuilder.MaxRowLimit)]
        [InlineData(-5, PivotBuilder.MinRowLimit)]
        public void Build_RowLimit_IsClamped(int requested, int expectedRowsWhenSelectionIsHuge)
        {
            var selection = new[] { new RowRange(0, 999_999) };
            var result = PivotBuilder.Build(Columns, selection, requested, (r, c) => "v");

            Assert.Equal(expectedRowsWhenSelectionIsHuge, result.Rows.Count);
        }

        [Fact]
        public void Build_MoreSelectedThanLimit_Truncates()
        {
            var selection = new[] { new RowRange(0, 199) };
            var result = PivotBuilder.Build(Columns, selection, 50, (r, c) => "v");

            Assert.Equal(50, result.Rows.Count);
            Assert.Equal(200, result.TotalSelectedRows);
            Assert.True(result.Truncated);
        }

        [Fact]
        public void Build_ReadCell_CalledOnlyForPivotedRows_OncePerCell()
        {
            var calls = new List<(long row, int ordinal)>();
            Func<long, int, string> readCell = (row, ordinal) =>
            {
                calls.Add((row, ordinal));
                return "v";
            };

            // 10 rows selected, limit 3: only rows 0,1,2 should ever be read, across both columns.
            var selection = new[] { new RowRange(0, 9) };
            var result = PivotBuilder.Build(Columns, selection, 3, readCell);

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(6, calls.Count); // 3 rows x 2 columns, each exactly once
            Assert.All(calls, c => Assert.True(c.row < 3));
            Assert.Contains((0L, 1), calls);
            Assert.Contains((0L, 2), calls);
            Assert.Contains((2L, 1), calls);
            Assert.Contains((2L, 2), calls);
        }

        [Fact]
        public void Build_GridOrdinal_IsOneBasedFromColumnIndex()
        {
            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 0) }, 10, (r, c) => "v");
            Assert.Equal(1, result.Columns[0].GridOrdinal);
            Assert.Equal(2, result.Columns[1].GridOrdinal);
        }

        [Fact]
        public void Build_NullCellText_BecomesEmptyString()
        {
            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 0) }, 10, (r, c) => null);
            Assert.Equal("", result.Values[0][0]);
            Assert.Equal("", result.Values[1][0]);
        }

        [Fact]
        public void Build_ValuesIndexing_IsColumnThenRow()
        {
            var data = new Dictionary<(long, int), string>
            {
                [(0L, 1)] = "id0",
                [(0L, 2)] = "name0",
                [(1L, 1)] = "id1",
                [(1L, 2)] = "name1",
            };

            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 1) }, 10, Cell(data));

            Assert.Equal("id0", result.Values[0][0]);
            Assert.Equal("id1", result.Values[0][1]);
            Assert.Equal("name0", result.Values[1][0]);
            Assert.Equal("name1", result.Values[1][1]);
        }

        [Fact]
        public void Build_OneRow_DiffersIsFalse()
        {
            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 0) }, 10, (r, c) => "same");
            Assert.All(result.Columns, col => Assert.False(col.Differs));
            Assert.Equal(0, result.DifferingColumnCount);
        }

        [Fact]
        public void Build_TwoRowsSameValue_DiffersIsFalse()
        {
            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 1) }, 10, (r, c) => "same");
            Assert.All(result.Columns, col => Assert.False(col.Differs));
        }

        [Fact]
        public void Build_TwoRowsDifferentValue_DiffersIsTrue()
        {
            var data = new Dictionary<(long, int), string>
            {
                [(0L, 1)] = "a",
                [(1L, 1)] = "b",
                [(0L, 2)] = "same",
                [(1L, 2)] = "same",
            };

            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 1) }, 10, Cell(data));

            Assert.True(result.Columns[0].Differs);
            Assert.False(result.Columns[1].Differs);
            Assert.Equal(1, result.DifferingColumnCount);
        }

        [Fact]
        public void Build_AllNull_TrueWhenEveryValueIsNullText()
        {
            var result = PivotBuilder.Build(Columns, new[] { new RowRange(0, 2) }, 10, (r, c) => PivotBuilder.NullDisplayText);
            Assert.All(result.Columns, col => Assert.True(col.AllNull));
        }

        [Fact]
        public void Build_AllNull_FalseWhenOneValueDiffers()
        {
            var data = new Dictionary<(long, int), string>
            {
                [(0L, 1)] = PivotBuilder.NullDisplayText,
                [(1L, 1)] = "not null",
            };
            var result = PivotBuilder.Build(new[] { "Id" }, new[] { new RowRange(0, 1) }, 10, Cell(data));
            Assert.False(result.Columns[0].AllNull);
        }

        [Fact]
        public void Build_DuplicateColumnNames_GetSuffixedDisplayNames()
        {
            var columns = new[] { "Name", "Name", "Name" };
            var result = PivotBuilder.Build(columns, new[] { new RowRange(0, 0) }, 10, (r, c) => "v");

            Assert.Equal("Name", result.Columns[0].DisplayName);
            Assert.Equal("Name (2)", result.Columns[1].DisplayName);
            Assert.Equal("Name (3)", result.Columns[2].DisplayName);

            // The underlying Name is untouched -- only DisplayName is deduplicated.
            Assert.All(result.Columns, col => Assert.Equal("Name", col.Name));
        }

        [Fact]
        public void Build_DuplicateColumnNames_SkipCollisionWithRealColumnNamedWithSuffix()
        {
            // A literal "Name (2)" column sits between the two "Name" duplicates: the
            // generated suffix for the second "Name" must skip past it.
            var columns = new[] { "Name", "Name (2)", "Name" };
            var result = PivotBuilder.Build(columns, new[] { new RowRange(0, 0) }, 10, (r, c) => "v");

            Assert.Equal("Name", result.Columns[0].DisplayName);
            Assert.Equal("Name (2)", result.Columns[1].DisplayName);
            Assert.Equal("Name (3)", result.Columns[2].DisplayName);
        }

        [Fact]
        public void Build_SingleColumnNoDuplicates_KeepsOwnName()
        {
            var result = PivotBuilder.Build(new[] { "OnlyOne" }, new[] { new RowRange(0, 0) }, 10, (r, c) => "v");
            Assert.Equal("OnlyOne", result.Columns[0].DisplayName);
        }
    }
}
