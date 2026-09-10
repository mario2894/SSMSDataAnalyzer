# SSMS Data Analyzer

**Find out which columns in a database table are actually being used — and when each one was last filled in.**

An extension for SQL Server Management Studio 22. You don't need to write any SQL to use it:
right-click a table, and it tells you what's in it.

Useful if you need to answer questions like:

- *Is anyone still filling in this field, or is it dead?*
- *When did we stop using this column?*
- *How many different values does this field actually have?*
- *This ID points at another table — what's the actual record behind it?*
- *These two rows look the same — what's actually different between them?*

It also adds a few things SSMS itself doesn't have: searching query results, comparing rows
side by side, and turning a list of values into a SQL `IN (...)` clause.

## Everything it adds, at a glance

| What | Where to click |
|---|---|
| **Analyze a table** — fill rates, distinct counts, last-fill dates | Object Explorer → right-click a table → **Analyze Data…** |
| **Search the analysis** | Click the Analyze Data panel → **Ctrl+F** |
| **Search query results** | Right-click the results grid → **Find…** |
| **Jump to a linked record** | Right-click a cell in the results grid → **Go to source for this value** |
| **Compare rows side by side** | Select rows in the results grid → right-click → **Pivot selected rows…** |
| **Paste a list as `IN (...)`** | In a query window, right-click → **Paste as SQL IN (...)** |
| **Settings** | **Tools → Options… → SSMS Data Analyzer** |
| **Your own keyboard shortcuts** | **Tools → Options… → Environment → Keyboard** |

---

## Installing it

**You don't need to build anything.** The ready-to-install file is in this repository.

1. Open the **[`dist`](dist)** folder above → click **`SsmsDataAnalyzer.vsix`** →
   click the **Download** button (or the ⤓ icon) to save it.
2. **Close SSMS** if it's open. *(The installer can't replace files while SSMS is running.)*
3. **Double-click the downloaded file** and click through the installer.
4. **Open SSMS again.**

That's it — you'll find **Analyze Data…** when you right-click a table.

Requires **SSMS 22** (any build). It will not install on SSMS 21 or older, or on Visual Studio.

<details>
<summary>If double-clicking doesn't work</summary>

Run this instead, replacing the path at the end with wherever you saved the file:

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" "%USERPROFILE%\Downloads\SsmsDataAnalyzer.vsix"
```

To remove it:

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:SsmsDataAnalyzer.6f2b6e2a-6c2a-4e3a-9c9a-2f6b0c8a1a4d
```
</details>

### Updating to a newer version

Same steps — download the new file and install over the top. No need to uninstall first.
The current version and what changed in each one are listed in
[`dist/VERSION.md`](dist/VERSION.md).

---

## Feature 1 — Analyze a table

**Where:** Object Explorer (the tree on the left) → expand your database → **Tables** →
**right-click any table** → **Analyze Data…**

It uses the connection you're already signed in with — no passwords to re-enter.

A panel opens and fills in after a few seconds, one row per column of the table:

| Column in the panel | What it tells you |
|---|---|
| **Column** | The field name |
| **Type** | What kind of data it holds, with its declared size — `nvarchar(50)`, `decimal(18,2)`, `nvarchar(max)` |
| **Filled** | How many rows actually have a value here |
| **Fill %** | The same as a percentage — **the quickest thing to scan** |
| **Blank** | Rows containing empty text (counted separately from "no value at all") |
| **Distinct** | How many *different* values exist (always an exact count) |
| **Last Fill** | **When this column was last filled in** — see below |
| **Min / Max** | The smallest and largest values |
| **Flags** | A plain-English summary — see below |

### How to read it

**Fill %** is the fastest signal. A column at `0.4%` is filled in for 4 rows in every 1,000 —
almost certainly abandoned, or only used for one rare case.

**Last Fill** is the most useful column and the reason this tool exists. It answers
*"when did anyone last put something in this field?"* If a column shows `2019-03-11` and the
table has rows from last week, **people stopped using that field in 2019**. That's the evidence
you need to retire it.

It works from the table's creation-date column: `DateCreated`, or if there isn't one, the first
of `CreatedDate`, `CreatedOn`, `Created`, `InsertDate`, … that exists (the list can be changed in
[Settings](#settings)). If the table has none of them, Last Fill shows `n/a` and everything else
still works.

**Flags** call out the interesting cases automatically:

| Flag | Meaning |
|---|---|
| `DEAD` | Never filled in. Not once. |
| `SPARSE` | Filled in less than 5% of the time |
| `CONSTANT` | Every row has the *same* value — so it isn't telling you anything |
| `UNIQUE` | Every row has a *different* value — it's an identifier |

### In the panel

- **Copy as Markdown** / **Copy as CSV** (buttons at the top) — paste straight into a ticket, a
  document, or Excel.
- **Ctrl+F** — search the panel. Type, and matching cells are highlighted; **Enter** / **F3** for
  the next match, **Shift+Enter** / **Shift+F3** for the previous, **Esc** to close. Handy when a
  table has 150 columns and you want every `…Date` column or everything flagged `DEAD`.
- **Right-click a row** → **Go to source table** or **Go to source for this value** — see
  [Feature 3](#feature-3--jump-to-a-linked-record-go-to-source).
- **Cancel** stops a long analysis and keeps whatever was already worked out.

---

## Feature 2 — Search inside query results

SSMS has no way to search the results of a query. This adds one.

**Where:** run any query → **right-click anywhere in the results grid** → **Find…**

A **Find in Results** panel opens:

1. Type what you're looking for.
2. Press **Enter** or click **Find**.
3. **Enter** / **F3** for the next match, **Shift+Enter** / **Shift+F3** for the previous.

It searches **every row**, not just the ones on screen, and jumps to each match in turn.

> **Note:** Ctrl+F won't open this one — in query windows that shortcut belongs to SSMS and opens
> its own Find dialog. Use the right-click menu (or [assign your own shortcut](#keyboard-shortcuts)).

---

## Feature 3 — Jump to a linked record ("Go to source")

When a column holds an ID pointing at another table (a foreign key), this opens that other table
for you, already filtered to the matching record.

**Three places you can do it:**

| From | How |
|---|---|
| **Query results** | Right-click a cell containing an ID → **Go to source for this value** |
| **The Analyze Data panel** | Right-click a column's row → **Go to source table**, or right-click its **Min** / **Max** cell → **Go to source for this value** |
| **A pivot window** | Click the small icon inside a linked cell (see [Feature 4](#feature-4--compare-rows-side-by-side-pivot)) |

A new query tab opens, connected with your current sign-in and **already run**, showing the
record. (To review the query before it runs, turn off *Automatically execute the generated
query* in [Settings](#settings).)

**It never guesses.** The option is only offered when the link is certain. It is not offered for:

- columns that aren't a declared foreign key, or that point at several tables,
- foreign keys made of more than one column,
- calculated columns (e.g. `Price * 2`),
- values it can't safely turn back into SQL (`NULL` cells, `float` numbers, binary data, very
  long text).

In those cases the status bar at the bottom of SSMS says why.

Works with queries that use `USE`, `GO`, joins, aliases and several result grids. Only the
declared table structure is read to find the link — never your data.

---

## Feature 4 — Compare rows side by side ("Pivot")

Turns selected rows sideways: column names down the left, one column per row. Great for wide
tables, and for seeing exactly what differs between rows that look the same.

**Where:** select rows in the results grid → **right-click** → **Pivot selected rows…**
(also under the **Tools** menu)

```
Column        Row 4    Row 7    Row 9
ID            4        7        9
TestAKey 🔗    1   ⧉    2   ⧉    1   ⧉     ← foreign key: click ⧉ to open the linked record
Name          bbbb     ttt      gfhd     ← highlighted: values differ
```

### Selecting the rows

| To pivot… | Do this |
|---|---|
| One row | Right-click any cell in it |
| A range of rows | Click the first row number, **Shift+click** the last, then right-click inside the selection |
| Rows far apart | **Ctrl+click one cell in each row**, then right-click one of them. *(Ctrl+click on row numbers doesn't work — SSMS itself replaces the selection.)* |

Right-clicking **inside** your selection pivots the whole selection; right-clicking a row
**outside** it pivots just that row. All columns are always shown, however many cells you
selected.

### In the pivot window

| | |
|---|---|
| **Highlighted rows** | Columns whose values differ between the rows |
| **Show only differing columns** | Hides everything that's the same — often turns 150 columns into 3 |
| **Hide all-NULL columns** | Hides columns that are empty in every row |
| **Filter** | Narrows columns by name |
| **Header** | Label each row by a column's value (e.g. `ID = 4522`) instead of `Row 7` |
| **🔗 and ⧉** | Foreign-key column; click ⧉ in a cell to open the linked record ([Feature 3](#feature-3--jump-to-a-linked-record-go-to-source)). Hover it to see where it goes. Right-click a cell → **Go to source…** does the same. |
| **Ctrl+C** / **Ctrl+A** | Copy selected cells (pastes into Excel) / select all |
| **Right-click → Copy as Markdown table** | Copies what's currently shown, for Jira or Confluence |

Each pivot opens in **its own tab** (Pivot 1, Pivot 2, …), so you can keep several open.

### Good to know

- **Up to 100 rows** per pivot by default. If you select more, a highlighted banner says
  "Showing 100 of N selected rows". Change the limit in [Settings](#settings) (1–500).
- **It's a snapshot.** Re-running the query doesn't change an open pivot — pivot again to
  refresh. It keeps working even after you close the query tab.
- **Values are exactly what the grid shows** — very long text is cut off the same way, and a
  real `NULL` looks the same as the text `NULL`.
- If no ⧉ icons appear, the line under the banner says why (for example, the query couldn't be
  matched to its tables).

---

## Feature 5 — Paste a list as `IN (...)`

You have a list of values in a spreadsheet or an email, and you need them as a SQL `IN` list.

**Where:** in a query window, put the cursor where you want them → **right-click** →
**Paste as SQL IN (...)** (or **Paste as numeric SQL IN (...)** for numbers).
Both are also under the **Tools** menu.

Copy this:

```
aba
baba
dagate
```

Right-click → **Paste as SQL IN (...)**, and you get:

```sql
(
'aba',
'baba',
'dagate'
)
```

The **numeric** variant leaves the quotes off, for ID columns and other numbers:

```sql
(
10,
20,
30
)
```

It handles the awkward bits for you:

- **Duplicates are removed** (the status bar tells you how many).
- **Apostrophes are escaped** — `O'Brien` becomes `'O''Brien'`, which is valid SQL.
- **Accented text gets the `N` prefix** automatically, so Croatian characters survive.
- **Values already in quotes** are not double-quoted.
- **Excel columns work** — tabs and line breaks are both treated as separators.
- If you pick the **numeric** variant and something isn't a number, it **tells you which value**
  and pastes nothing, rather than producing SQL that doesn't run.

---

## Keyboard shortcuts

The extension ships with **no** default shortcuts, so it can't take a key you already use. You
can add your own:

1. **Tools → Options… → Environment → Keyboard**
2. Type `SsmsDataAnalyzer` in *Show commands containing* and pick a command.
3. Leave *Use new shortcut in* on **Global**.
4. Click into *Press shortcut keys*, press the combination you want, then **Assign**.

| Command | Name in the Keyboard list |
|---|---|
| Go to source for this value | `SsmsDataAnalyzer.GoToSourceForValue` — uses the cell selected in the results grid |
| Find… (in query results) | `SsmsDataAnalyzer.FindInResults` |
| Pivot selected rows… | `SsmsDataAnalyzer.PivotRows` |
| Paste as SQL IN (...) | `SsmsDataAnalyzer.PasteAsSqlIn` |
| Paste as numeric SQL IN (...) | `SsmsDataAnalyzer.PasteAsNumericSqlIn` |

---

## Settings

**Where:** **Tools** menu → **Options…** → **SSMS Data Analyzer** (in the list on the left)

You can leave every one of these alone. Changes apply the next time you use the feature — no
restart needed.

| Setting | What it does | Default |
|---|---|---|
| **Automatically execute the generated query** | Whether "Go to source" runs the query for you, or opens it for you to review first | On |
| **Pivot row limit** | Most rows one pivot shows (1–500) | 100 |
| **Query timeout (seconds)** | How long Analyze Data waits before giving up on a slow table — raise it if a big table times out | 120 |
| **Large table threshold (rows)** | Above this size, Analyze Data warns you before starting a long analysis | 10,000,000 |
| **DateCreated candidate columns** | Fallback column names used for **Last Fill** when a table has no `DateCreated` | `CreatedDate, CreatedOn, …` |
| **Enable right-click Analyze Data** | Turn off only if a future SSMS update breaks the Object Explorer menu | On |
| **Distinct batch size**, **Max grant percent**, **MAXDOP** | Advanced — how Analyze Data spreads its work and caps its server memory use | 8, 25, 0 |

---

## Is it safe to run on a production database?

It only ever **reads**. It never writes, updates or deletes anything.

- **Analyze Data** stays out of other users' way: it reads without blocking anyone else's work,
  caps how much server memory it can take, gives up rather than running forever, and warns you
  before starting on a very large table. **Cancel** at any point keeps what it has so far.
- **Go to source** and pivot links only read the table structure to find the link; the query
  they open selects the one linked record (or at most 1,000 rows for **Go to source table**).
- **Find**, **Pivot** and **Paste as SQL IN** work on what's already on your screen or clipboard —
  they don't query the database at all.
- Your password is never stored or written anywhere, and cell values are never written to SSMS's
  log files.

---

## If something doesn't work

- **"Analyze Data…" isn't in the right-click menu** — make sure you right-clicked a *table*
  (Databases → *your database* → Tables). Try right-clicking again; if it still doesn't appear,
  restart SSMS, and check *Enable right-click Analyze Data* is on in [Settings](#settings).
- **The Analyze Data panel shows an error** — it always says what went wrong rather than sitting
  blank. Send that message along when reporting the problem.
- **A large table is slow** — that's the exact counting doing its work. Press **Cancel** to keep
  partial results, or raise the timeout in [Settings](#settings).
- **"Go to source" isn't offered, or nothing happens** — look at the status bar at the bottom of
  SSMS; it says why (see [Feature 3](#feature-3--jump-to-a-linked-record-go-to-source) for the
  cases it deliberately refuses). Sign-ins with Microsoft Entra aren't supported for this yet.
- **A pivot shows fewer rows than you selected** — you hit the pivot row limit; the banner says
  so. Raise it in [Settings](#settings).

---

<details>
<summary><b>For developers</b> — building, testing, design decisions</summary>

### Repository layout

```
src/SsmsDataAnalyzer.Core/   netstandard2.0 — profiling engine, pivot and result-shape logic, zero VS dependencies
src/SsmsDataAnalyzer.Cli/    net8.0 — same engine, scriptable from a terminal
src/SsmsDataAnalyzer.Vsix/   net472 — the SSMS 22 extension
tests/                       xUnit — 132 tests, unit + integration
tools/seed/                  seeded test database + verified ground truth
docs/                        reverse-engineering notes on SSMS's internals, feature plans (pivot-plan.md)
spikes/OeProbe/              metadata/IL inspector used to produce those notes
dist/                        the released .vsix and VERSION.md
```

`Core` has no dependency on Visual Studio or WPF, which is why the engine is testable and the
CLI exists.

### Building

Requires MSBuild from a Visual Studio 2022 or newer install. The VS "extension development"
workload is **not** required — the VSSDK NuGet package supplies the build targets, but they must
be imported explicitly (see `src/SsmsDataAnalyzer.Vsix/README-BUILD.md`).

```
msbuild src/SsmsDataAnalyzer.Vsix/SsmsDataAnalyzer.Vsix.csproj -restore -p:Configuration=Release
```

### Tests

```
dotnet test tests/SsmsDataAnalyzer.Tests/SsmsDataAnalyzer.Tests.csproj
```

Integration tests need a local SQL Server and the seeded database:

```
sqlcmd -S . -E -C -i tools/seed/seed.sql
```

`dotnet test --filter "Speed!=Slow"` skips the one deliberately slow timeout test.

### Design decisions

**Exact distinct counts, always.** `APPROX_COUNT_DISTINCT` is banned from the codebase and a
test enforces it. Approximate cardinality is fine for query planning and useless for deciding
whether a column is a de-facto key.

**One scan for everything else.** Pass 1 computes fill counts, last-fill dates, min/max and
average length for *every* column in a single table scan. Distinct counts get their own pass,
using index-backed queries where an index exists and batching the rest under a capped memory
grant.

**Report, never guess.** Distinct counts are collation-dependent and reported as the database
computes them. A composite foreign key offers a table jump but *not* a value jump, because
filtering on half a composite key returns plausible-but-wrong rows. Results-grid jumps describe
every `GO` batch and require them to agree on the source column before offering a link.

**Nothing is silently partial.** Cancellation keeps completed work. A pass-1 timeout returns the
metadata it has plus a warning rather than discarding the profile. A capped search reports
`10000+`, and a capped pivot says how many rows it left out.

**No secrets or data in logs.** SSMS's own connection object is reused rather than rebuilt from
a password, and nothing that can contain a cell value is written to the ActivityLog.

`CONTRACT.md` holds the frozen interfaces and the amendment history behind each of these
decisions; `PLAN.md` has the roadmap and `docs/pivot-plan.md` the pivot feature's plan and team.

</details>
