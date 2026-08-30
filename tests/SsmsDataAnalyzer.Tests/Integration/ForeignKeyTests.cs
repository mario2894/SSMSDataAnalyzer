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
    /// CONTRACT.md Amendments 14 &amp; 15: FK metadata on <see cref="ColumnMeta"/>, and
    /// specifically the FOUR-state rule Amendment 15 revises Amendment 14 into (not a binary
    /// resolved/unresolved split):
    /// <list type="bullet">
    /// <item>Single-column FK: schema/table/column/name all populated, count = 1.</item>
    /// <item>Composite FK (one constraint, more than one column): schema/table/name populated,
    /// column NULL, count = 1.</item>
    /// <item>Multiple FKs on one column: everything NULL, count = n &gt; 1,
    /// <see cref="ColumnMeta.HasUnresolvedForeignKey"/> = true.</item>
    /// <item>Not a FK: everything NULL/false, count = 0.</item>
    /// </list>
    /// Every value asserted here was read directly from <c>sys.foreign_keys</c> /
    /// <c>sys.foreign_key_columns</c> first -- see tools/seed/expected.md -- before being
    /// written into this test, including the live proof that a disabled FK's data can violate
    /// the constraint (so "disabled" is genuinely unenforced, not cosmetic) and that
    /// <c>MultiFkCol</c> can only ever be NULL because its two targets have disjoint id spaces.
    /// </summary>
    [Trait("Category", "Integration")]
    public class ForeignKeyTests
    {
        private static async Task<TableProfile> ProfileAsync(TableRef table)
        {
            var profiler = new TableProfiler();
            return await profiler.ProfileAsync(
                TestDb.ConnectionString, table, new ProfileOptions(),
                progress: null, cancellationToken: CancellationToken.None);
        }

        private static ColumnMeta Meta(TableProfile profile, string name)
        {
            var cp = profile.Columns.FirstOrDefault(c => c.Meta.Name == name);
            Assert.True(cp != null, "Column " + name + " not found in profile.");
            return cp.Meta;
        }

        [Fact]
        public async Task SingleColumnFk_CrossSchema_FullyResolves()
        {
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "SingleFkCol");

            Assert.True(m.IsForeignKey);
            Assert.Equal(1, m.ForeignKeyCount);
            Assert.Equal("ref", m.ReferencedSchema);
            Assert.Equal("ParentSingle", m.ReferencedTable);
            Assert.Equal("Id", m.ReferencedColumn);
            Assert.Equal("FK_FkChild_Single", m.ForeignKeyName);
            Assert.False(m.HasUnresolvedForeignKey);
            Assert.Equal("[ref].[ParentSingle]", m.ReferencedQualifiedName);
        }

        [Fact]
        public async Task CompositeFk_KeepsTableTarget_ButColumnIsIntentionallyNull_NotAMissingFeature()
        {
            // This is the case Amendment 15 exists for, and the one most likely to regress back
            // to Amendment 14's "null everything" rule as an "obvious fix". It must NOT: the
            // catalog (sys.foreign_key_columns) genuinely knows CompFkA maps to KeyA and CompFkB
            // maps to KeyB (verified directly against the catalog in expected.md -- the pairing
            // is right there, constraint_column_id 1 and 2). ReferencedColumn is null here
            // because filtering the parent table on only HALF of a composite key returns
            // plausible-but-wrong rows with nothing to signal the error -- not because the
            // information is unavailable. Do not "fix" this by populating ReferencedColumn from
            // one half of the pair.
            var profile = await ProfileAsync(TestDb.FkChild);

            foreach (var colName in new[] { "CompFkA", "CompFkB" })
            {
                var m = Meta(profile, colName);

                Assert.True(m.IsForeignKey);
                Assert.Equal(1, m.ForeignKeyCount);
                Assert.Equal("dbo", m.ReferencedSchema);
                Assert.Equal("ParentComposite", m.ReferencedTable);
                Assert.Null(m.ReferencedColumn); // deliberate -- see comment above
                Assert.Equal("FK_FkChild_Composite", m.ForeignKeyName);

                // The table target is NOT ambiguous, even though the column target is: "go to
                // source table" must remain offerable here.
                Assert.False(m.HasUnresolvedForeignKey);
                Assert.Equal("[dbo].[ParentComposite]", m.ReferencedQualifiedName);
            }
        }

        [Fact]
        public async Task MultiFkColumn_NullsEverythingExceptCount_AndIsUnresolved()
        {
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "MultiFkCol");

            Assert.True(m.IsForeignKey);
            Assert.Equal(2, m.ForeignKeyCount);
            Assert.Null(m.ReferencedSchema);
            Assert.Null(m.ReferencedTable);
            Assert.Null(m.ReferencedColumn);
            Assert.Null(m.ForeignKeyName);

            // HasUnresolvedForeignKey is true ONLY for this case among all seeded FK columns.
            Assert.True(m.HasUnresolvedForeignKey);
            Assert.Null(m.ReferencedQualifiedName);
        }

        [Fact]
        public async Task HasUnresolvedForeignKey_IsTrueOnlyForTheMultiFkColumn_AcrossEverySeededCase()
        {
            // Assert the "only" in the lead's brief directly, across every case in one place,
            // rather than trusting four separate tests never to overlap in what they check.
            var profile = await ProfileAsync(TestDb.FkChild);

            var unresolved = profile.Columns
                .Where(c => c.Meta.HasUnresolvedForeignKey)
                .Select(c => c.Meta.Name)
                .ToList();

            Assert.Equal(new[] { "MultiFkCol" }, unresolved);
        }

        [Fact]
        public async Task DisabledAndUntrustedFk_ResolvesIdenticallyToAnEnabledOne()
        {
            // Seeded so the constraint is provably unenforced (DisabledFkCol = 999 in every
            // row, which does not exist in ParentDisabled {1, 2} -- the insert only succeeded
            // because the FK was disabled first). CONTRACT Amendment 14: disabled/untrusted
            // must still resolve, not be flagged differently from an enabled FK.
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "DisabledFkCol");

            Assert.True(m.IsForeignKey);
            Assert.Equal(1, m.ForeignKeyCount);
            Assert.Equal("dbo", m.ReferencedSchema);
            Assert.Equal("ParentDisabled", m.ReferencedTable);
            Assert.Equal("Id", m.ReferencedColumn);
            Assert.Equal("FK_FkChild_Disabled", m.ForeignKeyName);
            Assert.False(m.HasUnresolvedForeignKey);
            Assert.Equal("[dbo].[ParentDisabled]", m.ReferencedQualifiedName);
        }

        [Fact]
        public async Task DottedTableNameTarget_RoundTripsAsOneIdentifier_NotSplitOnPeriods()
        {
            // Live case from the user's real database (e.g.
            // "Intervention.ABB.Request.Change.History"), not a hypothetical. The whole point:
            // ReferencedTable must be the single raw string with the periods still in it, and
            // ReferencedQualifiedName must bracket it as ONE identifier.
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "DottedFkCol");

            Assert.True(m.IsForeignKey);
            Assert.Equal(1, m.ForeignKeyCount);
            Assert.Equal("dbo", m.ReferencedSchema);
            Assert.Equal("Intervention.ABB.Request.Change.History", m.ReferencedTable);
            Assert.Equal("Id", m.ReferencedColumn);
            Assert.Equal("FK_FkChild_Dotted", m.ForeignKeyName);
            Assert.False(m.HasUnresolvedForeignKey);

            Assert.Equal("[dbo].[Intervention.ABB.Request.Change.History]", m.ReferencedQualifiedName);
        }

        [Fact]
        public async Task NonFkColumn_IsNotFlaggedAsOne()
        {
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "PlainCol");

            Assert.False(m.IsForeignKey);
            Assert.Equal(0, m.ForeignKeyCount);
            Assert.Null(m.ReferencedSchema);
            Assert.Null(m.ReferencedTable);
            Assert.Null(m.ReferencedColumn);
            Assert.Null(m.ForeignKeyName);
            Assert.False(m.HasUnresolvedForeignKey);
            Assert.Null(m.ReferencedQualifiedName);
        }

        [Fact]
        public async Task PrimaryKeyColumn_WithNoFk_IsNotFlaggedAsOne()
        {
            var profile = await ProfileAsync(TestDb.FkChild);
            var m = Meta(profile, "Id");

            Assert.False(m.IsForeignKey);
            Assert.Equal(0, m.ForeignKeyCount);
            Assert.True(m.IsPrimaryKey);
        }

        [Fact]
        public async Task SelfReferencingFk_ResolvesToItsOwnTable()
        {
            var profile = await ProfileAsync(TestDb.SelfRefTable);
            var m = Meta(profile, "ParentId");

            Assert.True(m.IsForeignKey);
            Assert.Equal(1, m.ForeignKeyCount);
            Assert.Equal("dbo", m.ReferencedSchema);
            Assert.Equal("SelfRefTable", m.ReferencedTable);
            Assert.Equal("Id", m.ReferencedColumn);
            Assert.Equal("FK_SelfRefTable_Parent", m.ForeignKeyName);
            Assert.False(m.HasUnresolvedForeignKey);
            Assert.Equal("[dbo].[SelfRefTable]", m.ReferencedQualifiedName);
        }
    }
}
