# Current build

**`SsmsDataAnalyzer.vsix` — version 0.7.4**

Install: download the `.vsix` in this folder, close SSMS, double-click the file, reopen SSMS.
Full instructions in the [main README](../README.md#installing-it).

Requires **SSMS 22**.

---

## What's in this build

| Feature | Where to find it |
|---|---|
| Analyze a table | Object Explorer → right-click a table → **Analyze Data…** |
| Search the analysis results | Click the panel → **Ctrl+F** |
| Search inside query results | Right-click the results grid → **Find…** |
| Jump to a linked record | Right-click a cell or column → **Go to source…** |
| Settings | **Tools → Options… → SSMS Data Analyzer** |

## Version history

**0.7.4** — "Go to source" now works with multi-statement queries (`USE ... GO ... SELECT`), with a selection, and when a tab shows more than one result grid.

**0.7.3** — Find in Results moved into a proper dockable panel; F3 / Shift+F3 now work there;
fixed the panel's layout.

**0.7.0** — Added Find for SSMS's own query results grid, which SSMS itself has no feature for.

**0.6.0** — "Go to source" queries now open connected and run automatically, using the
connection you were already working in.

**0.5.0** — Added "Go to source" to SSMS's query results grid, so any cell holding an ID can
jump to its parent record.

**0.4.0** — Added foreign-key navigation to the analysis panel.

**0.3.0** — Added search within the analysis panel, made the settings page functional, and added
a confirmation prompt before analysing very large tables.

**0.2.0** — Analysis is driven entirely from Object Explorer; removed the manual server/database
entry form.

**0.1.x** — First working version: right-click a table, get per-column fill rates, exact distinct
counts and last-fill dates.

---

## Note for maintainers

This file and the `.vsix` beside it are updated by hand when a build is released. If the version
above and the version inside the `.vsix` ever disagree, the `.vsix` is the truth — check it with:

```
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead("SsmsDataAnalyzer.vsix").Entries |
  Where-Object Name -eq "extension.vsixmanifest"
```

A GitHub **Release** with the `.vsix` attached would be the tidier long-term home for builds —
it gives a proper download page, release notes and version history without binaries accumulating
in the repository's history. Worth switching to if this gets more than a handful of users.
