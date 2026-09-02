# SSMS Data Analyzer

**Find out which columns in a database table are actually being used — and when each one was last filled in.**

An extension for SQL Server Management Studio 22. You don't need to write any SQL to use it:
right-click a table, and it tells you what's in it.

Useful if you need to answer questions like:

- *Is anyone still filling in this field, or is it dead?*
- *When did we stop using this column?*
- *How many different values does this field actually have?*
- *This ID points at another table — what's the actual record behind it?*

---

## Installing it

**You don't need to build anything.** The ready-to-install file is in this repository.

1. Open the **[`dist`](dist)** folder above → click **`SsmsDataAnalyzer.vsix`** →
   click the **Download** button (or the ⤓ icon) to save it.
2. **Close SSMS** if it's open. *(The installer can't replace files while SSMS is running.)*
3. **Double-click the downloaded file** and click through the installer.
4. **Open SSMS again.**

That's it — you'll find **Analyze Data…** when you right-click a table.

Requires **SSMS 22**. It will not install on SSMS 21 or older, or on Visual Studio.
**Analyze Data works on every SSMS 22 build.** "Find in Results" and "Go to source" on the query-results grid additionally need SSMS **22.9** or newer (they depend on a results-grid API not present in earlier 22.x builds); on an older build those two menu items simply don't appear.

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
To check which version you have: **Help → About** in SSMS, or look at the version noted in
[`dist/VERSION.md`](dist/VERSION.md).

---

## Feature 1 — Analyze a table

**Where:** Object Explorer (the tree on the left) → expand your database → **Tables** →
**right-click any table** → **Analyze Data…**

That's it. It uses the connection you're already signed in with — no passwords to re-enter.

A panel opens and fills in after a few seconds, one row per column:

| Column in the panel | What it tells you |
|---|---|
| **Column** | The field name |
| **Type** | What kind of data it holds (text, number, date…) |
| **Filled** | How many rows actually have a value here |
| **Fill %** | The same as a percentage — **the quickest thing to scan** |
| **Blank** | Rows containing empty text (counted separately from "no value at all") |
| **Distinct** | How many *different* values exist |
| **Last Fill** | **When this column was last filled in** — see below |
| **Min / Max** | The smallest and largest values |
| **Flags** | A plain-English summary — see below |

### How to read it

**Fill %** is the fastest signal. A column at `0.4%` is filled in for 4 rows in every 1,000 —
almost certainly abandoned, or only used for one rare case.

**Last Fill** is the most useful column and the reason this tool exists. It answers
*"when did anyone last put something in this field?"* If a column shows `2019-03-11` and the
table has rows from last week, **people stopped using that field in 2019**. That's the evidence
you need to retire it. (It works by looking at the table's `DateCreated` column — if the table
doesn't have one, this column shows `n/a` and everything else still works.)

**Flags** call out the interesting cases automatically:

| Flag | Meaning |
|---|---|
| `DEAD` | Never filled in. Not once. |
| `SPARSE` | Filled in less than 5% of the time |
| `CONSTANT` | Every row has the *same* value — so it isn't telling you anything |
| `UNIQUE` | Every row has a *different* value — it's an identifier |

### Getting the results out

Buttons at the top of the panel: **Copy as Markdown** and **Copy as CSV**. Paste straight into
a ticket, a document, or Excel.

---

## Feature 2 — Search within the results

**Where:** click anywhere in the Analyze Data panel → press **Ctrl+F**

A search box appears. Type, and matching cells are highlighted. Useful when a table has 150
columns and you're looking for anything named "…Date" or every column flagged `DEAD`.

- **Enter** or **F3** — next match
- **Shift+Enter** or **Shift+F3** — previous match
- **Esc** — close

---

## Feature 3 — Search inside query results

SSMS has no way to search the results of a query. This adds one.

**Where:** run any query → **right-click anywhere in the results grid** → **Find…**

A **Find in Results** panel opens.

1. Type what you're looking for
2. Press **Enter** or click **Find**
3. **Enter** / **F3** for the next match, **Shift+Enter** / **Shift+F3** for the previous

It searches **every row**, not just the ones on screen, and jumps to each match in turn.

> **Note:** Ctrl+F won't open this one — that shortcut belongs to SSMS itself and opens its own
> Find dialog. Use the right-click menu.

---

## Feature 4 — Jump to a linked record ("Go to source")

When a column holds an ID pointing at another table, this opens that other table for you,
already filtered to the matching record.

**Two places you can do it:**

**From query results** — right-click a cell containing an ID → **Go to source for this value**

**From the Analyze Data panel** — right-click a column row → **Go to source table**
(or right-click its **Min**/**Max** cell → **Go to source for this value**)

Either way a new query tab opens, connected and **already run**, showing the record.

The option only appears when the link is unambiguous. If a column doesn't point anywhere, or
points at several tables at once, the option is hidden or greyed out with the reason — it will
never guess and send you to the wrong table.

---

## Settings

**Where:** **Tools** menu → **Options…** → **SSMS Data Analyzer** (in the list on the left)

| Setting | What it does | Default |
|---|---|---|
| **Automatically execute the generated query** | Whether "Go to source" runs the query for you or just opens it for review | On |
| **Query Timeout (seconds)** | How long to wait before giving up on a slow table | 120 |
| **Large Table Threshold** | Above this row count, you get a confirmation prompt before a long analysis starts | 10,000,000 |
| **Distinct Batch Size** | Advanced — how many columns are counted per query | 8 |
| **Max Grant Percent** | Advanced — caps how much server memory an analysis may use, so it can't slow down other people | 25 |

Changes apply to the next analysis. No restart needed.

You can leave every one of these alone. The two worth knowing about are the timeout (raise it if
a big table times out) and the large-table prompt (which stops you accidentally starting a long
job on a huge table).

---

## Is it safe to run on a production database?

It only ever **reads**. It never writes, updates or deletes anything.

It also stays deliberately out of the way of other users: it reads without blocking anyone
else's work, caps how much server memory it can take, gives up rather than running forever, and
warns you before starting on a very large table. You can press **Cancel** at any point and keep
whatever it worked out so far.

---

## If something doesn't work

- **"Analyze Data…" isn't in the right-click menu** — make sure you right-clicked a *table*
  (under Databases → *your database* → Tables). Try right-clicking a second time; if it still
  doesn't appear, restart SSMS.
- **The panel is empty** — it will show a message explaining what went wrong rather than sitting
  blank. Send that message along when reporting the problem.
- **A large table is slow** — that's the exact-counting doing its work. Press **Cancel** to keep
  partial results, or raise the timeout in Settings.

---

<details>
<summary><b>For developers</b> — building, testing, design decisions</summary>

### Repository layout

```
src/SsmsDataAnalyzer.Core/   netstandard2.0 — profiling engine, zero VS dependencies
src/SsmsDataAnalyzer.Cli/    net8.0 — same engine, scriptable from a terminal
src/SsmsDataAnalyzer.Vsix/   net472 — the SSMS 22 extension
tests/                       xUnit — 75 tests, unit + integration
tools/seed/                  seeded test database + verified ground truth
docs/                        reverse-engineering notes on SSMS's internals
spikes/OeProbe/              metadata inspector used to produce those notes
```

`Core` has no dependency on Visual Studio or WPF, which is why the engine is testable and the
CLI exists.

### Building

Requires MSBuild from a VS 2022+ install. The VS "extension development" workload is **not**
required — the VSSDK NuGet package supplies the build targets, but they must be imported
explicitly (see `src/SsmsDataAnalyzer.Vsix/README-BUILD.md`).

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
filtering on half a composite key returns plausible-but-wrong rows. Sampled data never produces
a distinct count.

**Nothing is silently partial.** Cancellation keeps completed work. A pass-1 timeout returns the
metadata it has plus a warning rather than discarding the profile. A capped search reports
`10000+` rather than truncating quietly.

`CONTRACT.md` holds the frozen interfaces and the amendment history behind each of these
decisions; `PLAN.md` has the roadmap.

</details>
