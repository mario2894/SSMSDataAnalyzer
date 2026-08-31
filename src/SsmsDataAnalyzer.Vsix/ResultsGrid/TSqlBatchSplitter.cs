using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// v0.7.4 field report: "Go to source" was passing the WHOLE editor text to
    /// sys.dm_exec_describe_first_result_set, which describes the FIRST result set of that
    /// text. With the extremely common "USE db / GO / SELECT ..." pattern, the first
    /// statement's result set (none, for USE) is not what produced the grid the user clicked
    /// — describing whole-document text like that means gate 4 (column count) or gate 5
    /// (column name) almost always trips, declining a case Go to source should be able to
    /// serve. Fix: split the text into GO-separated batches and describe each one, picking
    /// whichever batch's described shape matches the grid exactly (see
    /// ResultsGridGoToSourceResolver.ResolveAsync) — this makes NO gate weaker, it only
    /// changes which text gets described.
    ///
    /// Splitting on GO correctly requires knowing what NOT to split on: the literal letters
    /// "GO" inside a string, a quoted identifier, or a comment must never be treated as a
    /// batch separator, or the split corrupts the query text being described. Per SSMS/sqlcmd
    /// convention, a real batch separator is a line that consists of ONLY "GO", optionally
    /// followed by a repeat count (e.g. "GO 5") and/or a trailing line comment — nothing else
    /// on that line. This is a genuine single-pass T-SQL lexer (tracks '...' strings with ''
    /// escapes, [...] bracketed identifiers with ]] escapes, "..." quoted identifiers with ""
    /// escapes, --line comments, and /* nestable */ block comments — SQL Server's block
    /// comments DO nest) rather than a regex over the raw text, specifically because a regex
    /// has no way to know "am I currently inside an unterminated string that started on an
    /// earlier line" — exactly the case that would otherwise misfire on something like:
    /// <code>
    /// SELECT '
    /// GO
    /// ' AS Note
    /// </code>
    /// where the middle line is literally "GO" but is inside an open string continued from
    /// the line before, and must NOT be treated as a separator.
    /// </summary>
    internal static class TSqlBatchSplitter
    {
        private enum LexState { Normal, SingleQuoteString, BracketIdent, DoubleQuoteIdent, LineComment, BlockComment }

        // Whole line (after trimming leading/trailing whitespace) is "GO", optionally with a
        // repeat count, optionally with a trailing line comment — matches sqlcmd/SSMS's own
        // batch-separator rule. Only ever tested against a line whose START state was Normal
        // (see below) — a line where GO appears after something else (e.g. a string closing
        // mid-line) is correctly never a separator, same as real SSMS parsing.
        private static readonly Regex GoLineRegex = new Regex(
            @"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*(--.*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits <paramref name="text"/> into batches on true GO separator lines.
        /// Always returns at least one element (the whole text, if there is no separator).
        /// Batches are NOT trimmed here — callers decide what to do with blank/whitespace-only
        /// batches (e.g. the text before a leading GO, or after a trailing one).</summary>
        public static List<string> Split(string text)
        {
            var batches = new List<string>();
            if (string.IsNullOrEmpty(text)) { batches.Add(text ?? string.Empty); return batches; }

            int n = text.Length;
            int i = 0;
            var state = LexState.Normal;
            int blockCommentDepth = 0;

            int lineStart = 0;
            var lineStartState = LexState.Normal; // state as of the FIRST character of the current line
            int batchStart = 0;

            while (i < n)
            {
                char c = text[i];
                char next = i + 1 < n ? text[i + 1] : '\0';

                if (c == '\r' || c == '\n')
                {
                    int termLen = (c == '\r' && next == '\n') ? 2 : 1;
                    string line = text.Substring(lineStart, i - lineStart);

                    // A line comment always ends at physical end-of-line by definition —
                    // never carries into the next line, regardless of how deeply we're
                    // "inside" it right now.
                    var stateAfterLine = state == LexState.LineComment ? LexState.Normal : state;

                    if (lineStartState == LexState.Normal && GoLineRegex.IsMatch(line))
                    {
                        batches.Add(text.Substring(batchStart, lineStart - batchStart));
                        batchStart = i + termLen;
                    }

                    i += termLen;
                    lineStart = i;
                    lineStartState = stateAfterLine;
                    state = stateAfterLine;
                    continue;
                }

                switch (state)
                {
                    case LexState.Normal:
                        if (c == '\'') { state = LexState.SingleQuoteString; i++; }
                        else if (c == '[') { state = LexState.BracketIdent; i++; }
                        else if (c == '"') { state = LexState.DoubleQuoteIdent; i++; }
                        else if (c == '-' && next == '-') { state = LexState.LineComment; i += 2; }
                        else if (c == '/' && next == '*') { state = LexState.BlockComment; blockCommentDepth = 1; i += 2; }
                        else i++;
                        break;

                    case LexState.SingleQuoteString:
                        if (c == '\'') { if (next == '\'') i += 2; else { state = LexState.Normal; i++; } }
                        else i++;
                        break;

                    case LexState.BracketIdent:
                        if (c == ']') { if (next == ']') i += 2; else { state = LexState.Normal; i++; } }
                        else i++;
                        break;

                    case LexState.DoubleQuoteIdent:
                        if (c == '"') { if (next == '"') i += 2; else { state = LexState.Normal; i++; } }
                        else i++;
                        break;

                    case LexState.LineComment:
                        // Consumed char-by-char; the newline handler above resets this to
                        // Normal at end of line unconditionally.
                        i++;
                        break;

                    case LexState.BlockComment:
                        if (c == '/' && next == '*') { blockCommentDepth++; i += 2; }
                        else if (c == '*' && next == '/')
                        {
                            blockCommentDepth--;
                            i += 2;
                            if (blockCommentDepth == 0) state = LexState.Normal;
                        }
                        else i++;
                        break;
                }
            }

            // Final (possibly unterminated-by-newline) line — same check, no trailing
            // terminator to skip past.
            string lastLine = text.Substring(lineStart, n - lineStart);
            if (lineStartState == LexState.Normal && GoLineRegex.IsMatch(lastLine))
            {
                batches.Add(text.Substring(batchStart, lineStart - batchStart));
                batchStart = n;
            }

            batches.Add(text.Substring(batchStart));
            return batches;
        }
    }
}
