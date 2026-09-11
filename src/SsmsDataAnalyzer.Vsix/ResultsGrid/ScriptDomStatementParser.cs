using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SsmsDataAnalyzer.Core.ResultShape;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// Splits one GO batch into top-level statements for
    /// <see cref="StatementCandidateBuilder"/>, using Microsoft's own T-SQL parser
    /// (Microsoft.SqlServer.TransactSql.ScriptDom 18.0.0.0) as SHIPPED BY SSMS 22 in
    /// Common7\IDE\Extensions\Application — a probing path in Ssms.exe.config, and SQLEditors.dll
    /// itself references the same identity. Never packaged into our .vsix.
    ///
    /// Same shell/core idea as <see cref="ResultsGridCapability"/>: <see cref="Parse"/> (the
    /// shell) references no ScriptDom type, so a missing/changed assembly surfaces as an
    /// exception INSIDE its try/catch when the JIT compiles <see cref="ParseCore"/>, not as a
    /// failure of the caller. Any failure — load error, parse errors, anything unexpected —
    /// yields <see cref="ParsedBatch.Unparsed"/>, i.e. exactly the pre-split behaviour
    /// (whole batch only). A load failure disables parsing for the rest of the session.
    ///
    /// Never logs query text: parse error messages and exception messages can quote it, so only
    /// fixed text / exception type names go to OeDiagnostics.
    /// </summary>
    internal static class ScriptDomStatementParser
    {
        private static volatile bool _unavailable;

        public static ParsedBatch Parse(string batchText)
        {
            if (batchText == null) throw new ArgumentNullException(nameof(batchText));
            if (_unavailable) return ParsedBatch.Unparsed(batchText);

            try
            {
                return ParseCore(batchText) ?? ParsedBatch.Unparsed(batchText);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException ||
                                       ex is TypeLoadException || ex is MissingMethodException || ex is MissingFieldException)
            {
                _unavailable = true;
                OeDiagnostics.Warn("Go to source: T-SQL statement splitting unavailable in this SSMS build (" + ex.GetType().Name + "); using whole batches only.");
                return ParsedBatch.Unparsed(batchText);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Go to source: T-SQL statement splitting failed (" + ex.GetType().Name + "); using the whole batch.");
                return ParsedBatch.Unparsed(batchText);
            }
        }

        /// <summary>Null when the text does not parse cleanly as exactly one batch.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ParsedBatch ParseCore(string batchText)
        {
            // Newest parser in the SSMS 22 copy; initialQuotedIdentifiers = true is SSMS's
            // default (SET QUOTED_IDENTIFIER ON), so "..." is an identifier, as in the lexer.
            var parser = new TSql180Parser(true);
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(batchText))
                fragment = parser.Parse(reader, out errors);

            if (errors != null && errors.Count > 0) return null;
            if (!(fragment is TSqlScript script) || script.Batches.Count > 1) return null;
            if (script.Batches.Count == 0) return new ParsedBatch(batchText, new ParsedStatement[0], false);

            var topLevel = script.Batches[0].Statements;
            var statements = new List<ParsedStatement>(topLevel.Count);
            int topLevelContextChanges = 0;
            foreach (var statement in topLevel)
            {
                var kind = Classify(statement);
                if (kind == StatementKind.ContextChange) topLevelContextChanges++;
                if (statement.StartOffset < 0 || statement.FragmentLength < 0 ||
                    statement.StartOffset + statement.FragmentLength > batchText.Length)
                    return null;
                statements.Add(new ParsedStatement(kind, statement.StartOffset, statement.FragmentLength));
            }

            var counter = new ContextChangeCounter();
            script.Accept(counter);

            return new ParsedBatch(batchText, statements, counter.Count > topLevelContextChanges);
        }

        private static StatementKind Classify(TSqlStatement statement)
        {
            switch (statement)
            {
                case SelectStatement select:
                    if (select.Into != null) return StatementKind.Other;
                    // SELECT @x = col … assigns variables and returns no result set.
                    if (select.QueryExpression is QuerySpecification spec &&
                        spec.SelectElements.Count > 0 &&
                        spec.SelectElements[0] is SelectSetVariable)
                        return StatementKind.Other;
                    return StatementKind.ResultSetSelect;

                case DeclareVariableStatement _:
                case DeclareTableVariableStatement _:
                    return StatementKind.Declare;

                case UseStatement _:
                case ExecuteAsStatement _:
                case RevertStatement _:
                case SetUserStatement _:
                    return StatementKind.ContextChange;

                default:
                    return StatementKind.Other;
            }
        }

        /// <summary>Counts every context-changing statement at any depth.</summary>
        private sealed class ContextChangeCounter : TSqlFragmentVisitor
        {
            public int Count;
            public override void Visit(UseStatement node) => Count++;
            public override void Visit(ExecuteAsStatement node) => Count++;
            public override void Visit(RevertStatement node) => Count++;
            public override void Visit(SetUserStatement node) => Count++;
        }
    }
}
