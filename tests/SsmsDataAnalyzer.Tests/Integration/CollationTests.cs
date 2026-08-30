using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Tests.TestSupport;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Integration
{
    /// <summary>
    /// CONTRACT.md Amendment 11: distinct counts are collation-dependent, and the profiler must
    /// report what <c>COUNT(DISTINCT …)</c> actually returns for a column under that column's
    /// own collation rather than normalising it.
    /// <para>
    /// <c>dbo.Orders.ColCaseDbDefault</c> / <c>ColCaseLatin1CI</c> / <c>ColCaseBin2</c> hold
    /// identical data (NULL / <c>'value'</c> / <c>'VALUE'</c> / 250 unique values) under three
    /// different collations. This is the executable statement of why the tool doesn't
    /// normalise: the two case-insensitive columns (one the database default -- this instance
    /// runs <c>Croatian_CI_AS</c>, one an explicit, different, still case-insensitive collation)
    /// agree at 251 distinct (<c>'value'</c>/<c>'VALUE'</c> collapse); the binary column
    /// (case-sensitive) reports 252. Every number here was verified live via sqlcmd before this
    /// test was written -- see tools/seed/expected.md, which also documents a correction: the
    /// amendment's original hypothesis (that the pre-existing <c>ColStringBlank</c>
    /// trailing-space case would diverge under <c>_BIN2</c>) turned out to be wrong --
    /// trailing-space collapsing is ANSI-padding behaviour, not collation-dependent, and holds
    /// even under a binary collation. Case sensitivity, not trailing spaces, is the real axis,
    /// which is why these columns use <c>'value'</c>/<c>'VALUE'</c> instead.
    /// </para>
    /// </summary>
    [Trait("Category", "Integration")]
    public class CollationTests
    {
        private static async Task<TableProfile> ProfileOrdersAsync()
        {
            var profiler = new TableProfiler();
            return await profiler.ProfileAsync(
                TestDb.ConnectionString, TestDb.Orders, new ProfileOptions(),
                progress: null, cancellationToken: CancellationToken.None);
        }

        private static ColumnProfile Col(TableProfile profile, string name)
        {
            var cp = profile.Columns.FirstOrDefault(c => c.Meta.Name == name);
            Assert.True(cp != null, "Column " + name + " not found in profile.");
            return cp;
        }

        [Fact]
        public async Task Orders_ProfilesWithoutCollationConflict()
        {
            // The whole point: three differently-collated columns (plus the pre-existing
            // ColStringBlank blank-check comparing a column against a literal) sit in the same
            // pass-1 chunk. A single Msg 451 anywhere in the generated SQL would fail this
            // entire profile rather than just one column -- so simply not throwing here is
            // itself a meaningful assertion.
            var profile = await ProfileOrdersAsync();
            Assert.NotNull(profile);
            Assert.Equal(1000, profile.TotalRows);
        }

        [Fact]
        public async Task CaseInsensitiveColumns_AgreeAtTwoFiftyOne_RegardlessOfWhichCollationIsDefault()
        {
            var profile = await ProfileOrdersAsync();

            var dbDefault = Col(profile, "ColCaseDbDefault");
            var latin1Ci = Col(profile, "ColCaseLatin1CI");

            Assert.Equal(750, dbDefault.FilledCount);
            Assert.Equal(251, dbDefault.DistinctCount);

            Assert.Equal(750, latin1Ci.FilledCount);
            Assert.Equal(251, latin1Ci.DistinctCount);

            Assert.Equal(dbDefault.DistinctCount, latin1Ci.DistinctCount);
        }

        [Fact]
        public async Task Bin2Column_DisagreesWithDbDefault_OnIdenticalData_PerAmendment11()
        {
            // The executable statement of Amendment 11's decision: same data, same table, a
            // different -- and equally correct -- distinct count purely because of collation.
            var profile = await ProfileOrdersAsync();

            var dbDefault = Col(profile, "ColCaseDbDefault");
            var bin2 = Col(profile, "ColCaseBin2");

            Assert.Equal(750, bin2.FilledCount);
            Assert.Equal(252, bin2.DistinctCount);

            Assert.NotEqual(dbDefault.DistinctCount, bin2.DistinctCount);
            Assert.Equal(dbDefault.FilledCount, bin2.FilledCount); // same NULLs either way -- only DISTINCT differs
        }
    }
}
