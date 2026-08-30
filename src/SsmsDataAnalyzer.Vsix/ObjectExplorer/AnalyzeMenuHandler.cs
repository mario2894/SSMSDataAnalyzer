using System;
using System.ComponentModel.Design;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ObjectExplorer
{
    /// <summary>
    /// The "Analyze Data" contribution to a table node's Object Explorer context menu.
    /// docs/oe-api.md section 4 / risk #6: this is injected into SSMS's own WinForms
    /// <c>ContextMenuStrip</c> construction, not a VS <c>ctmenu</c> — a throwing
    /// <see cref="GetMenuItems"/> takes down the ENTIRE node context menu, not just our
    /// item. <see cref="GetMenuItems"/> must therefore be a total function: wrap the whole
    /// body and return an empty array on any error, no exceptions.
    /// </summary>
    internal sealed class AnalyzeMenuHandler : IMenuHandler, IWinformsMenuHandler
    {
        private readonly Action<INodeInformation> _onInvoke;

        // CONTRACT.md Amendment 13, Bug 2 ("table is whole time no matter where i click"):
        // the interface's Parent { get; set; } is documented as "set by SSMS after AddChild,"
        // but AddChild only runs ONCE per host (docs/oe-api.md's de-dupe guard). If SSMS
        // reuses/pools a single per-type menu-handler host across sibling table nodes rather
        // than constructing one per node — plausible given the XML-driven "TableItemMenu"
        // template shape in ObjectExplorer.dll's embedded hierarchy — Parent would only ever
        // reflect whichever node was first right-clicked, never updating again. Rather than
        // depend on SSMS re-assigning Parent correctly on every render (unverifiable without
        // a live host), OeContextBridge.TryPatchNode calls SetCurrentNode on every single
        // CurrentContextChanged, whether or not this handler was already patched into its
        // host — so this instance always knows the most recently selected node regardless of
        // what SSMS does with Parent.
        private INodeInformation _currentNode;

        // Problem 2 (v0.5.2 field report, race between CurrentContextChanged and a right-
        // click): GetMenuItems() is SSMS's own synchronous "about to build the context menu"
        // call — unlike CurrentContextChanged, it cannot race the click, because it IS the
        // click. _getFreshSelection pulls the authoritative current node at that exact moment
        // (IObjectExplorerService.GetSelectedNodes, docs/oe-api.md section 3.1) as a fallback
        // for whenever the push-based _currentNode is stale or was never set for this node at
        // all; _patchNodeOnDemand re-runs OeContextBridge's own idempotent patch-or-retarget
        // logic for that fresh node, so a host discovered stale here gets corrected
        // immediately (and any host not yet patched gets patched, self-healing the NEXT
        // right-click even when this exact one can't be helped anymore).
        private readonly Func<INodeInformation> _getFreshSelection;
        private readonly Action<INodeInformation> _patchNodeOnDemand;

        public AnalyzeMenuHandler(Action<INodeInformation> onInvoke, Func<INodeInformation> getFreshSelection, Action<INodeInformation> patchNodeOnDemand)
        {
            _onInvoke = onInvoke ?? throw new ArgumentNullException(nameof(onInvoke));
            _getFreshSelection = getFreshSelection; // optional — degrades to _currentNode/Parent only
            _patchNodeOnDemand = patchNodeOnDemand; // optional — degrades to no self-heal
        }

        public void SetCurrentNode(INodeInformation node) => _currentNode = node;

        // Set by SSMS itself after HierarchyObject.AddChild, per docs/oe-api.md — kept as a
        // fallback for GetMenuItems (see _currentNode above) in case SetCurrentNode was never
        // called for some reason, but _currentNode is authoritative when present.
        public INodeInformation Parent { get; set; }

        public CommandID ContextMenuID => null;

        public event EventHandler OnRefresh { add { } remove { } }

        public void FillMenuCommands(IMenuCommandService menuCommandService)
        {
            // No legacy ctmenu commands to fill — this handler only participates via the
            // WinForms GetMenuItems path (docs/oe-api.md section 2: the ctmenu branch is
            // dead code for SQL nodes in SSMS 22).
        }

        public void UpdateMenuCommandsStatus(MenuCommand command)
        {
        }

        public void DoDefaultAction()
        {
        }

        public void InvokeProperties()
        {
        }

        public ToolStripItem[] GetMenuItems()
        {
            try
            {
                // Pull the authoritative current selection RIGHT NOW, at the one moment we
                // are guaranteed to be in sync with the actual right-click (see the field
                // comments above) — preferred over the possibly-stale _currentNode whenever
                // it's available and actually differs.
                var fresh = _getFreshSelection?.Invoke();
                if (fresh != null && !ReferenceEquals(fresh, _currentNode))
                {
                    _currentNode = fresh;
                    // Self-heal: ensure whichever host owns this node is patched (idempotent —
                    // a no-op retarget if it already is), so a host discovered stale/missing
                    // right here gets corrected without waiting for CurrentContextChanged.
                    _patchNodeOnDemand?.Invoke(fresh);
                }

                var node = _currentNode ?? Parent;
                if (node == null)
                {
                    OeDiagnostics.InfoOnce("oe.menu.nullParent", "AnalyzeMenuHandler.GetMenuItems called with Parent == null (at least once) — SSMS did not set it before invoking us.");
                    return Array.Empty<ToolStripItem>();
                }
                if (!OeTableInfo.IsTableNode(node))
                {
                    // Expected and silent by design once diagnosed: this handler is attached
                    // to every node type (docs/oe-api.md — patching happens before we know the
                    // node's kind), and correctly renders nothing for non-table nodes. Logged
                    // once per distinct UrnPath purely so "the menu never shows up" can be
                    // told apart from "it shows up on every node type except tables."
                    OeDiagnostics.InfoOnce("oe.menu.notTable." + (node.UrnPath ?? "?"),
                        $"AnalyzeMenuHandler skipped a non-table node (UrnPath='{node.UrnPath}') — expected, no menu item added for it.");
                    return Array.Empty<ToolStripItem>();
                }

                var item = new ToolStripMenuItem("Analyze Data...");
                item.Click += (s, e) => SafeInvoke(node);

                OeDiagnostics.InfoOnce("oe.menu.shown." + node.UrnPath,
                    $"'Analyze Data...' added to the context menu for a table node (UrnPath='{node.UrnPath}', Name='{node.Name}').");

                return new ToolStripItem[] { new ToolStripSeparator(), item };
            }
            catch (Exception ex)
            {
                // docs/oe-api.md risk #6, binding: never let an exception escape this method —
                // it would take down the entire Object Explorer node context menu, not just
                // this contribution. Still logged in full every time: this is the single
                // highest-value diagnostic for Bug 2, since a throw here means the item is
                // being suppressed on every single right-click of a table node.
                OeDiagnostics.Error("AnalyzeMenuHandler.GetMenuItems threw — 'Analyze Data' was suppressed for this node to protect the rest of the context menu", ex);
                return Array.Empty<ToolStripItem>();
            }
        }

        private void SafeInvoke(INodeInformation node)
        {
            try
            {
                _onInvoke(node);
            }
            catch (Exception ex)
            {
                // A click handler exception would surface as an unhandled exception dialog
                // inside SSMS's WinForms message loop — swallow it here too. The tool window
                // itself is responsible for showing a real error if the profiling run fails;
                // this is only the last line of defense for the menu-click plumbing. Logged
                // regardless, since a swallowed exception here means the user clicked
                // "Analyze Data" and nothing visibly happened.
                OeDiagnostics.Error("Analyze Data click handler threw before the tool window could open", ex);
            }
        }
    }
}
