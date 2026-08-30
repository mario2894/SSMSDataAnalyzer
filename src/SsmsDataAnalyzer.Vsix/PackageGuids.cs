using System;

namespace SsmsDataAnalyzer.Vsix
{
    /// <summary>
    /// Single source of truth for every GUID this package declares. Referenced from the
    /// .vsct file (as the equivalent hex literals — keep in sync if either side changes)
    /// and from the C# attributes on <see cref="DataAnalyzerPackage"/>.
    /// </summary>
    internal static class PackageGuids
    {
        /// <summary>DataAnalyzerPackage's own GUID (ProvideAutoLoad / package registration).</summary>
        public const string PackageGuidString = "b7e4f6a2-8f2f-4a7a-9e1a-3c7f7b8a9d10";
        public static readonly Guid PackageGuid = new Guid(PackageGuidString);

        /// <summary>guidSsmsDataAnalyzerCommandSet in VSCommandTable.vsct.</summary>
        public const string CommandSetGuidString = "e2d9f1c4-5b6a-4c8e-9a1b-7d6e2f3a8c55";
        public static readonly Guid CommandSetGuid = new Guid(CommandSetGuidString);

        /// <summary>guidToolWindowPersistence — identifies ProfileToolWindow's persisted frame.</summary>
        public const string ToolWindowPersistenceGuidString = "f4a6c8d0-2e3b-4f5a-8c7d-1a9b3e5f7c22";
        public static readonly Guid ToolWindowPersistenceGuid = new Guid(ToolWindowPersistenceGuidString);

        /// <summary>v0.7.2: GridFindToolWindow's persistence GUID. Results-grid Find moved
        /// from a floating WPF Window to a real VS ToolWindowPane after the floating window
        /// proved unable to reliably own keyboard focus/routing inside the SSMS host — see
        /// GridFindToolWindow's doc comment.</summary>
        public const string GridFindToolWindowPersistenceGuidString = "a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
        public static readonly Guid GridFindToolWindowPersistenceGuid = new Guid(GridFindToolWindowPersistenceGuidString);
    }

    /// <summary>Numeric command/menu/group IDs used inside VSCommandTable.vsct.</summary>
    internal static class PackageIds
    {
        public const int AnalyzeDataMenuGroup = 0x1020;
        public const int AnalyzeDataToolbarGroup = 0x1021;
        public const int AnalyzeDataMenu = 0x1030;
        public const int AnalyzeDataCommandId = 0x0100;

        /// <summary>CONTRACT.md Amendment 16 — results-grid "Go to source for this value".
        /// Group is parented (in VSCommandTable.vsct only — never referenced from C#) to the
        /// external GUID_SQLEditorGroup:IDM_SQLWB_SQLRESGRID_CONTEXT = {33F13AC3-80BB-4ECB-85BC-225435603A5E}:0x0070.</summary>
        public const int ResultsGridMenuGroup = 0x1040;
        public const int GoToSourceForValueCommandId = 0x0200;

        /// <summary>User request: "Find... on right click in result grid of SSMS." Same
        /// results-grid menu group as GoToSourceForValueCommandId.</summary>
        public const int GridFindCommandId = 0x0201;
    }
}
