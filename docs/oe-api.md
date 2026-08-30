# Object Explorer integration on SSMS 22 — spike report (Agent C)

Target verified: **SSMS 22.9.12105.275**,
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`.
All findings below come from reading the shipped binaries with `spikes/OeProbe`
(pure `System.Reflection.Metadata` inspection — nothing was loaded or executed) plus the
two working third-party extensions on this machine.

---

## 1. VERDICT

> ## **Tier A is FEASIBLE.**
> A VSIX can add a real right-click item on a table node in SSMS 22, and can read the
> node's URN (server / database / schema / table) **and** its live connection.
> The whole path uses **public types on public, unversioned-but-shipped SSMS assemblies** —
> **no private reflection is required.**
>
> Risk rating: **feasible, undocumented, medium-fragility.** It is unsupported API, so it
> must still be built behind the feature flag and behind a `try/catch` that degrades to
> Tier B — but the "it might not exist at all in the VS 18 shell" risk is now **eliminated**.

Three things settle it:

1. The entire `Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer` namespace
   is **alive and public** in SSMS 22 — 88 public types in `SqlWorkbench.Interfaces.dll`,
   316 in `ObjectExplorer.dll`.
2. `IObjectExplorerService` is registered as a **VS global service** in SSMS 22's own pkgdef.
3. **Red Gate SQL Prompt 11 does exactly this, today, in this SSMS 22 install** — it ships a
   dedicated `RedGate.SqlPrompt.ShellAbstraction.**22**.dll` whose `MenuManagerManager`
   implements `IMenuHandler` + `IWinformsMenuHandler` and injects itself into the table
   node's menu. That is empirical proof the mechanism works in the VS 18 shell.

### One correction to PLAN.md §4

> *"Tier A … access it by **late-bound reflection**"*

Late binding is **not** needed for the API surface — every type and member we need is
`public`. Recommend: **compile-time reference** to the SSMS assemblies with
`<Private>false</Private>` (as PLAN.md §6 already prescribes), and guard the *whole*
Tier A activation in one `try/catch (Exception)` + feature flag. A missing/renamed type then
surfaces as a `TypeLoadException`/`MissingMethodException` at the guarded entry point and we
fall back to Tier B, which is the same failure behaviour reflection would give with far less
code. Late binding buys nothing here and costs readability.

---

## 2. What actually renders the Object Explorer context menu (the key discovery)

**The OE node context menu in SSMS 22 is a WinForms `ContextMenuStrip`, not a VS `ctmenu`.**

From `ObjectExplorer.dll`, `ExplorerHierarchyNode::ShowContextMenu` (IL):

```
ldstr    "sql/ssms/objectexplorer/contextmenu"        // telemetry scope
ldfld    ExplorerHierarchyNode::containedItem
ldtoken  ...ObjectExplorer.IMenuHandler
callvirt System.IServiceProvider::GetService           // node.GetService(typeof(IMenuHandler))
isinst   ...ObjectExplorer.IMenuHandler
...
isinst   ...ObjectExplorer.IWinformsMenuHandler        // <-- preferred path
...
newobj   System.Windows.Forms.ContextMenuStrip::.ctor
callvirt System.Windows.Forms.Control::set_ContextMenuStrip
callvirt System.Windows.Forms.ToolStrip::get_Items
callvirt IWinformsMenuHandler::GetMenuItems            // ToolStripItem[]
callvirt ToolStripItemCollection::AddRange
callvirt ToolStripDropDown::Show
```

Consequences, and they are decisive:

* **A `.vsct` group parented to the OE context menu will NOT appear.** The legacy
  `CommandID` still exists — `DefaultMenuHandler.ContextMenuID` =
  `{B574F3B6-89B8-4B45-9255-E393C072441B}` : `4` (`ObjectExplorerGuids.guidGroup`,
  `MenuId.IDM_TREE_CONTEXT_MENU`, both read from `.cctor` IL) — but for every SQL node the
  handler is a `DefaultMenuHandler`, which implements `IWinformsMenuHandler`, so the
  `IVsUIShell`/ctmenu branch is dead code. **Do not spend time on a `.vsct` for Tier A.**
  (`.vsct` is still correct and necessary for the Tier B top-level menu item and the
  Tier C editor command.)
* The way in is to become one of the objects `DefaultMenuHandler.GetMenuItems()` walks.

`DefaultMenuHandler.GetMenuItems()` iterates its private `menuItemsInOrder` / `subMenus`
`ArrayList`s and, for each element, does `isinst IWinformsMenuHandler` → calls that
element's `GetMenuItems()` and merges the result. So: **put an `IWinformsMenuHandler` into
that list and its `ToolStripItem`s appear in the real context menu.**

### The public door into that list

`DefaultMenuHandler` is `internal`, but it derives from the **public abstract**
`HierarchyObject`, whose `AddChild(string, object)` is **abstract** (RVA 0 in
`SqlWorkbench.Interfaces.dll`) and therefore virtually dispatched to
`DefaultMenuHandler.AddChild`, which is:

```
// ObjectExplorer.dll — DefaultMenuHandler::AddChild(string, object)
ldarg.2 ; isinst IMenuItem      ; brfalse -> +14
  call DefaultMenuHandler::AddItem(IMenuItem)          // IMenuItem route
ldarg.2 ; isinst IMenuHandler   ; brfalse -> ret
  ldfld DefaultMenuHandler::subMenus         ; ArrayList::Add
  ldfld DefaultMenuHandler::menuItemsInOrder ; ArrayList::Add   // IMenuHandler route
ret
```

So `((HierarchyObject)menuHandler).AddChild("SsmsDataAnalyzer", ourHandler)` is a
**public API call** that appends our item. Red Gate reflects on the private `subMenus` /
`menuItemsInOrder` fields only because they want to *position* their entry precisely; a
plain append needs no reflection at all.

---

## 3. Exact API surface (all verified present in SSMS 22)

Assembly identities as installed:

| Assembly | Identity |
|---|---|
| `SqlWorkbench.Interfaces.dll` | `SqlWorkbench.Interfaces, Version=22.200.0.0, PublicKeyToken=89845dcd8080cc91` |
| `ObjectExplorer.dll` | `ObjectExplorer, Version=22.200.0.0, PublicKeyToken=89845dcd8080cc91` |
| `SqlPackageBase.dll` | `SqlPackageBase, Version=22.200.0.0, PublicKeyToken=89845dcd8080cc91` |
| `Microsoft.SqlServer.Management.Sdk.SqlStudio.dll` | `…SDK.SqlStudio, Version=22.200.0.0, PublicKeyToken=89845dcd8080cc91` |
| `Microsoft.SqlServer.ConnectionInfo.dll` | `…ConnectionInfo, Version=18.100.0.0, PublicKeyToken=89845dcd8080cc91` |
| `Microsoft.SqlServer.Management.Sdk.Sfc.dll` | `…Sdk.Sfc, Version=18.100.0.0, PublicKeyToken=89845dcd8080cc91` |
| `Microsoft.SqlServer.RegSvrEnum.dll` | `…RegSvrEnum, Version=18.100.0.0, PublicKeyToken=89845dcd8080cc91` |

> Note the split: shell/OE assemblies are `22.200.0.0`, the SMO-family assemblies are still
> `18.100.0.0`. Reference all of them straight from the IDE folder, `<Private>false</Private>`.

### 3.1 Namespace `Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer`

All in **`SqlWorkbench.Interfaces.dll`**, all **`public`**:

```csharp
public interface IObjectExplorerService : System.IServiceProvider
{
    void   NewConnection();
    void   ConnectToServer(object);
    void   TryConnectToServer(object);
    void   TryConnectToServerWithDialogFallback(object);
    void   DisconnectServer(object);
    void   DisconnectSelectedServer();
    void   GetSelectedNodes(ref int nodeCount, ref INodeInformation[] nodes);   // <-- selection
    INodeInformation FindNode(string urn);
    void   SynchronizeTree(INodeInformation);
    event  System.EventHandler ConnectionDialogLoaded;
}

public interface INodeContext
{
    Microsoft.SqlServer.Management.Common.SqlOlapConnectionInfoBase Connection { get; set; }
    string Context           { get; set; }   // the URN, e.g. Server[@Name='.']/Database[@Name='X']/Table[@Name='Orders' and @Schema='dbo']
    string UrnPath           { get; }        // the SHAPE, e.g. "Server/Database/Table"
    string NavigationContext { get; }
    object CreateObjectInstance();           // -> SMO object for the node
}

public interface INodeInformation : INodeContext, System.IServiceProvider
{
    INodeInformation  Parent        { get; }
    string            Name          { get; }
    string            InvariantName { get; }
    IExplorerHierarchy Hierarchy    { get; }
    object            Item          { get; }
    T    GetSetting<T>(string);
    void SetSetting(string, object, string, string);
}

public interface IMenuHandler
{
    INodeInformation Parent      { get; set; }
    System.ComponentModel.Design.CommandID ContextMenuID { get; }
    void FillMenuCommands(System.ComponentModel.Design.IMenuCommandService);
    void UpdateMenuCommandsStatus(System.ComponentModel.Design.MenuCommand);
    void DoDefaultAction();
    void InvokeProperties();
    event System.EventHandler OnRefresh;
}

public interface IWinformsMenuHandler
{
    System.Windows.Forms.ToolStripItem[] GetMenuItems();     // <-- the single member that matters
}

public abstract class HierarchyObject : System.ICloneable
{
    public abstract void AddChild(string name, object child);   // abstract -> virtual dispatch
    public abstract void AddProperty(string name, object value);
    public abstract object Clone();
}

public interface IMenuItem { INodeInformation Parent { get; set; } bool MultiSelect { get; }
                             string Name { get; } string Text { get; } System.Guid CommandGuid { get; }
                             int ItemId { get; } System.EventHandler MenuHandler { get; }
                             void UpdateMenuCommandStatus(System.ComponentModel.Design.MenuCommand); }
public interface IMultiSelectMenuHandler : IMenuHandler { }
public interface IManagedConnection : System.IDisposable
{ Microsoft.SqlServer.Management.Common.SqlOlapConnectionInfoBase Connection { get; } void Close(); }
public abstract class ToolsMenuItemBase : HierarchyObject, IMenuItem, IToolTipHandler { … }
public abstract class HierarchyTreeNode : LazyNode, IExplorerHierarchyNode, INodeWithIcon { … }
public interface IExplorerHierarchy { HierarchyTreeNode Root { get; } … }
```

`ObjectExplorer.dll` (implementations) — relevant visibility:

| Type | Visibility | Note |
|---|---|---|
| `DefaultMenuHandler` | **internal** | but derives from public `HierarchyObject`; `AddChild`, `AddItem`, `GetMenuItems`, `Parent`, `Parents`, `Owner`, `ContextMenuID` are all `public` members |
| `ExtensionMenuHandler : DefaultMenuHandler` | internal | used by the `.oexml` mechanism (§5) |
| `OeMenuItemBase : ToolsMenuItemBase` | **public** abstract | |
| `ActionMenuItem`, `MultiSelectActionMenuItem`, `ReportMenuItem` | **public** | |
| `ObjectExplorerControl` (the TreeView) | internal | only needed if you want tree-level hooks |
| `ObjectExplorerGuids`, `MenuId` | internal | values reproduced above; hard-code them, don't reference |

### 3.2 Getting the service

`Extensions\Application\Microsoft.SqlServer.Management.SqlStudio.Explorer.pkgdef` — SSMS's
own registration, verbatim:

```
[$RootKey$\Packages\{687b26ef-c096-4f2d-9f8c-aaafada321ac}]
@="SqlStudioExplorer"
"Class"="Microsoft.SqlServer.Management.SqlStudio.Explorer.SqlStudioExplorer"
"CodeBase"="$PackageFolder$\Microsoft.SqlServer.Management.SqlStudio.Explorer.dll"
…
[$RootKey$\Services\{26e139fb-2dd3-49c1-be58-3f8da45dba3a}]
@="{687b26ef-c096-4f2d-9f8c-aaafada321ac}"
"Name"="Object Explorer Service"
…
[$RootKey$\ToolWindows\{d114938f-591c-46cf-a785-500a82d97410}]
"Name"="Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerToolWindow"
```

So `IObjectExplorerService` is a plain `GetService(typeof(IObjectExplorerService))` away from
any `AsyncPackage`. `ServiceCache` (`Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache`,
**public**, `SqlPackageBase.dll`) is also intact and exposes `ServiceProvider`, `VsUIShell`,
`ExtensibilityModel` (`EnvDTE._DTE`) and `ScriptFactory` — but its old
`GetObjectExplorer()` helper is gone; the generic `GetService<T>()` is now private. Use
`ServiceCache.ServiceProvider.GetService(...)` or the package's own `GetService`.

Selection-change notification (needed to inject the menu item before the menu is built):

```csharp
// Microsoft.SqlServer.Management.Sdk.SqlStudio.dll — public
public interface IContextService {
    IContextProvider           ActionContext         { get; }
    INavigationContextProvider ObjectExplorerContext { get; }   // -> CurrentContextChanged
}
```

---

## 4. Minimal working sketch

Verified against the IL of `ExplorerHierarchyNode.ShowContextMenu`,
`DefaultMenuHandler.AddChild/GetMenuItems`, and against Red Gate's shipping implementation.

```csharp
using System;
using System.Windows.Forms;
using Microsoft.SqlServer.Management;                                   // IContextService
using Microsoft.SqlServer.Management.Common;                            // SqlConnectionInfo
using Microsoft.SqlServer.Management.Sdk.Sfc;                           // Urn
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

// ---------------------------------------------------------------- the menu contribution
internal sealed class AnalyzeMenuHandler : IMenuHandler, IWinformsMenuHandler
{
    private readonly Action<INodeInformation> _onInvoke;
    public AnalyzeMenuHandler(Action<INodeInformation> onInvoke) { _onInvoke = onInvoke; }

    public INodeInformation Parent { get; set; }                 // set by SSMS after AddChild
    public System.ComponentModel.Design.CommandID ContextMenuID => null;
    public event EventHandler OnRefresh { add { } remove { } }
    public void FillMenuCommands(System.ComponentModel.Design.IMenuCommandService s) { }
    public void UpdateMenuCommandsStatus(System.ComponentModel.Design.MenuCommand c) { }
    public void DoDefaultAction() { }
    public void InvokeProperties() { }

    public ToolStripItem[] GetMenuItems()
    {
        var node = Parent;
        if (node == null || !IsUserTable(node)) return new ToolStripItem[0];   // table nodes only

        var item = new ToolStripMenuItem("Analyze Data…");
        item.Click += (s, e) => _onInvoke(node);
        return new ToolStripItem[] { new ToolStripSeparator(), item };
    }

    private static bool IsUserTable(INodeInformation n) =>
        string.Equals(n.UrnPath, "Server/Database/Table", StringComparison.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------- wiring (from AsyncPackage)
internal sealed class OeContextBridge : IDisposable
{
    private readonly IObjectExplorerService _oe;
    private readonly INavigationContextProvider _ctx;
    private readonly System.Collections.Generic.HashSet<object> _patched = new …();
    private readonly Action<INodeInformation> _onInvoke;

    public OeContextBridge(IServiceProvider sp, Action<INodeInformation> onInvoke)
    {
        _onInvoke = onInvoke;
        _oe  = (IObjectExplorerService)sp.GetService(typeof(IObjectExplorerService));
        _ctx = ((IContextService)sp.GetService(typeof(IContextService))).ObjectExplorerContext;
        _ctx.CurrentContextChanged += OnContextChanged;      // fires on every OE selection change
    }

    private void OnContextChanged(object sender, NodesChangedEventArgs e)
    {
        var changed = e.ChangedNodes;
        if (changed == null || changed.Count == 0) return;
        INodeInformation node = changed[0];                  // INavigationContext : INodeInformation
        if (node == null) return;

        // The node is its own IServiceProvider; ask it for the menu handler SSMS will use.
        var handler = ((IServiceProvider)node).GetService(typeof(IMenuHandler)) as IMenuHandler;
        var host    = handler as HierarchyObject;            // DefaultMenuHandler is a HierarchyObject
        if (host == null || !_patched.Add(host)) return;     // patch each handler exactly once

        host.AddChild("SsmsDataAnalyzer.Analyze", new AnalyzeMenuHandler(_onInvoke));
    }

    public void Dispose() => _ctx.CurrentContextChanged -= OnContextChanged;
}
```

`_patched` matters: `CurrentContextChanged` fires on every selection, and `AddChild` appends
unconditionally — without the guard you get a duplicate menu item per click. Use a
`HashSet<object>` with reference identity (Red Gate keeps a `List<HierarchyObject>` and does
`Contains` for exactly this reason). Prefer a
`ConditionalWeakTable<object,object>`/weak set so it does not root disposed OE nodes.

### 4.1 Extracting (server, database, schema, table) + connection from the clicked node

```csharp
internal static bool TryGetTable(INodeInformation node,
                                 out string server, out string database,
                                 out string schema, out string table,
                                 out string connectionString)
{
    server = database = schema = table = connectionString = null;

    // ---- identity: the URN ------------------------------------------------------------
    // node.Context looks like:
    //   Server[@Name='MYBOX']/Database[@Name='Sales']/Table[@Name='Orders' and @Schema='dbo']
    var urn = new Urn(node.Context);
    if (!string.Equals(urn.Type, "Table", StringComparison.OrdinalIgnoreCase)) return false;

    table    = urn.GetAttribute("Name");                 // "Orders"
    schema   = urn.GetAttribute("Schema");               // "dbo"
    database = urn.GetAttribute("Name", "Database");     // "Sales"
    server   = urn.GetAttribute("Name", "Server");       // "MYBOX"

    // ---- connection -------------------------------------------------------------------
    var ci = node.Connection as SqlConnectionInfo;       // SqlOlapConnectionInfoBase
    if (ci == null) return false;

    // SqlOlapConnectionInfoBase: ServerName / DatabaseName / UserName / Password /
    //                            SecurePassword / UseIntegratedSecurity / ConnectionString
    // Prefer these over the URN when they disagree (OE aliases, "." vs machine name).
    server   = ci.ServerName   ?? server;
    if (!string.IsNullOrEmpty(ci.DatabaseName)) database = ci.DatabaseName;

    // Option A — build our own string for Core (recommended: Core owns its lifetime,
    //            and OE's DatabaseName is often "master" while the URN names the real db).
    connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
    {
        DataSource            = server,
        InitialCatalog        = database,
        IntegratedSecurity    = ci.UseIntegratedSecurity,
        UserID                = ci.UseIntegratedSecurity ? null : ci.UserName,
        Password              = ci.UseIntegratedSecurity ? null : ci.Password,
        ApplicationName       = "SSMS Data Analyzer",
        TrustServerCertificate= ci.TrustServerCertificate
    }.ConnectionString;

    // Option B — reuse SSMS's own live connection (do NOT do this for profiling:
    //            long-running queries on the shared OE connection block the UI).
    //   var live = ci as SqlConnectionInfoWithConnection;   // .ServerConnection (SMO)
    //   IDbConnection c = ci.CreateConnectionObject();

    return true;
}
```

Notes on the above, all verified:

* `Urn` — `Microsoft.SqlServer.Management.Sdk.Sfc.Urn`, **public**, has
  `Type`, `Parent`, `Value`, `GetAttribute(string)`, `GetAttribute(string attr, string type)`.
  `"Name"` and `"Schema"` are the right attribute names — Red Gate's `GetDatabaseObject`
  calls exactly `GetAttribute("Name")` and `GetAttribute("Schema")` on the same URN.
* `SqlOlapConnectionInfoBase` (public, `Microsoft.SqlServer.ConnectionInfo.dll`) exposes
  `ServerName`, `DatabaseName`, `UserName`, `Password`, `SecurePassword`,
  `UseIntegratedSecurity`, `ConnectionString` (get-only), `CreateConnectionObject()`.
  `SqlConnectionInfo` adds `Authentication`, `TrustServerCertificate`, `EncryptConnection`,
  `AccessToken` (`IRenewableToken`, for Entra ID) and `ApplicationName`.
* **Azure / Entra ID caveat:** for token-based connections `Password` is empty and the
  credential lives in `SqlConnectionInfo.AccessToken`. For M3 scope, detect
  `ci.AccessToken != null` and fall back to Tier B's picker rather than producing a broken
  connection string.
* `node.CreateObjectInstance()` returns the SMO `Table` object if you ever want it — it is
  public, but it forces an SMO round-trip; the URN is cheaper and Core only wants strings.
* Multi-select: `IObjectExplorerService.GetSelectedNodes(ref int, ref INodeInformation[])`
  is the way to read the full selection at click time. Note the odd `ref`-array signature.

---

## 5. Registration mechanism

**Nothing OE-specific is registered.** Tier A needs no `.vsct`, no `.oexml`, no special
registry key — it is pure runtime code inside a normally-registered VSPackage. What you do
need is for the package to be **loaded** before the user right-clicks, i.e. an autoload rule.

Both third-party extensions on this box do it the same way. `Extensions\SQLPrompt\RedGate.SQLPrompt.SsmsPackage22.pkgdef`, verbatim and complete:

```
[$RootKey$\InstalledProducts\SQLPromptSsmsPackage22]
@="#110"
"Package"="{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}"
"PID"="1.0"
"ProductDetails"="#112"
"LogoID"="#400"
[$RootKey$\Packages\{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}]
@="SQLPromptSsmsPackage22"
"InprocServer32"="$WinDir$\SYSTEM32\MSCOREE.DLL"
"Class"="RedGate.SqlPrompt.SsmsPackage22.SsmsPackage"
"CodeBase"="C:\PROGRA~2\Red Gate\SQL Prompt 11\RedGate.SqlPrompt.SsmsPackage22.dll"
"AllowsBackgroundLoad"=dword:00000001
[$RootKey$\AutoLoadPackages\{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}]
"{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}"=dword:00000002
[$RootKey$\Menus]
"{e33b8a3b-d1cf-4eb0-92aa-0590f0b55b1a}"=", Menus.ctmenu, 1"
```

`SqlLizard\SSMSLizard.pkgdef` (UTF-16, same shape) autoloads on
`{adfc4e64-0397-11d1-9f4e-00a0c911004f}` = `UICONTEXT_SolutionExists`, registers a
`ToolWindows` entry and `ToolsOptionsPages`.

For us this is just the standard attribute set on the `AsyncPackage` — the VSSDK generates
the identical pkgdef:

```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]                       // for Tier B/C only
[ProvideToolWindow(typeof(ProfileToolWindow))]
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string,
                 PackageAutoLoadFlags.BackgroundLoad)]         // must be loaded before first right-click
public sealed class DataAnalyzerPackage : AsyncPackage { … }
```

VSIX manifest target — Microsoft's own SSMS 22 extensions use
`<InstallationTarget Id="Microsoft.VisualStudio.Ssms" Version="[22.0,)">` with
`<ProductArchitecture>amd64</ProductArchitecture>`, which is what PLAN.md §1 already
specifies — **confirmed correct**. (SqlLizard uses the short alias `Id="ssms"`; both load,
but match Microsoft.)

### 5.1 The `.oexml` mechanism — real, but a dead end for us

Worth recording because it looks promising and is not: `XmlHierarchyBuilder.LoadExtensionHierarchies`
scans **two** directories for `*.oexml` (IL: `Environment.GetFolderPath(26 /* ApplicationData */)`
→ `Path.Combine(appData, "Microsoft", "SQL Server Management Studio")`, plus
`Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)` i.e. the IDE folder), and
merges them into the OE hierarchy. The schema (from `ProcessExtensionHierarchy` /
`ProcessExtensionMenuItem` IL) is:

```xml
<MenuItem Name="..." Text="..." UrnPaths="Server/Database/Table" ParentObjects="...">
  <Children><Child Name="SomeExistingItem" /></Children>
  <VisibleExpression>...</VisibleExpression>
</MenuItem>
```

Two blockers:

1. `<MenuItem>` is hard-wired to construct SSMS's own internal `ExtensionMenuHandler`
   (`ldtoken ExtensionMenuHandler; …; IObjectBuilder::SetType`) — you cannot name your own type.
   `<Child>` only *references an existing named object* by name; it cannot define a new action.
2. `<ActionMenuItem>` — the element that would define a new clickable action — is
   **stubbed out in 22.9**: `XmlHierarchyBuilder::ProcessExtensionActionMenuItem` is literally
   `ldnull; ret`, and its caller `AddExtensionObject` throws `ArgumentNullException("builder")`
   on null (swallowed by the surrounding `Trace.TraceError`). So `<ActionMenuItem>` silently
   does nothing.

No `.oexml` files ship with SSMS 22 and none exist in `%APPDATA%\Microsoft\SQL Server Management Studio`
on this machine. Treat `.oexml` as vestigial.

For reference, the built-in table menu is defined in `ObjectExplorer.dll`'s embedded
`sqlexplorerhier.xml` / `sqlexplorermenu.xml` (extractable with `OeProbe res`):

```xml
<Object name='Table' base='TableBase'>
  <Properties>
    <Property Name='Xpath'>Table</Property>
    <Object name='MenuHandler' base='TableItemMenu'></Object>
  </Properties>
  …
</Object>

<Object name='TableItemMenu' base='DefaultMenuHandler'>
  <Children>
    <Child Name='DesignTable' /> <Child Name='SelectTable' /> <Child Name='EditTable' />
    <Child Name='TableItemScriptMenuHandler' /> … <Child Name='TableProperties' />
  </Children>
</Object>
```

Note `TableBase` carries `<Property Name='UrnShell'>Server/Database/Table</Property>` — this is
the `UrnPath` we filter on. Beware there are many table variants
(`UserTable100/110/120/130/140`, `MemoryOptimizedTable*`, `TemporalTable*`, `LedgerTable*`,
`FileTable`, `ExternalTable`, `SystemTable`, `TablesSqlAzureV12`, `TablesSqlDw`) — they **all**
share `UrnPath == "Server/Database/Table"`, so a single `UrnPath` test covers every one, which
is exactly what we want.

---

## 6. Evidence from the two working extensions

### SQL Prompt 11 — **does exactly Tier A**, and is the strongest evidence we have

`RedGate.SqlPrompt.ShellAbstraction.22.dll` (there is a per-shell DLL:
`…ShellAbstraction.2016/2017/18/19/20/21/22/2022/2026.dll` — they version-fork this API on purpose).

```
internal sealed class RedGate.SqlPrompt.Shell.MenuManagerManager
    : RedGate.SqlPrompt.Shell.Api.ObjectExplorer.IObjectExplorerMenuManager,
      Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IMenuHandler,
      Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IWinformsMenuHandler
{
    private List<HierarchyObject> _handledMenus;
    public  ToolStripItem[] GetMenuItems();
    public  void AddToMenuIfNeeded(object);
    private void AddChild(HierarchyObject);
    …
}
```

Their flow, read out of the IL:

1. `ObjectExplorer.HookUpEvents()`
   → `GetService(typeof(IObjectExplorerService))`
   → `GetService(typeof(Microsoft.SqlServer.Management.IContextService)).ObjectExplorerContext`
   → `+= CurrentContextChanged`.
2. `SetSelectedNode(INodeInformation node)`
   → `node.GetService(typeof(IMenuHandler))` → `IObjectExplorerMenuManager.AddToMenuIfNeeded(handler)`.
3. `AddToMenuIfNeeded` → `if (!_handledMenus.Contains(h) && GetMenuItems().Length != 0) { AddChild(h); _handledMenus.Add(h); }`
   — the de-dupe guard I flagged in §4.
4. `AddChild` uses **private reflection** (`ReflectionUtils.GetFieldValue(h,"subMenus")`,
   `…"menuItemsInOrder"`) and then locates its insert position by scanning
   `menuItemsInOrder` for the entry whose `IMenuHandler.ContextMenuID.Guid.ToString()` ==
   **`"b574f3b6-89b8-4b45-9255-e393c072441b"`** and `.ID == 4` and whose type name is
   **`"Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.DefaultMenuHandler"`**
   — independently confirming the guid/id/type I read out of `ObjectExplorer.dll`.
5. Separately, `AddHookUpEventListener` reflects `IObjectExplorerService`'s **internal**
   `Tree` property (type `ObjectExplorerControl`) and hooks its `onAfterSelect` event.

We need only steps 1–3 plus the public `HierarchyObject.AddChild`; steps 4–5 are Red Gate
buying menu-item *ordering* and an extra selection signal, and are where their fragility lives.

### SQL Lizard 2.0 — reads OE, does **not** add menu items

`SSMSLizardCore.dll` binds early (compile-time `MemberRef`s, no reflection) to:

```
IObjectExplorerService::GetSelectedNodes(ref int, ref INodeInformation[])
IObjectExplorerService::SynchronizeTree(INodeInformation)
INodeContext::get_Connection()  -> SqlOlapConnectionInfoBase
INodeContext::get_UrnPath()
INodeInformation::get_Parent(), ::get_InvariantName()
IExplorerHierarchy::get_Root()  -> HierarchyTreeNode
LazyNode::EnumerateChildren()
```

with helpers `ObjectExplorerHelper.FindSelectedNode/FindSelectedDatabaseNode/FindParentServerNode/
GetServersConnection/SelectNode` and constants `SERVER_URNPATH`, `DB_URNPATH`. Its
"Object Explorer Search" is a tool window, not a context menu. Its
`extension.vsixmanifest` targets `<InstallationTarget Id="ssms" Version="[22.0,)">` with
`<ProductArchitecture>amd64</ProductArchitecture>`.

**Net:** SQL Lizard proves the *read* half (URN + connection off a selected node) works in
SSMS 22 with plain compile-time references. SQL Prompt proves the *menu* half.

---

## 7. Risks, and what breaks on an SSMS update

| # | Risk | Likelihood | Blast radius | Mitigation |
|---|---|---|---|---|
| 1 | `DefaultMenuHandler` stops implementing `IWinformsMenuHandler` (MS moves OE to WPF/ctmenu) | low-medium — this is the most plausible future change, given SSMS 22 is mid-migration to the VS 18 shell | Tier A dies entirely | one `try/catch` around bridge init + feature flag → Tier B. Our `AddChild` would simply have no visible effect, not crash |
| 2 | `HierarchyObject.AddChild` signature/semantics change | low — it is abstract public API used across many MS assemblies | Tier A dies | `MissingMethodException` at the guarded call site |
| 3 | `IContextService` / `ObjectExplorerContext` moves | low | no injection point; menu never appears | fall back to polling `GetSelectedNodes` from a `ToolStripDropDown.Opening`-less path, or Tier B |
| 4 | Assembly version roll `22.200.0.0` → `23.x` | **certain** on SSMS 23 | binding failure at load | we already pin `InstallationTarget [22.0,)`; add binding-redirect-free `<Private>false</Private>` refs and gate on `ServiceCache.ShellMode`/version at startup |
| 5 | Duplicate menu items | **certain if unguarded** | user-visible bug | the `_patched` identity set in §4 — non-optional |
| 6 | Our `GetMenuItems()` throws | medium during development | **the entire node context menu fails to open** — worst UX failure mode in this design | `GetMenuItems` must be a total function: wrap its whole body in `try/catch` and `return new ToolStripItem[0]` on any error |
| 7 | Long-running profiling on SSMS's shared OE connection | high if we take Option B in §4.1 | SSMS UI hangs | always build our own `SqlConnection` from the connection info; never reuse `SqlConnectionInfoWithConnection.ServerConnection` |
| 8 | Entra ID / access-token connections | medium | broken connection string | detect `SqlConnectionInfo.AccessToken != null` → degrade to Tier B picker |
| 9 | Package not yet loaded on first right-click | high without autoload | item missing until something else loads us | `[ProvideAutoLoad(ShellInitialized, BackgroundLoad)]` |

Risk 6 deserves emphasis: unlike a `.vsct` command, a throwing `IWinformsMenuHandler` is
inside SSMS's own menu-construction path. **`GetMenuItems()` must never throw.**

---

## 8. Recommendation to the lead

1. **Keep the PLAN.md build order.** Tier B first is still right — it is the product's
   spine, it is what the CLI and the tool window need anyway, and Tier A is a 150-line
   adapter on top of it.
2. **Change PLAN.md §4 "late-bound reflection" to "compile-time references, guarded".**
   Reflection is unnecessary here and would make `OeContextBridge.cs` three times the size
   for no additional safety. (Lead's call — flagging, not editing.)
3. **Drop `.vsct` from the Tier A design.** It cannot work; keep `.vsct` for Tier B's
   top-level menu and Tier C's editor command.
4. **M0 exit criterion for this spike is met and can be marked resolved**: Tier A is
   confirmed feasible on SSMS 22. The remaining M0 blocker is unrelated — PLAN.md §1's
   "VS extension-development workload is NOT installed".
5. Tier B's API, if Tier A is ever disabled, is confirmed present:
   `ServiceCache.ScriptFactory` → `IScriptFactory.CurrentlyActiveWndConnectionInfo`
   (`CurrentlyActiveWndConnectionInfo.Live`, `.UIConnectionInfo`) where
   `Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo` gives
   `ServerName`, `UserName`, `Password`, `AuthenticationType`, `AdvancedOptions`
   (the current database is in `AdvancedOptions["DATABASE"]`). All public.

---

## Appendix — reproducing these findings

`spikes/OeProbe` is a `net8.0` console app that reads assembly metadata via
`System.Reflection.Metadata` only. It never loads or executes the assemblies it inspects, so
it needs none of their dependencies and cannot be affected by them.

```
cd spikes/OeProbe && dotnet build
set IDE=C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE

dotnet run -- survey  "%IDE%" --ns VSIntegration.ObjectExplorer
dotnet run -- types   "%IDE%\SqlWorkbench.Interfaces.dll" --ns ObjectExplorer --all
dotnet run -- members "%IDE%\SqlWorkbench.Interfaces.dll" --type .IObjectExplorerService
dotnet run -- il      "%IDE%\ObjectExplorer.dll" --type DefaultMenuHandler --method AddChild
dotnet run -- il      "%IDE%\ObjectExplorer.dll" --type ObjectExplorerGuids --method .cctor
dotnet run -- res     "%IDE%\ObjectExplorer.dll" --name sqlexplorer --out .\res
dotnet run -- refs    "C:\Program Files (x86)\Red Gate\SQL Prompt 11\RedGate.SqlPrompt.ShellAbstraction.22.dll" --ns ObjectExplorer
dotnet run -- strings "%IDE%\ObjectExplorer.dll" --grep oexml
```

Verbs: `types`, `members`, `refs` (what an assembly *consumes* — how the third-party
evidence was gathered), `strings`, `survey`, `il` (token-resolving disassembler), `res`
(embedded-resource extractor).
