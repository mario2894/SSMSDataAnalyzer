using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.ResultShape;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ResultShape
{
    /// <summary>
    /// Multi-statement batches: sys.dm_exec_describe_first_result_set only describes the FIRST
    /// result set, so grids 2..N need per-statement candidates. Pins the conservative candidate
    /// rules (StatementCandidateBuilder) and how ResultShapeMatcher.MatchCandidates matches,
    /// dedupes and declines over them.
    /// </summary>
    public class StatementCandidateTests
    {
        // ---- a tiny "parser": statements separated by ";" at top level, kind by first word ----

        private static ParsedBatch Batch(string text, bool nestedContextChange = false)
        {
            var statements = new List<ParsedStatement>();
            int pos = 0;
            foreach (var part in text.Split(';'))
            {
                int lead = part.Length - part.TrimStart().Length;
                string body = part.Trim();
                if (body.Length > 0)
                    statements.Add(new ParsedStatement(KindOf(body), pos + lead, body.Length));
                pos += part.Length + 1;
            }
            return new ParsedBatch(text, statements, nestedContextChange);
        }

        private static StatementKind KindOf(string body)
        {
            string upper = body.ToUpperInvariant();
            if (upper.StartsWith("SELECT") && !upper.Contains(" INTO ")) return StatementKind.ResultSetSelect;
            if (upper.StartsWith("WITH")) return StatementKind.ResultSetSelect;
            if (upper.StartsWith("DECLARE")) return StatementKind.Declare;
            if (upper.StartsWith("USE") || upper.StartsWith("EXECUTE AS") || upper.StartsWith("REVERT")) return StatementKind.ContextChange;
            return StatementKind.Other;
        }

        private static List<string> Texts(CandidateSet set) => set.Candidates.Select(c => c.Text).ToList();

        // ---- StatementCandidateBuilder ----

        [Fact]
        public void Build_ThreeSelects_WholeBatchPlusOnePerStatement()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A FROM T1; SELECT B FROM T2; SELECT C FROM T3") });

            Assert.Equal(new[] { "SELECT A FROM T1; SELECT B FROM T2; SELECT C FROM T3", "SELECT A FROM T1", "SELECT B FROM T2", "SELECT C FROM T3" }, Texts(set));
            Assert.Equal(new int?[] { null, 1, 2, 3 }, set.Candidates.Select(c => c.StatementNumber).ToArray());
            Assert.Equal(3, set.StatementCandidateCount);
            Assert.False(set.CapReached);
        }

        [Fact]
        public void Build_SingleStatementBatch_WholeBatchOnly()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A FROM T1") });

            Assert.Equal(new[] { "SELECT A FROM T1" }, Texts(set));
            Assert.Equal(0, set.StatementCandidateCount);
        }

        [Fact]
        public void Build_Unparsed_WholeBatchOnly()
        {
            var set = StatementCandidateBuilder.Build(new[] { ParsedBatch.Unparsed("SELECT A; SELECT B") });

            Assert.Equal(new[] { "SELECT A; SELECT B" }, Texts(set));
        }

        [Fact]
        public void Build_EarlierDeclaresArePrefixedInOrder_LaterOnesAreNot()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("DECLARE @x int = 5; SELECT A FROM T WHERE Id = @x; DECLARE @y int; SELECT B FROM T WHERE Id = @y") });

            Assert.Equal("DECLARE @x int = 5;\r\nSELECT A FROM T WHERE Id = @x", set.Candidates[1].Text);
            Assert.Equal("DECLARE @x int = 5;\r\nDECLARE @y int;\r\nSELECT B FROM T WHERE Id = @y", set.Candidates[2].Text);
        }

        [Fact]
        public void Build_OtherStatementsAreNeitherCandidatesNorPrefixes()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A INTO #t FROM T; SET NOCOUNT ON; SELECT B FROM U") });

            Assert.Equal(new[] { "SELECT A INTO #t FROM T; SET NOCOUNT ON; SELECT B FROM U", "SELECT B FROM U" }, Texts(set));
            Assert.Equal(3, set.Candidates[1].StatementNumber);
        }

        [Fact]
        public void Build_UseInBatch_OnlyStatementsAfterTheLastContextChange()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A FROM T; USE Other; SELECT B FROM U; SELECT C FROM V") });

            Assert.Equal(new[] { "SELECT B FROM U", "SELECT C FROM V" }, Texts(set).Skip(1));
        }

        [Fact]
        public void Build_ContextChangeInLaterBatch_EarlierBatchesGetNoStatementCandidates()
        {
            var set = StatementCandidateBuilder.Build(new[]
            {
                Batch("SELECT A FROM T; SELECT B FROM U"),
                Batch("EXECUTE AS USER = 'x'"),
                Batch("SELECT C FROM V; SELECT D FROM W"),
            });

            Assert.Equal(new[] { "SELECT A FROM T; SELECT B FROM U", "EXECUTE AS USER = 'x'", "SELECT C FROM V; SELECT D FROM W", "SELECT C FROM V", "SELECT D FROM W" }, Texts(set));
            Assert.Equal(new[] { 0, 1, 2, 2, 2 }, set.Candidates.Select(c => c.BatchIndex).ToArray());
        }

        [Fact]
        public void Build_UnparsedLaterBatch_EarlierBatchesGetNoStatementCandidates()
        {
            var set = StatementCandidateBuilder.Build(new[]
            {
                Batch("SELECT A FROM T; SELECT B FROM U"),
                ParsedBatch.Unparsed("SELEC oops"),
                Batch("SELECT C FROM V; SELECT D FROM W"),
            });

            Assert.Equal(2, set.StatementCandidateCount);
            Assert.All(set.Candidates.Where(c => !c.IsWholeBatch), c => Assert.Equal(2, c.BatchIndex));
        }

        [Fact]
        public void Build_NestedContextChange_ThatBatchAndEarlierGetNoStatementCandidates()
        {
            var set = StatementCandidateBuilder.Build(new[]
            {
                Batch("SELECT A FROM T; SELECT B FROM U"),
                Batch("IF 1 = 1 BEGIN USE Other END; SELECT C FROM V; SELECT D FROM W", nestedContextChange: true),
            });

            Assert.Equal(0, set.StatementCandidateCount);
        }

        [Fact]
        public void Build_Cap_StopsAddingAndReportsIt()
        {
            string text = string.Join("; ", Enumerable.Range(1, 5).Select(i => "SELECT C" + i + " FROM T"));
            var set = StatementCandidateBuilder.Build(new[] { Batch(text), Batch(text) }, maxStatementCandidates: 7);

            Assert.Equal(7, set.StatementCandidateCount);
            Assert.True(set.CapReached);
            Assert.Equal(7, set.StatementCap);
            Assert.Equal(2, set.Candidates.Count(c => c.IsWholeBatch));
        }

        [Fact]
        public void Build_DefaultCapIsFifty()
        {
            string text = string.Join("; ", Enumerable.Range(1, 60).Select(i => "SELECT C" + i + " FROM T"));
            var set = StatementCandidateBuilder.Build(new[] { Batch(text) });

            Assert.Equal(50, set.StatementCandidateCount);
            Assert.True(set.CapReached);
        }

        // ---- ResultShapeMatcher.MatchCandidates ----

        private static DescribedColumn Col(int ordinal, string name, string table, string column = null) =>
            new DescribedColumn
            {
                Ordinal = ordinal, Name = name, SourceDatabase = "Db", SourceSchema = "dbo",
                SourceTable = table, SourceColumn = column ?? name, IsHidden = false, SystemTypeName = "int", MaxLength = 4
            };

        private static DescribedColumn Error(int number, string message) =>
            new DescribedColumn { Ordinal = 0, ErrorNumber = number, ErrorMessage = message, IsHidden = null };

        private static IReadOnlyList<IReadOnlyList<DescribedColumn>> Described(params DescribedColumn[][] rows) => rows;

        private static CandidateSet ThreeSelects() =>
            StatementCandidateBuilder.Build(new[] { Batch("SELECT A, B FROM T1; SELECT X FROM T2; SELECT A, B FROM T3") });

        [Fact]
        public void MatchCandidates_GridOfSecondStatement_MatchesOnlyThatStatement()
        {
            var set = ThreeSelects();
            var described = Described(
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },   // whole batch = first result set
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },
                new[] { Col(1, "X", "T2") },
                new[] { Col(1, "A", "T3"), Col(2, "B", "T3") });

            var m = ResultShapeMatcher.MatchCandidates(set, described, 1, new[] { "X" }, 0, null);

            Assert.True(m.IsMatch);
            var only = Assert.Single(m.Matches);
            Assert.Equal(2, only.StatementNumber);
            Assert.Equal("T2", ResultShapeMatcher.ResolveColumn(m, 1, "X").Described.SourceTable);
        }

        [Fact]
        public void MatchCandidates_WholeBatchAndIdenticalFirstStatement_CountOnce()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A FROM T1; SELECT Y FROM T2") });
            var described = Described(new[] { Col(1, "A", "T1") }, new[] { Col(1, "A", "T1") }, new[] { Col(1, "Y", "T2") });

            var m = ResultShapeMatcher.MatchCandidates(set, described, 1, new[] { "A" }, 0, null);

            var r = ResultShapeMatcher.ResolveColumn(m, 1, "A");
            Assert.True(r.Succeeded);
            Assert.Equal(1, r.MatchCount);
        }

        [Fact]
        public void MatchCandidates_IdenticalShapeSameTable_Agrees()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT Y FROM T2 WHERE Id = 1; SELECT A FROM T1 WHERE Id = 1; SELECT A FROM T1 WHERE Id = 2") });
            var described = Described(new[] { Col(1, "Y", "T2") }, new[] { Col(1, "Y", "T2") }, new[] { Col(1, "A", "T1") }, new[] { Col(1, "A", "T1") });

            var m = ResultShapeMatcher.MatchCandidates(set, described, 1, new[] { "A" }, 0, null);

            var r = ResultShapeMatcher.ResolveColumn(m, 1, "A");
            Assert.True(r.Succeeded);
            Assert.Equal("T1", r.Described.SourceTable);
        }

        [Fact]
        public void MatchCandidates_SameNamesDifferentTables_DeclinesOnDisagreement()
        {
            var set = ThreeSelects();
            var described = Described(
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },
                new[] { Col(1, "X", "T2") },
                new[] { Col(1, "A", "T3"), Col(2, "B", "T3") });

            var m = ResultShapeMatcher.MatchCandidates(set, described, 2, new[] { "A", "B" }, 0, null);

            Assert.True(m.IsMatch);
            Assert.Equal(2, m.Matches.Count);
            var r = ResultShapeMatcher.ResolveColumn(m, 1, "A");
            Assert.False(r.Succeeded);
            Assert.Equal("Go to source: 'A' does not resolve the same way across the query's matching batches — Db.dbo.T1.A vs. Db.dbo.T3.A — declined rather than risk the wrong table.", r.DeclineMessage);
        }

        [Fact]
        public void MatchCandidates_NothingMatches_ReportsStatementsNotOneArbitraryMismatch()
        {
            var set = ThreeSelects();
            var described = Described(
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },
                new[] { Col(1, "A", "T1"), Col(2, "B", "T1") },
                new[] { Col(1, "X", "T2") },
                new[] { Error(208, "Invalid object name 'T3'.") });

            var m = ResultShapeMatcher.MatchCandidates(set, described, 86, Enumerable.Repeat("Q", 86).ToArray(), 0, null);

            Assert.False(m.IsMatch);
            Assert.Equal("Go to source: none of the 3 statement(s) in the query that ran produced a result matching this grid's 86 columns (2 had a different column count, 1 errored — SQL Server error 208: Invalid object name 'T3'.) — declined rather than risk the wrong table.", m.DeclineMessage);
            Assert.Equal(2, m.DiagnosticDumps.Count);
        }

        [Fact]
        public void MatchCandidates_UnsplitBatchCountsAsOneStatement_AndCapIsNoted()
        {
            string three = "SELECT A FROM T; SELECT B FROM T; SELECT C FROM T";
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT Z FROM T"), Batch(three) }, maxStatementCandidates: 2);
            var described = set.Candidates.Select(_ => (IReadOnlyList<DescribedColumn>)new[] { Col(1, "A", "T") }).ToList();

            var m = ResultShapeMatcher.MatchCandidates(set, described, 1, new[] { "Nope" }, 0, null);

            Assert.Equal("Go to source: none of the 3 statement(s) in the query that ran produced a result matching this grid's 1 columns (3 had different column names, only the first 2 statements were checked) — declined rather than risk the wrong table.", m.DeclineMessage);
        }

        [Fact]
        public void MatchCandidates_NoStatementCandidates_IsExactlyMatch()
        {
            var set = StatementCandidateBuilder.Build(new[] { Batch("SELECT A, B, C FROM T") });
            var described = Described(new[] { Col(1, "A", "T"), Col(2, "B", "T"), Col(3, "C", "T") });

            var viaCandidates = ResultShapeMatcher.MatchCandidates(set, described, 2, new[] { "A", "C" }, 0, null);
            var viaMatch = ResultShapeMatcher.Match(described, 2, new[] { "A", "C" }, 0, null);

            Assert.Equal(viaMatch.DeclineMessage, viaCandidates.DeclineMessage);
            Assert.Equal("Go to source: the query describes 3 column(s) but the grid shows 2 — declined rather than risk the wrong table. First divergence at column 2: described 'B', grid 'C'.", viaCandidates.DeclineMessage);
        }
    }
}
