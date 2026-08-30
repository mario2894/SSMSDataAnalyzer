# Building, debugging, and installing SsmsDataAnalyzer.Vsix

Target: **SSMS 22 only** (`22.9.12105.275`), Visual Studio **18.x** shell, amd64,
.NET Framework 4.7.2 hosted. See `CONTRACT.md` and `PLAN.md` at the repo root.

## 1. Prerequisites — what must be installed

**Update:** command-line builds (`msbuild ...csproj`) have been proven working on this
machine **without** the VS "extension development" workload installed — see section 5 for
why, and CONTRACT.md Amendment 5 for the investigation. The workload is still recommended
for a good IDE authoring experience (VSCT/manifest designers, F5 debugging support), but it
is not a hard requirement for `msbuild`-driven builds of this project as currently
configured.

If you want the full IDE experience anyway, install via the Visual Studio Installer on
**VS 2022** (the shell version used to author against; the *output* still runs inside SSMS
22's bundled VS 18 shell — SSMS does not need its own separate SDK):

- Workload: **Visual Studio extension development**
- Individual component (usually included by the workload, verify it's checked):
  **.NET Framework 4.7.2 targeting pack** (or 4.8 — the project targets `net472`)

`Microsoft.VSSDK.BuildTools` (a NuGet package referenced in the `.csproj`) supplies the
actual VSCT-compile / pkgdef-generation / VSIX-packaging tooling at restore/build time. If
NuGet restore cannot find `Microsoft.VisualStudio.SDK` or `Microsoft.VSSDK.BuildTools`,
ensure `nuget.org` is reachable/configured as a package source
(`%APPDATA%\NuGet\NuGet.Config`).

## 2. Build

From a **Developer Command Prompt for VS 2022** (or with `vcvars`/MSBuild on PATH):

```bat
msbuild src\SsmsDataAnalyzer.Vsix\SsmsDataAnalyzer.Vsix.csproj /restore /p:Configuration=Debug
```

Or, once the solution exists (owned by the lead):

```bat
msbuild SsmsDataAnalyzer.sln /restore /p:Configuration=Debug
```

**Proven working end-to-end** (Core builds clean, Vsix builds clean) as of this writing.
Output artifacts:

- `src\SsmsDataAnalyzer.Vsix\bin\Debug\net472\SsmsDataAnalyzer.Vsix.dll`
- `src\SsmsDataAnalyzer.Vsix\bin\Debug\net472\SsmsDataAnalyzer.Vsix.pkgdef`
- `src\SsmsDataAnalyzer.Vsix\bin\Debug\net472\SsmsDataAnalyzer.Vsix.vsix` — the installable
  package, verified to contain `extension.vsixmanifest`, the compiled assembly, the merged
  VSCT resources, `Resources\DataAnalyzer.ico`, the `.pkgdef`, and `SsmsDataAnalyzer.Core.dll`
  plus its `Microsoft.Data.SqlClient` runtime dependencies (pulled in transitively through
  the Core `ProjectReference` — expected, since Core needs them at runtime for its own SQL
  connections; nothing SSMS-owned is copied in, per the `<Private>false</Private>` rule).

If your SSMS 22 install lives somewhere other than
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`, override
the IDE path used to locate `ObjectExplorer.dll` / `sqlmgmt.dll` / `SqlWorkbench.Interfaces.dll`
/ SMO assemblies:

```bat
msbuild src\SsmsDataAnalyzer.Vsix\SsmsDataAnalyzer.Vsix.csproj /restore /p:SsmsIdeDir="D:\Other\Path\IDE"
```

These SSMS-owned references are all marked `<Private>false</Private>` in the `.csproj` —
**do not remove that** — they must never be copied into the VSIX output. We run inside
SSMS's own process, which already has them loaded; a second copy causes type-identity
mismatches or masks a servicing update.

## 3. Debug

The project is pre-configured (`StartProgram` / `StartArguments` in the `.csproj`) to launch
SSMS 22 with the **experimental hive** on F5, so the daily-driver SSMS profile stays clean:

```
C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe /rootsuffix Exp
```

`GeneratePkgDefFile` + `DeployExtension=false` plus this `StartProgram` means F5 debugging
under Visual Studio (once the extension-development workload is installed there) builds the
package, registers it into the `Exp` hive via the normal experimental-deployment mechanism,
and launches SSMS pointed at it — standard VSIX F5 debug loop.

To reset the experimental hive if it gets into a bad state (stale MEF cache, etc.):

```bat
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe" /rootsuffix Exp /resetsettings
rmdir /s /q "%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_*Exp"
```

(Adjust the hive folder name to whatever `/rootsuffix Exp` actually created under
`%LOCALAPPDATA%\Microsoft\VisualStudio\` — list that directory to confirm the exact
`18.0_<hash>Exp` name on this machine.)

## 4. Install the built VSIX (outside of F5 debugging)

SSMS 22 ships its own `VSIXInstaller.exe` — use *that* one, not any copy bundled with
Visual Studio itself, so the extension registers against the right instance:

```bat
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" src\SsmsDataAnalyzer.Vsix\bin\Debug\SsmsDataAnalyzer.Vsix.vsix
```

To target the experimental instance instead of the real one, add `/rootSuffix:Exp`. To
target a specific instance ID non-interactively:

```bat
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /instanceIds:<ssms22-instance-id> src\SsmsDataAnalyzer.Vsix\bin\Debug\SsmsDataAnalyzer.Vsix.vsix
```

(List installed instance IDs from `VSIXInstaller.exe /?` or by inspecting
`%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_*\` folder names.)

Alternative manual install (useful for a quick local test without the installer UI): copy
the **unpacked** extension folder (project output, not the `.vsix` archive itself) into

```
%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_<hash>\Extensions\<YourPublisher>\SsmsDataAnalyzer\
```

then restart SSMS. This is what the `.vsix` installer does under the hood.

## 5. Manifest target — why it's pinned the way it is

`source.extension.vsixmanifest` declares:

```xml
<InstallationTarget Id="Microsoft.VisualStudio.Ssms" Version="[22.0,)">
  <ProductArchitecture>amd64</ProductArchitecture>
</InstallationTarget>
```

This mirrors SSMS's own self-declared `InstallationTarget` in
`Extensions\Application\extension.vsixmanifest` inside the SSMS 22 IDE folder, which is the
empirical ground truth for what ID this SSMS build answers to. Two proven third-party
extensions already installed in this exact SSMS 22 (`Extensions\SQLPrompt`,
`Extensions\SqlLizard`) confirm the pattern works — SqlLizard's manifest uses the shorthand
`Id="ssms"` for the same target; we use the fully-qualified `Microsoft.VisualStudio.Ssms`
form per PLAN.md's decision to match the Application manifest's own self-identity exactly.
**Do not widen this to a VS-generic target or add a `[17.0,18.0)` VS-shell branch** — this
extension is intentionally SSMS-22-exclusive.

## 6. Known gaps in this scaffold

- `Resources\DataAnalyzer.ico` is a placeholder single-color icon generated for packaging
  purposes only — replace with real artwork before any release.
- The VSCT command lives on the top-level **Tools** menu ("Analyze Data...") — this is the
  Tier B entry point per PLAN.md. Object Explorer right-click integration (Tier A) is
  Agent C's separate investigation (`spikes/OeProbe/`, `docs/oe-api.md`) and is intentionally
  not wired up here.
- `ProfileViewModel` currently builds its connection string from `Server`/`Database` text
  boxes with `Integrated Security=true`. It does not yet seed from the active query window's
  connection (`DTE.ActiveDocument` / `SqlWorkbench.Interfaces`) as PLAN.md describes for
  Tier B — that's next once `SsmsDataAnalyzer.Core` exists on disk and an end-to-end build is
  possible.
- `DataAnalyzerOptionsPage` (Tools > Options) is not yet wired to feed
  `ProfileViewModel.Options` — see the `TODO(M2+)` comment in `ProfileViewModel.cs`.
