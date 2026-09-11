using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.ResultShape
{
    /// <summary>What a TOP-LEVEL statement of a GO batch means for describe-candidate building.
    /// Produced by a parser adapter (the Vsix uses SSMS's own ScriptDom); Core never parses.</summary>
    public enum StatementKind
    {
        /// <summary>Anything else (SET, INSERT, EXEC, IF/WHILE/BEGIN…END blocks, SELECT … INTO,
        /// variable-assigning SELECT, …). Never becomes a candidate, never a prefix.</summary>
        Other = 0,
        /// <summary>A SELECT (CTE included) that returns a result set to the client.</summary>
        ResultSetSelect,
        /// <summary>DECLARE @variable / DECLARE @table TABLE — prefixed to later candidates in
        /// the same batch so their variable references still compile.</summary>
        Declare,
        /// <summary>USE / EXECUTE AS / REVERT / SETUSER: changes the database or default schema
        /// a later unqualified name resolves against. A statement described on its own would
        /// lose that context, so nothing before one gets a statement candidate.</summary>
        ContextChange
    }

    /// <summary>One top-level statement: its kind and its span in the batch text it was parsed from.</summary>
    public sealed class ParsedStatement
    {
        public ParsedStatement(StatementKind kind, int startOffset, int length)
        {
            if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Kind = kind;
            StartOffset = startOffset;
            Length = length;
        }

        public StatementKind Kind { get; }
        public int StartOffset { get; }
        public int Length { get; }
    }

    /// <summary>One GO batch as handed to <see cref="StatementCandidateBuilder.Build"/>.</summary>
    public sealed class ParsedBatch
    {
        /// <param name="text">The batch text exactly as described/parsed (offsets refer to it).</param>
        /// <param name="statements">Top-level statements in order, or null when the batch could
        /// not be parsed (parser unavailable, parse errors, anything unexpected).</param>
        /// <param name="containsNestedContextChange">True when a USE / EXECUTE AS / REVERT /
        /// SETUSER appears anywhere below the top level (inside IF, BEGIN…END, TRY, …).</param>
        public ParsedBatch(string text, IReadOnlyList<ParsedStatement> statements, bool containsNestedContextChange)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Statements = statements;
            ContainsNestedContextChange = containsNestedContextChange;
        }

        public string Text { get; }
        public IReadOnlyList<ParsedStatement> Statements { get; }
        public bool ContainsNestedContextChange { get; }

        /// <summary>The fallback shape: no statement information at all.</summary>
        public static ParsedBatch Unparsed(string text) => new ParsedBatch(text, null, false);
    }

    /// <summary>One text to describe with sys.dm_exec_describe_first_result_set.</summary>
    public sealed class DescribeCandidate
    {
        internal DescribeCandidate(int batchIndex, int? statementNumber, string text)
        {
            BatchIndex = batchIndex;
            StatementNumber = statementNumber;
            Text = text;
        }

        /// <summary>0-based GO batch index.</summary>
        public int BatchIndex { get; }
        /// <summary>1-based position among the batch's top-level statements; null for the
        /// whole-batch candidate (the pre-statement-split behaviour).</summary>
        public int? StatementNumber { get; }
        public bool IsWholeBatch => StatementNumber == null;
        public string Text { get; }
    }

    /// <summary>Result of <see cref="StatementCandidateBuilder.Build"/>.</summary>
    public sealed class CandidateSet
    {
        internal CandidateSet(IReadOnlyList<DescribeCandidate> candidates, bool capReached, int statementCap)
        {
            Candidates = candidates;
            CapReached = capReached;
            StatementCap = statementCap;
        }

        /// <summary>Only whole-batch candidates — the behaviour before statement splitting.</summary>
        public static CandidateSet WholeBatchesOnly(IReadOnlyList<string> batchTexts) =>
            new CandidateSet((batchTexts ?? throw new ArgumentNullException(nameof(batchTexts)))
                .Select((t, i) => new DescribeCandidate(i, null, t)).ToList(), false, 0);

        /// <summary>Whole-batch candidates for every batch (in batch order), each followed by
        /// that batch's statement candidates.</summary>
        public IReadOnlyList<DescribeCandidate> Candidates { get; }
        /// <summary>True when more statements qualified than the cap allowed.</summary>
        public bool CapReached { get; }
        /// <summary>The statement-candidate cap that was applied.</summary>
        public int StatementCap { get; }
        public int StatementCandidateCount => Candidates.Count(c => !c.IsWholeBatch);
    }

    /// <summary>
    /// sys.dm_exec_describe_first_result_set only describes the FIRST result set of a text, so
    /// grids 2..N of a multi-statement batch (e.g. three highlighted SELECTs executed together)
    /// could never shape-match. This builds, next to today's whole-batch candidate, one extra
    /// candidate per top-level result-returning SELECT. Pure: the statement list comes from a
    /// parser adapter. The rules are deliberately conservative — a candidate that is missing is
    /// only a decline, while a candidate described outside its real context could resolve a
    /// name to the wrong table:
    /// <list type="bullet">
    /// <item>only batches with at least 2 top-level statements (a single statement is the whole batch);</item>
    /// <item>candidate text = the batch's earlier top-level DECLAREs, then the statement itself;</item>
    /// <item>no candidate for anything before the LAST context change (USE / EXECUTE AS / REVERT /
    /// SETUSER) in the whole text — including every earlier batch — and none at all for a batch
    /// (or the batches before it) that has a nested context change or could not be parsed;</item>
    /// <item>at most <see cref="MaxStatementCandidates"/> statement candidates in total.</item>
    /// </list>
    /// </summary>
    public static class StatementCandidateBuilder
    {
        public const int MaxStatementCandidates = 50;

        public static CandidateSet Build(IReadOnlyList<ParsedBatch> batches, int maxStatementCandidates = MaxStatementCandidates)
        {
            if (batches == null) throw new ArgumentNullException(nameof(batches));

            // The last batch whose context we cannot see past: it (possibly) changes the
            // database/user context, so statements before its change point would be described
            // in the wrong context. Unparsed batches count — they might hold a USE.
            int contextBatch = -1;
            for (int b = batches.Count - 1; b >= 0; b--)
            {
                var batch = batches[b];
                if (batch.Statements == null || batch.ContainsNestedContextChange ||
                    batch.Statements.Any(s => s.Kind == StatementKind.ContextChange))
                {
                    contextBatch = b;
                    break;
                }
            }

            var candidates = new List<DescribeCandidate>();
            int statementCandidates = 0;
            bool capReached = false;

            for (int b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                candidates.Add(new DescribeCandidate(b, null, batch.Text));

                if (b < contextBatch) continue;
                if (batch.Statements == null || batch.ContainsNestedContextChange) continue;
                if (batch.Statements.Count < 2) continue;

                // In the context batch itself, only statements after its last top-level change.
                int firstEligible = 0;
                for (int s = batch.Statements.Count - 1; s >= 0; s--)
                {
                    if (batch.Statements[s].Kind == StatementKind.ContextChange) { firstEligible = s + 1; break; }
                }

                var declares = new List<string>();
                for (int s = 0; s < batch.Statements.Count; s++)
                {
                    var statement = batch.Statements[s];
                    if (!TryGetText(batch.Text, statement, out var statementText)) continue;

                    if (statement.Kind == StatementKind.Declare)
                    {
                        declares.Add(statementText);
                        continue;
                    }

                    if (statement.Kind != StatementKind.ResultSetSelect || s < firstEligible) continue;

                    if (statementCandidates >= maxStatementCandidates) { capReached = true; continue; }

                    candidates.Add(new DescribeCandidate(b, s + 1, ComposeText(declares, statementText)));
                    statementCandidates++;
                }
            }

            return new CandidateSet(candidates, capReached, maxStatementCandidates);
        }

        private static bool TryGetText(string batchText, ParsedStatement statement, out string text)
        {
            text = null;
            if (statement.StartOffset + statement.Length > batchText.Length || statement.Length == 0) return false;
            text = batchText.Substring(statement.StartOffset, statement.Length);
            return true;
        }

        /// <summary>Each DECLARE is terminated with ';' when it isn't already, so a following
        /// WITH (CTE) still parses ("previous statement must be terminated with a semicolon").</summary>
        private static string ComposeText(IReadOnlyList<string> declares, string statementText)
        {
            if (declares.Count == 0) return statementText;
            var parts = declares.Select(d => d.TrimEnd().EndsWith(";", StringComparison.Ordinal) ? d : d + ";");
            return string.Join("\r\n", parts) + "\r\n" + statementText;
        }
    }
}
