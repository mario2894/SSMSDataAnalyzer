using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SqlGeneration
{
    public class SqlIdentifierTests
    {
        [Fact]
        public void Bracket_PlainIdentifier_WrapsInBrackets()
        {
            Assert.Equal("[Orders]", SqlIdentifier.Bracket("Orders"));
        }

        [Fact]
        public void Bracket_IdentifierContainingCloseBracket_DoublesIt()
        {
            // The core rule from CONTRACT.md #2: ']' -> ']]'.
            Assert.Equal("[Bracket]]Table]", SqlIdentifier.Bracket("Bracket]Table"));
        }

        [Fact]
        public void Bracket_IdentifierWithMultipleCloseBrackets_DoublesEachOne()
        {
            Assert.Equal("[a]]b]]c]", SqlIdentifier.Bracket("a]b]c"));
        }

        [Fact]
        public void Bracket_IdentifierContainingOpenBracket_LeavesOpenBracketAlone()
        {
            // Only ']' needs escaping inside a bracketed identifier; '[' is not special there.
            Assert.Equal("[a[b]", SqlIdentifier.Bracket("a[b"));
        }

        [Fact]
        public void TableRef_QualifiedName_BracketDoublesBothSchemaAndTableName()
        {
            var table = new TableRef { Schema = "dbo", Name = "Bracket]Table" };
            Assert.Equal("[dbo].[Bracket]]Table]", table.QualifiedName);
        }

        [Fact]
        public void TableRef_QualifiedName_DefaultsMissingSchemaToDbo()
        {
            var table = new TableRef { Schema = null, Name = "Orders" };
            Assert.Equal("[dbo].[Orders]", table.QualifiedName);
        }

        [Fact]
        public void Int_OutOfRange_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => SqlIdentifier.Int(101, 1, 100, "MaxGrantPercent"));
        }

        [Fact]
        public void Int_InRange_FormatsInvariantly()
        {
            Assert.Equal("25", SqlIdentifier.Int(25, 1, 100, "MaxGrantPercent"));
        }
    }
}
