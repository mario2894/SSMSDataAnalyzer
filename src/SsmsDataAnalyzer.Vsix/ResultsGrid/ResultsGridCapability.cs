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
    /// results grid. Our dev machine is SSMS 22.9.12105.275, where IGridResultSet IS public in
    /// SqlEditors.dll. Same assembly IDENTITY (22.200.0.0) across both builds -- so the
    /// reference binds fine -- but DIFFERENT CONTENTS: the type simply doesn't exist in the
    /// older build's copy of that DLL. The manifest's InstallationTarget floor ([22.0,)) can't
    /// express "this specific type must exist," and raising it to [22.9,) would block install
    /// entirely for users on any 22.3-22.8 build, when Analyze Data (the core feature) works
    /// completely fine for them -- it never touches any of this. So: graceful degradation, not
    /// exclusion.
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
    /// Every OTHER file that touches GridControl/IGridResultSet/SqlScriptEditorControl (real,
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
            ("Microsoft.SqlServer.Management.QueryExecution.IGridResultSet", "SqlEditors"),
            ("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl", "SqlEditors"),
            ("Microsoft.SqlServer.Management.UI.Grid.GridControl", "Microsoft.SqlServer.GridControl"),
            ("Microsoft.SqlServer.Management.UI.Grid.IGridStorage", "Microsoft.SqlServer.GridControl"),
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
            featureName + " needs a newer SSMS 22 build (the SQLEditors grid API this feature depends on isn't present here). Analyze Data is unaffected.";

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
