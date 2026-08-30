using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SqlGeneration
{
    /// <summary>
    /// Contract rule 6: "APPROX_COUNT_DISTINCT must not appear anywhere in the repository."
    /// This is deliberately a source-scan rather than a call into ProfileSqlBuilder /
    /// DistinctPlanner: it does not depend on those classes' method signatures (which are not
    /// frozen by CONTRACT.md, only the model/service shapes are), so it compiles and is
    /// meaningful even before or after Core's SQL-generation API changes shape. It is also
    /// strictly stronger than a generated-SQL check: it catches the term appearing in a comment,
    /// a string constant, a test, or dead code -- anywhere at all.
    /// </summary>
    public class ApproxCountDistinctBanTests
    {
        /// <summary>
        /// This file's own absolute path, resolved by the compiler via CallerFilePath -- not a
        /// hand-typed string that could drift from the real location.
        /// </summary>
        private static string ThisFilePath([CallerFilePath] string path = null) => path;

        [Fact]
        public void Repository_NeverContainsApproxCountDistinct()
        {
            string root = FindRepoRoot();
            string selfPath = Path.GetFullPath(ThisFilePath());
            var offenders = new List<string>();

            // Scan every .cs/.sql file in the whole repository, INCLUDING the rest of tests/ --
            // a helper, fixture, or another test file smuggling a real call to
            // APPROX_COUNT_DISTINCT must still be caught. The only file excluded is this one:
            // it legitimately contains the literal banned string as the text it searches for
            // and asserts against, and would otherwise trip its own ban. The exclusion is a
            // single-file path comparison, not a directory skip, so it cannot be widened by
            // accident to shadow a real violation placed anywhere else, including a sibling
            // file in this same SqlGeneration folder.
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories)))
            {
                // Skip build output -- generated/obj artifacts are not source we authored.
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;

                if (string.Equals(Path.GetFullPath(file), selfPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = File.ReadAllText(file);
                if (text.IndexOf("APPROX_COUNT_DISTINCT", StringComparison.OrdinalIgnoreCase) >= 0)
                    offenders.Add(file);
            }

            Assert.True(offenders.Count == 0,
                "APPROX_COUNT_DISTINCT (banned by CONTRACT.md rule 6) found in: "
                + string.Join(", ", offenders));
        }

        /// <summary>
        /// Locate the repository root by walking up from the test assembly's location until
        /// CONTRACT.md is found -- robust to whatever bin/obj nesting the build produces.
        /// </summary>
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CONTRACT.md")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate repository root (CONTRACT.md not found above " + AppContext.BaseDirectory + ").");
        }
    }
}
