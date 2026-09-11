# Current build

**`SsmsDataAnalyzer.vsix` — version 0.15.0**

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
| Paste a list as an IN clause | In a query window, right-click → **Paste as SQL IN (...)** |
| Compare rows side by side | Select rows in the results grid → right-click → **Pivot selected rows…** |
| Settings | **Tools → Options… → SSMS Data Analyzer** |

## Version history

**0.15.0** — New: **Aggregate selection…** on the results grid's right-click menu (also under Tools). Select cells and get **Count, Distinct, Sum, Average, Min, Max** in a small popup, formatted with your Windows regional settings (thousands separators included). NULLs are left out like in SQL; if some values aren't numbers, Sum/Average show "—" and say how many. Ctrl+C copies the results; **Esc** closes it.

**0.14.3** — Closing **Find in Results** (Esc or X) now really removes the yellow highlights from the results grid. 0.14.2 closed the window but left the colours behind.

**0.14.2** — **Esc** now closes the **Find in Results** window too. Closing it (Esc or X) also clears the yellow match highlights from the results grid.

**0.14.1** — **Esc** now closes the Peek window. Before, SSMS used Esc to jump back to the query and the window stayed open.

**0.14.0** — New: **Peek source for this value**, next to Go to source on the results grid's right-click menu (and **Peek source…** in pivot windows). Instead of opening a new query tab, it shows the linked record right away in a small floating window — glance at it, then close it with **X** (or **Esc**). Foreign-key icons inside the peek window work too, so you can follow links further. Go to source is unchanged; pick whichever you need each time.

**0.13.3** — A keyboard shortcut assigned to **Go to source for this value** now works: it uses the grid's current cell (the one you last clicked or moved to with the arrow keys). Previously it looked for a cell under the mouse pointer, so a shortcut did nothing unless the pointer happened to be over the right cell. Go to source always follows one value; when several cells are selected, the status bar says which row it used.

**0.13.2** — "Go to source" and pivot FK links now use the query text that was actually **run**, not whatever the query window holds when you right-click. Previously, editing or pasting into the window after running a query (for example pasting copied rows above it) made them decline with a syntax error.

**0.13.1** — When "Go to source" or pivot FK links decline because SQL Server couldn't describe the query, the status bar now shows SQL Server's actual error number and message instead of a generic guess, so the cause can be found. (Shown on screen only, never written to logs.)

**0.13.0** — Pivot extras: a **Header** dropdown labels each pivot column by a chosen column's value (e.g. `ID = 4522`) instead of `Row 7`; every pivot now opens in its own tab (Pivot 1, Pivot 2, …) instead of replacing the previous one; right-click → **Copy as Markdown table** copies what's currently shown, ready for Jira or Confluence. Also: "Go to source" no longer writes clicked cell values into SSMS's ActivityLog — the full message is still shown on the status bar.

**0.12.1** — Pivot look: cells now have vertical grid lines and the link icon has space around it, so it clearly belongs to its own value instead of seeming to point at the next column. The icon is now "open in new window" rather than an arrow.

**0.12.0** — Pivot: foreign-key columns now get a small arrow inside each cell (like DBeaver). Click it to open the linked record in a new, connected query window — the same as **Go to source…**. FK column names show a link marker, and hovering the arrow shows where it goes. Right-click a pivot cell → **Go to source…** does the same from the keyboard. If the query can't be traced back to its tables, the pivot says why and simply shows no arrows.

**0.11.1** — Pivot: right-clicking a row that is **not** part of your selection now pivots just that row. Previously it pivoted the old selection, because SSMS keeps the selection when you right-click elsewhere. Right-clicking inside the selection still pivots all selected rows.

**0.11.0** — New: **Pivot selected rows…** on the results grid's right-click menu (also under Tools). Turns the selected rows sideways — column names down the left, one column per row — so wide rows can be read and compared. Columns whose values differ between the rows are highlighted; toggles show only differing columns or hide all-NULL ones, and a filter box narrows columns by name. Ctrl+C copies selected cells for Excel. Shows at most 100 rows (change it in Options → Pivot); anything beyond that is named in the banner. The values are a snapshot taken when you open it.

**0.10.0** — The **Type** column in Analyze Data now shows the declared size: `nvarchar(50)`, `decimal(18,2)`, `datetime2(7)`, `nvarchar(max)` — instead of just the bare type name.

**0.9.4** — "Paste as SQL IN" now actually appears on the query editor's right-click menu. Earlier builds attached it to the generic Visual Studio editor menu; SSMS's query editor uses its own.

**0.9.3** — You can now assign your own keyboard shortcuts to these commands: **Tools → Options → Environment → Keyboard**, search for `SsmsDataAnalyzer`.

**0.9.2** — "Paste as SQL IN" should now appear on the query editor's right-click menu as well as under Tools.

**0.9.1** — Fixed the new "Paste as SQL IN" items not appearing on the query editor's right-click menu. Also added them to the **Tools** menu.

**0.9.0** — New: **Paste as SQL IN (...)** and **Paste as numeric SQL IN (...)** on the query editor's right-click menu. Turns a list of values from the clipboard (a spreadsheet column, an email) into a ready-to-use IN list — de-duplicated, apostrophes escaped, `N` prefix added automatically for accented text. The numeric variant omits the quotes, and refuses (naming the offending value) if something isn't a number.

**0.8.2** — Fixed "Analyze Data..." missing from the right-click menu on the very first right-click after connecting to a server (it appeared from the second click onward).

**0.8.1** — Fixed "Go to source" refusing to work on query windows using Windows Authentication (it mistook them for Entra sign-ins).

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
