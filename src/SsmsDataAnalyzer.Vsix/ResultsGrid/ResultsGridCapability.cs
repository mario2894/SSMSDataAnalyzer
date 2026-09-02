using System;
using System.IO;
using System.Reflection;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// v0.7.6 field report: a user on SSMS 22.3.2+25.11520.95 hit a raw .NET modal dialog --
    /// "Could not load type 'Microsoft.SqlServer.Management.QueryExecution.IGridResultSet'
    /// from assembly 'SqlEditors, Version=22.200.0.0, ...'" -- clicking "Go to source" on a
    /// results grid. IGridResultSet was public in our SSMS 22.9 dev build's copy of
    /// SqlEditors.dll but absent from the user's 22.3 build -- same assembly IDENTITY, so the
    /// reference bound fine and the failure only surfaced at first real use.
    ///
    /// v0.8.0 ("build against the older API" decision): the results-grid code no longer
    /// touches IGridResultSet/SqlEditors.dll AT ALL -- it was rewritten onto
    /// Microsoft.SqlServer.GridControl.dll's IGridStorage/IGridControl, which the lead
    /// confirmed byte-for-byte identical between SSMS 21 and every SSMS 22 build (including
    /// 22.3). See docs/newer-grid-api.md for the previous IGridResultSet-based
    /// implementation, preserved for an easy upgrade if a future feature genuinely needs it.
    ///
    /// This class is KEPT rather than removed, on the lead's explicit instruction: being
    /// portable in THEORY (confirmed identical for the specific members this project uses)
    /// is not the same as having proven there is no OTHER surprise waiting on some 22.x build
    /// this project has never run on. The shell/core split and the friendly "needs a newer
    /// SSMS build" message stay as the safety net; RunProbe below now checks the PORTABLE
    /// types this project actually depends on, not the retired IGridResultSet.
    ///
    /// THIS CLASS is deliberately the ONLY place in the assembly that resolves these types by
    /// STRING NAME rather than a compile-time reference (typeof(...), a field of that type, a
    /// method parameter of that type -- ALL of those embed a TypeRef token that the JIT must
    /// resolve the moment the CONTAINING METHOD is first entered, regardless of whether the
    /// code path that actually uses the type executes). A string-keyed Assembly.GetType(name,
    /// throwOnError: false) call never forces that resolution -- it just answers "does this
    /// exist," which is exactly the question this class exists to answer, once, safely, no
    /// matter how broken the results-grid assemblies are on a given SSMS build.
    ///
    /// Every OTHER file that touches GridControl/IGridStorage/SqlScriptEditorControl (real,
    /// strongly-typed code -- that's still the right way to write the actual feature) must be
    /// reached ONLY through a "shell" method that checks IsSupported FIRST and contains NO
    /// reference of its own to any risky type -- see ResultsGridFindCommand and
    /// ResultsGridSourceCommand's OnBeforeQueryStatus/Execute pairs for the pattern. A shell
    /// method that itself declares so much as a local variable of type ClickedGridCell (which
    /// has GridControl/SqlScriptEditorControl fields) is NOT safe, even behind an "if" that
    /// never runs that branch -- the JIT compiles the WHOLE method body, all branches, the
    /// moment the method is first CALLED, not lazily per executed instruction. That is the
    /// exact trap that turned one missing interface into a modal dialog that took down two
    /// entire features instead of naming itself and disabling a menu item.
    /// </summary>
    internal static class ResultsGridCapability
    {
        // Types actually referenced (directly, by real code) elsewhere in ResultsGrid/*.cs.
        // If ANY of these fail to resolve, the whole results-grid feature set (Find AND Go to
        // source) is treated as unsupported -- a partial failure here would still leave some
        // code path able to JIT-crash into a modal dialog, which is exactly what this exists
        // to prevent.
        private static readonly (string TypeName, string AssemblySimpleName)[] RequiredTypes =
        {
            ("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl", "SqlEditors"),
            ("Microsoft.SqlServer.Management.UI.Grid.GridControl", "Microsoft.SqlServer.GridControl"),
            ("Microsoft.SqlServer.Management.UI.Grid.IGridStorage", "Microsoft.SqlServer.GridControl"),
            ("Microsoft.SqlServer.Management.UI.Grid.IGridControl", "Microsoft.SqlServer.GridControl"),
        };

        private static readonly Lazy<CapabilityResult> Probe = new Lazy<CapabilityResult>(RunProbe);

        private sealed class CapabilityResult
        {
            public bool Supported;
            public string Reason;
        }

        /// <summary>True once every type this feature area genuinely needs has been confirmed
        /// resolvable in THIS SSMS session. Computed once (Lazy, thread-safe by default) and
        /// cached for the process lifetime -- SSMS's own assemblies don't change underneath a
        /// running session, so re-probing on every menu click would only add cost for no new
        /// information.</summary>
        public static bool IsSupported => Probe.Value.Supported;

        /// <summary>Human-readable detail for the ActivityLog when unsupported -- which
        /// specific type/assembly failed and how. Null when supported.</summary>
        public static string UnsupportedReason => Probe.Value.Reason;

        /// <summary>The sentence for the STATUS BAR (lead's explicit ergonomics rule -- never
        /// make the user relaunch with /log to learn this). Callable even when
        /// IsSupported is true, though callers should only need it when it's not.</summary>
        public static string UserFacingMessage(string featureName) =>
            featureName + " isn't available in this SSMS session (a results-grid API this feature depends on isn't behaving as expected here). Analyze Data is unaffected.";

        private static CapabilityResult RunProbe()
        {
            foreach (var entry in RequiredTypes)
            {
                string typeName = entry.TypeName;
                string assemblySimpleName = entry.AssemblySimpleName;
                try
                {
                    // Assembly.Load by SIMPLE name (not typeof/AssemblyQualifiedName) --
                    // resolves whatever build of that DLL this SSMS session actually loaded
                    // (it will already be loaded, since real code elsewhere in this assembly
                    // references it too), without embedding any version/token assumption of
                    // our own that could itself go stale across SSMS updates.
                    var asm = Assembly.Load(assemblySimpleName);
                    var type = asm.GetType(typeName, throwOnError: false);
                    if (type == null)
                    {
                        return new CapabilityResult
                        {
                            Supported = false,
                            Reason = "'" + typeName + "' was not found in '" + assemblySimpleName + "' (assembly loaded: " + asm.FullName + ")."
                        };
                    }
                }
                catch (Exception ex)
                {
                    // Belt-and-suspenders: Assembly.Load itself, or GetType with a malformed
                    // name, could in principle throw rather than return null. Never let the
                    // PROBE become the crash it exists to prevent.
                    return new CapabilityResult
                    {
                        Supported = false,
                        Reason = "probing '" + typeName + "' in '" + assemblySimpleName + "' threw " + ex.GetType().Name + ": " + ex.Message
                    };
                }
            }
            return new CapabilityResult { Supported = true, Reason = null };
        }

        /// <summary>
        /// For the try/catch belt-and-suspenders around every "Core" (risky) method call --
        /// the probe above is the FIRST line of defense, but it only checks the specific types
        /// this project's code happens to reference today; a different missing member
        /// (MissingMethodException/MissingMemberException) or a related assembly entirely
        /// missing (FileNotFoundException) on some other 22.x build is the same class of
        /// problem and deserves the same friendly message, not a raw exception dialog. Returns
        /// null for any exception that ISN'T one of these -- callers should let those surface
        /// normally (they are real bugs, not a build-compatibility gap).
        /// </summary>
        public static string DescribeIfCompatibilityException(Exception ex, string featureName)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TypeLoadException || e is MissingMethodException || e is MissingMemberException || e is FileNotFoundException || e is BadImageFormatException)
                {
                    OeDiagnostics.Warn(featureName + ": build-compatibility failure (" + e.GetType().Name + "): " + e.Message);
                    return UserFacingMessage(featureName);
                }
            }
            return null;
        }
    }
}
