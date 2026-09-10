using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ResultShape;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ResultShape
{
    /// <summary>
    /// Pins the pure half of results-grid Go to source (CONTRACT.md Amendments 16/17) after
    /// the docs/pivot-plan.md item 13 split. Decline messages are asserted verbatim: they are
    /// the exact texts the resolver produced before the split and are user-visible.
    /// </summary>
    public class ResultShapeMatcherTests
    {
        private static DescribedColumn Col(int ordinal, string name, string table = "FkChild", string column = null,
            string schema = "dbo", string db = "Db", bool? hidden = false, int? error = null) =>
            new DescribedColumn
            {
                Ordinal = ordinal,
                Name = name,
                SourceDatabase = table == null ? null : db,
                SourceSchema = table == null ? null : schema,
                SourceTable = table,
                SourceColumn = table == null ? null : (column ?? name),
                IsHidden = hidden,
                ErrorNumber = error,
                SystemTypeName = "int",
                MaxLength = 4
            };

        private static IReadOnlyList<IReadOnlyList<DescribedColumn>> Batches(params DescribedColumn[][] batches) => batches;

        private static readonly string[] AB = { "A", "B" };

        [Fact]
        public void Match_SingleBatchFullShape_Matches()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "A"), Col(2, "B") }), 2, AB, 0, null);

            Assert.True(m.IsMatch);
            Assert.Null(m.DeclineMessage);
            Assert.Single(m.Matches);
            Assert.Equal(0, m.Matches[0].BatchIndex);
            Assert.Empty(m.DiagnosticDumps);
        }

        [Fact]
        public void Match_BrowseInfoHiddenRows_AreIgnoredForShape()
        {
            var batch = new[] { Col(1, "A"), Col(3, "PkHidden", hidden: true), Col(2, "B") };
            var m = ResultShapeMatcher.Match(Batches(batch), 2, AB, 0, null);

            Assert.True(m.IsMatch);
            Assert.Equal(new[] { 1, 2 }, new[] { m.Matches[0].Rows[0].Ordinal, m.Matches[0].Rows[1].Ordinal });
        }

        [Fact]
        public void Match_UseThenSelect_MatchesOnlyTheSelectBatch()
        {
            var m = ResultShapeMatcher.Match(Batches(new DescribedColumn[0], new[] { Col(1, "A"), Col(2, "B") }), 2, AB, 0, null);

            Assert.True(m.IsMatch);
            Assert.Single(m.Matches);
            Assert.Equal(1, m.Matches[0].BatchIndex);
        }

        [Fact]
        public void Match_ErrorRowWithNullIsHidden_IsCountedAsErrored()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(0, null, table: null, hidden: null, error: 11525) }), 1, new[] { "Id" }, 0, null);

            Assert.False(m.IsMatch);
            Assert.Equal("Go to source: the query text has 1 batch(es) and none produced a result matching this grid's 1 columns (1 errored — SQL Server error 11525) — declined rather than risk the wrong table.", m.DeclineMessage);
        }

        [Fact]
        public void Match_ErrorRow_DeclineQuotesSqlServerMessage_Trimmed()
        {
            var errorRow = Col(0, null, table: null, hidden: null, error: 11529);
            errorRow.ErrorMessage = "  The metadata could not be determined. " + new string('x', 300);

            var m = ResultShapeMatcher.Match(Batches(new[] { errorRow }), 1, new[] { "Id" }, 0, null);

            Assert.False(m.IsMatch);
            Assert.Contains("(1 errored — SQL Server error 11529: The metadata could not be determined. ", m.DeclineMessage);
            Assert.Contains("x…)", m.DeclineMessage);
            // Trimmed to 200 characters of message, so the 300 x's never all appear.
            Assert.DoesNotContain(new string('x', 200), m.DeclineMessage);
            // Never logged: error text can quote the query, so it is not in the diagnostic dumps.
            Assert.Empty(m.DiagnosticDumps);
        }

        [Fact]
        public void Match_SingleNameMismatch_NamesOrdinalAndBothNames()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "A"), Col(2, "B") }), 2, new[] { "A", "X" }, 0, null);

            Assert.False(m.IsMatch);
            Assert.Equal("Go to source: column 2 is named 'B' in the query but 'X' on screen — declined rather than risk the wrong table.", m.DeclineMessage);
            Assert.Single(m.DiagnosticDumps);
            Assert.StartsWith("Go to source (name mismatch) — full describe dump for batch 1 of 1:\n  ordinal=1 name='A' is_hidden=False error_number=NULL", m.DiagnosticDumps[0]);
        }

        [Fact]
        public void Match_SingleNameMismatch_InMultiBatchText_AddsBatchNote()
        {
            var m = ResultShapeMatcher.Match(
                Batches(new[] { Col(0, null, table: null, hidden: null, error: 208) }, new[] { Col(1, "A"), Col(2, "B") }),
                2, new[] { "A", "X" }, 0, null);

            Assert.Equal("Go to source: column 2 is named 'B' in the query but 'X' on screen (batch 2 of 2) — declined rather than risk the wrong table.", m.DeclineMessage);
        }

        [Fact]
        public void Match_SingleCountMismatch_NamesBothCountsAndFirstDivergence()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "A"), Col(2, "B"), Col(3, "C") }), 2, new[] { "A", "C" }, 0, null);

            Assert.Equal("Go to source: the query describes 3 column(s) but the grid shows 2 — declined rather than risk the wrong table. First divergence at column 2: described 'B', grid 'C'.", m.DeclineMessage);
        }

        [Fact]
        public void Match_SeveralMismatchingBatches_GenericMessageWithBreakdown()
        {
            var m = ResultShapeMatcher.Match(Batches(new DescribedColumn[0], new[] { Col(1, "A") }), 2, AB, 0, null);

            Assert.Equal("Go to source: the query text has 2 batch(es) and none produced a result matching this grid's 2 columns (2 had a different column count) — declined rather than risk the wrong table.", m.DeclineMessage);
            Assert.Equal(2, m.DiagnosticDumps.Count);
        }

        [Fact]
        public void Match_UnnamedColumn_MatchesGridNoColumnName()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, null, table: null) }), 1, new[] { "(No column name)" }, 0, null);

            Assert.True(m.IsMatch);
        }

        [Fact]
        public void Match_DegradedCaller_ChecksOnlyTheClickedColumnName()
        {
            var batch = new[] { Col(1, "Whatever"), Col(2, "B") };

            Assert.True(ResultShapeMatcher.Match(Batches(batch), 2, null, 2, "B").IsMatch);
            Assert.Equal(
                "Go to source: column 2 is named 'B' in the query but 'Z' on screen — declined rather than risk the wrong table.",
                ResultShapeMatcher.Match(Batches(batch), 2, null, 2, "Z").DeclineMessage);
        }

        [Fact]
        public void ResolveColumn_TwoAgreeingBatches_SucceedsWithMatchCount()
        {
            var m = ResultShapeMatcher.Match(
                Batches(new[] { Col(1, "A", column: "SingleFkCol") }, new[] { Col(1, "A", column: "SINGLEFKCOL", schema: null) }),
                1, new[] { "A" }, 0, null);

            var r = ResultShapeMatcher.ResolveColumn(m, 1, "A");

            Assert.True(r.Succeeded);
            Assert.Equal(2, r.MatchCount);
            Assert.Equal("SingleFkCol", r.Described.SourceColumn);
        }

        [Fact]
        public void ResolveColumn_DisagreeingBatches_DeclinesNamingBothSources()
        {
            var m = ResultShapeMatcher.Match(
                Batches(new[] { Col(1, "X", column: "SingleFkCol") }, new[] { Col(1, "X", table: "SelfRefTable", column: "ParentId") }),
                1, new[] { "X" }, 0, null);

            var r = ResultShapeMatcher.ResolveColumn(m, 1, "X");

            Assert.False(r.Succeeded);
            Assert.Equal("Go to source: 'X' does not resolve the same way across the query's matching batches — Db.dbo.FkChild.SingleFkCol vs. Db.dbo.SelfRefTable.ParentId — declined rather than risk the wrong table.", r.DeclineMessage);
        }

        [Fact]
        public void ResolveColumn_ComputedExpression_Declines()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "Id"), Col(2, "Total", table: null) }), 2, new[] { "Id", "Total" }, 0, null);

            Assert.True(ResultShapeMatcher.ResolveColumn(m, 1, "Id").Succeeded);
            var r = ResultShapeMatcher.ResolveColumn(m, 2, "Total");
            Assert.False(r.Succeeded);
            Assert.Equal("Go to source: 'Total' is a computed expression — it has no base table.", r.DeclineMessage);
        }

        [Fact]
        public void ResolveColumn_DottedTableName_IsKeptAsOneIdentifier()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "Id", table: "Intervention.ABB.Request.Change.History") }), 1, new[] { "Id" }, 0, null);

            var r = ResultShapeMatcher.ResolveColumn(m, 1, "Id");

            Assert.True(r.Succeeded);
            Assert.Equal("Intervention.ABB.Request.Change.History", r.Described.SourceTable);
        }

        [Fact]
        public void ResolveColumn_OrdinalOutsideShape_Declines()
        {
            var m = ResultShapeMatcher.Match(Batches(new[] { Col(1, "A") }), 1, new[] { "A" }, 0, null);

            Assert.Equal("Go to source: could not match this column to the described query.", ResultShapeMatcher.ResolveColumn(m, 5, "?").DeclineMessage);
        }

        [Fact]
        public void ResolveColumn_OnDeclinedShape_Throws()
        {
            Assert.Throws<ArgumentException>(() => ResultShapeMatcher.ResolveColumn(ShapeMatch.Declined("no"), 1, "A"));
        }
    }
}
