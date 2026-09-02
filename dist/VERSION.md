# Current build

**`SsmsDataAnalyzer.vsix` — version 0.8.0**

Install: download the `.vsix` in this folder, close SSMS, double-click the file, reopen SSMS.
Full instructions in the [main README](../README.md#installing-it).

Requires **SSMS 22**. Every feature — Analyze Data, Find in Results, Go to source — works on
every SSMS 22 build. (v0.7.6 briefly needed a newer 22.x build for the results-grid features;
v0.8.0 moved them onto a results-grid API confirmed present as far back as SSMS 21, so that
requirement is gone. The graceful-degradation safety net from v0.7.6 — a hidden menu item and
a plain-language status-bar message instead of an error dialog — is kept in case some other,
still-unknown SSMS build surprise ever turns up.)

A handful of value types are still declined on "Go to source" for a results-grid cell, in
trade for that portability — see the version history entry below for exactly which ones and
why.

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

**0.8.0** — "Find in Results" and "Go to source" now work on every SSMS 22 build (previously some builds needed 22.9+ — see 0.7.6). The trade-off: "Go to source" reads a cell's on-screen text now, not its raw stored value, so it declines rather than guess for a few cases where that text can't be trusted to round-trip exactly: `float`/`real` values (shown rounded), `binary`/`varbinary`/`timestamp` values (shown as hex, with no way to confirm nothing was cut off), very long text or `xml` values (same truncation risk), and a cell that displays exactly "NULL" (indistinguishable from the literal word "NULL" stored in a text column). Every other type — whole numbers, `decimal`/`money`, dates and times, GUIDs, ordinary bounded text — still works exactly as before.

**0.7.6** — "Find in Results" and "Go to source" no longer crash SSMS with a raw .NET error dialog on an older SSMS 22 build that lacks the results-grid API they need; the menu items just don't appear, and if triggered anyway, the status bar says why. Analyze Data is unaffected either way.

**0.7.5** — When "Go to source" declines because the query and the grid disagree, it now says exactly how they disagree (both column counts, and the first column that differs).

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
