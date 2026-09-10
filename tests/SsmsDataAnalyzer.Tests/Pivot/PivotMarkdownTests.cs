using System.Collections.Generic;
using SsmsDataAnalyzer.Core.Pivot;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Pivot
{
    public class PivotMarkdownTests
    {
        [Fact]
        public void Build_SimpleTable_HasHeaderAndSeparatorAndRows()
        {
            var headers = new[] { "Column", "Row 3", "Row 7" };
            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "ID", "4521", "4522" },
                new[] { "Note", "NULL", "Storno" },
            };

            var markdown = PivotMarkdown.Build(headers, rows);

            var expected =
                "| Column | Row 3 | Row 7 |\n" +
                "| --- | --- | --- |\n" +
                "| ID | 4521 | 4522 |\n" +
                "| Note | NULL | Storno |\n";
            Assert.Equal(expected, markdown);
        }

        [Fact]
        public void Build_PipeInCell_IsEscaped()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>> { new[] { "Note", "a|b" } };

            var markdown = PivotMarkdown.Build(headers, rows);

            Assert.Contains("a\\|b", markdown);
            Assert.DoesNotContain("a|b |", markdown); // unescaped pipe would end the cell early
        }

        [Fact]
        public void Build_NewlinesInCell_BecomeSpaces()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>> { new[] { "Note", "line1\r\nline2\nline3" } };

            var markdown = PivotMarkdown.Build(headers, rows);

            Assert.Contains("line1 line2 line3", markdown);
            Assert.DoesNotContain("\r", markdown);
            Assert.DoesNotContain("\n\n", markdown); // no embedded blank line inside the cell
        }

        [Fact]
        public void Build_EmptyAndNullCells_RenderAsEmpty()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>> { new[] { "Note", null }, new[] { "Other", "" } };

            var markdown = PivotMarkdown.Build(headers, rows);

            Assert.Contains("| Note |  |\n", markdown);
            Assert.Contains("| Other |  |\n", markdown);
        }

        [Fact]
        public void Build_UnicodeCells_PassThroughUnchanged()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>> { new[] { "Ime", "Marič Žabar č ž" } };

            var markdown = PivotMarkdown.Build(headers, rows);

            Assert.Contains("Marič Žabar č ž", markdown);
        }

        [Fact]
        public void Build_SingleRow_ProducesThreeLines()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>> { new[] { "ID", "1" } };

            var markdown = PivotMarkdown.Build(headers, rows);

            var lines = markdown.TrimEnd('\n').Split('\n');
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public void Build_NoRows_ProducesHeaderAndSeparatorOnly()
        {
            var headers = new[] { "Column", "Row 1" };
            var rows = new List<IReadOnlyList<string>>();

            var markdown = PivotMarkdown.Build(headers, rows);

            var lines = markdown.TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
        }
    }
}
