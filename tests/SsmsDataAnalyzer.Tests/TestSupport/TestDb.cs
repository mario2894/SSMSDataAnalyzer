using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Tests.TestSupport
{
    /// <summary>
    /// Connection info for the live seeded database. Owned exclusively by Agent D
    /// (tools/seed/seed.sql creates and populates SsmsDataAnalyzerTest); every ground-truth
    /// number an integration test asserts against is documented and verified in
    /// tools/seed/expected.md.
    /// </summary>
    internal static class TestDb
    {
        public const string ConnectionString =
            "Server=.;Database=SsmsDataAnalyzerTest;Integrated Security=true;TrustServerCertificate=true";

        public static TableRef Orders => new TableRef { Schema = "dbo", Name = "Orders" };
        public static TableRef WideTable => new TableRef { Schema = "dbo", Name = "WideTable" };
        public static TableRef NoDateTable => new TableRef { Schema = "dbo", Name = "NoDateTable" };
        public static TableRef FallbackDateTable => new TableRef { Schema = "dbo", Name = "FallbackDateTable" };
        public static TableRef EmptyTable => new TableRef { Schema = "dbo", Name = "EmptyTable" };

        /// <summary>Table name literally contains ']' — proves bracket-doubling end to end.</summary>
        public static TableRef BracketTable => new TableRef { Schema = "dbo", Name = "Bracket]Table" };

        /// <summary>CONTRACT Amendments 14/15 — exercises every FK case in one table.</summary>
        public static TableRef FkChild => new TableRef { Schema = "dbo", Name = "FkChild" };

        /// <summary>Self-referencing FK case, kept separate from FkChild for clarity.</summary>
        public static TableRef SelfRefTable => new TableRef { Schema = "dbo", Name = "SelfRefTable" };
    }
}
