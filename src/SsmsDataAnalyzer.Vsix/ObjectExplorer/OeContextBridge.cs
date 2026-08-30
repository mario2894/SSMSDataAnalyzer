using System;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.Management;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ObjectExplorer
{
    /// <summary>
    /// Tier A: injects "Analyze Data" into the Object Explorer context menu of every table
    /// node, per docs/oe-api.md. This is unsupported, undocumented API (public types, but no
    /// Microsoft support contract) — every entry point here is wrapped so a shape change in
    /// a future SSMS build degrades to Tier B (the Tools-menu tool window) instead of taking
    /// SSMS down. <see cref="DataAnalyzerPackage"/> constructs this inside its own
    /// try/catch and treats any failure as "Tier A unavailable this session."
    /// </summary>
    internal sealed class OeContextBridge : IDisposable
    {
        private readonly INavigationContextProvider _objectExplorerContext;
        private readonly IObjectExplorerService _objectExplorerService;
        private readonly Action<INodeInformation> _onInvoke;

        // docs/oe-api.md section 4: CurrentContextChanged fires on every OE selection change,
        // and HierarchyObject.AddChild appends unconditionally — without a de-dupe guard,
        // every click on the same node adds another menu item. Reference-identity dedupe via
        // ConditionalWeakTable (not a plain HashSet) so it does not root menu handlers for OE
        // nodes the tree has since discarded. Maps host -> the AnalyzeMenuHandler WE added to
        // it (not just a marker), so a later CurrentContextChanged for the SAME host (Bug 2 —
        // see AnalyzeMenuHandler's comment) can retarget it via SetCurrentNode instead of only
        // guarding against duplicate AddChild calls.
        private readonly ConditionalWeakTable<object, AnalyzeMenuHandler> _patchedHandlers = new ConditionalWeakTable<object, AnalyzeMenuHandler>();

        private bool _disposed;

        /// <summary>
        /// Throws if any part of the Tier A wiring is unavailable (missing service, shape
        /// change, etc.) — by design, so the caller's try/catch is the single place that
        /// decides "fall back to Tier B," per docs/oe-api.md's binding guidance.
        /// </summary>
        public OeContextBridge(IServiceProvider serviceProvider, Action<INodeInformation> onInvoke)
        {
            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
            _onInvoke = onInvoke ?? throw new ArgumentNullException(nameof(onInvoke));

            var contextService = (IContextService)serviceProvider.GetService(typeof(IContextService));
            if (contextService == null) throw new InvalidOperationException("IContextService is not available.");

            _objectExplorerContext = contextService.ObjectExplorerContext;
            if (_objectExplorerContext == null) throw new InvalidOperationException("IContextService.ObjectExplorerContext is not available.");

            // Problem 2 (v0.5.2 field report — "sometimes not show Analyze Data... on next
            // click he showed up"): CurrentContextChanged is push-based and can race a
            // right-click — WinForms TreeViews famously do not always change selection (and
            // therefore do not always raise a selection-changed-style event) on a RIGHT click
            // the way they do on a left click, and even when they do, event delivery is not
            // guaranteed to complete before SSMS asks for the context menu. IObjectExplorerService
            // is the PULL-based counterpart (docs/oe-api.md section 3.1's GetSelectedNodes) —
            // optional (this whole bridge already degrades to Tier B if anything here is
            // unavailable), used as a synchronous fallback at menu-build time (see
            // AnalyzeMenuHandler.GetMenuItems) rather than only reacting to the event.
            _objectExplorerService = serviceProvider.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;

            _objectExplorerContext.CurrentContextChanged += OnCurrentContextChanged;
        }

        /// <summary>
        /// Pull-based fallback for <see cref="AnalyzeMenuHandler.GetMenuItems"/>: the node
        /// SSMS currently has selected, read synchronously via
        /// <see cref="IObjectExplorerService.GetSelectedNodes"/> rather than waited-for via
        /// <see cref="INavigationContextProvider.CurrentContextChanged"/>. Null if the service
        /// is unavailable, nothing is selected, or more than one node is selected (multi-select
        /// context menus are out of scope here — never guess which of several selected nodes
        /// the click was "for").
        /// </summary>
        public INodeInformation TryGetCurrentlySelectedNode()
        {
            if (_objectExplorerService == null) return null;
            try
            {
                // docs/oe-api.md documents this as ref/ref; the actual compiled interface
                // (verified by the compiler, not assumed) takes them as out/out instead.
                _objectExplorerService.GetSelectedNodes(out int count, out INodeInformation[] nodes);
                if (count != 1 || nodes == null || nodes.Length != 1) return null;
                return nodes[0];
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn($"IObjectExplorerService.GetSelectedNodes failed ({ex.GetType().Name}: {ex.Message}) — falling back to CurrentContextChanged-pushed state only.");
                return null;
            }
        }

        /// <summary>
        /// Same patch-or-retarget logic as <see cref="TryPatchNode"/>, exposed for
        /// <see cref="AnalyzeMenuHandler.GetMenuItems"/> to call on demand at menu-build time —
        /// idempotent via the same <see cref="_patchedHandlers"/> dedup, so calling it again
        /// for an already-patched host is a no-op retarget, never a duplicate AddChild.
        /// </summary>
        public void PatchNodeOnDemand(INodeInformation node) => TryPatchNode(node);

        private void OnCurrentContextChanged(object sender, NodesChangedEventArgs e)
        {
            // docs/oe-api.md risk #6's spirit applies here too: this fires from inside SSMS's
            // own Object Explorer selection-handling code. An exception here must not corrupt
            // that, even though the binding "never throw" rule is specifically about
            // GetMenuItems — being defensive in both places is cheap and consistent.
            try
            {
                OeDiagnostics.InfoOnce("oe.contextChanged.fired",
                    "CurrentContextChanged fired for the first time this session — Object Explorer selection events are reaching the bridge.");

                var changed = e?.ChangedNodes;
                if (changed == null || changed.Count == 0)
                {
                    OeDiagnostics.InfoOnce("oe.contextChanged.empty",
                        "CurrentContextChanged fired with no ChangedNodes (at least once) — this is normal for some selection-change kinds; only a concern if 'Analyze Data' never appears at all.");
                    return;
                }

                for (int i = 0; i < changed.Count; i++)
                {
                    TryPatchNode(changed[i] as INodeInformation);
                }
            }
            catch (Exception ex)
            {
                // Swallow: a missing menu item on this selection change is recoverable (the
                // user can still reach Tier B); corrupting OE's own event pipeline is not.
                // Still logged — a silently-eaten exception here is exactly the kind of
                // failure Bug 2 asked us to stop hiding.
                OeDiagnostics.Error("CurrentContextChanged handler threw", ex);
            }
        }

        private void TryPatchNode(INodeInformation node)
        {
            if (node == null)
            {
                OeDiagnostics.InfoOnce("oe.patch.nullNode", "A changed-context entry did not cast to INodeInformation (at least once).");
                return;
            }

            // The node is its own IServiceProvider; ask it for the menu handler SSMS itself
            // is about to use to build the real context menu (docs/oe-api.md section 4).
            var handler = ((IServiceProvider)node).GetService(typeof(IMenuHandler)) as IMenuHandler;
            if (handler == null)
            {
                OeDiagnostics.InfoOnce("oe.patch.noHandler." + (node.UrnPath ?? "?"),
                    $"node.GetService(IMenuHandler) returned null for UrnPath='{node.UrnPath}' (at least once). If this happens for 'Server/Database/Table' specifically, that is why the menu item never appears.");
                return;
            }

            var host = handler as HierarchyObject;
            if (host == null)
            {
                OeDiagnostics.Warn($"IMenuHandler for UrnPath='{node.UrnPath}' is a {handler.GetType().FullName}, which does not derive from HierarchyObject — AddChild is unavailable, so 'Analyze Data' cannot be injected for this node shape. This likely means docs/oe-api.md's DefaultMenuHandler assumption no longer holds (an SSMS update?).");
                return;
            }

            // CONTRACT.md Amendment 13, Bug 2: retarget on EVERY context change for this host,
            // whether or not it was already patched. If SSMS reuses one host across sibling
            // table nodes (see AnalyzeMenuHandler's comment), this is what makes right-clicking
            // a DIFFERENT table actually switch the menu item's target instead of it staying
            // bound to whichever node was first right-clicked.
            if (_patchedHandlers.TryGetValue(host, out var existingHandler))
            {
                existingHandler.SetCurrentNode(node);
                return;
            }

            var newHandler = new AnalyzeMenuHandler(_onInvoke, TryGetCurrentlySelectedNode, PatchNodeOnDemand);
            newHandler.SetCurrentNode(node);
            _patchedHandlers.Add(host, newHandler);

            host.AddChild("SsmsDataAnalyzer.Analyze", newHandler);
            OeDiagnostics.Info($"Patched a menu handler for UrnPath='{node.UrnPath}' (Name='{node.Name}') with 'Analyze Data'. It will only render for table nodes (AnalyzeMenuHandler filters by UrnPath).");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _objectExplorerContext.CurrentContextChanged -= OnCurrentContextChanged;
        }
    }
}
