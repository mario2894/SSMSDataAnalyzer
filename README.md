# SSMS Data Analyzer

A table **data profiler** for SQL Server Management Studio 22.

Right-click any table in Object Explorer → **Analyze Data** → get a per-column report of what is
actually being used: fill rates, exact distinct counts, and — the headline metric — **when each
column was last populated**.

![status](https://img.shields.io/badge/SSMS-22%20only-blue) ![status](https://img.shields.io/badge/tests-75%20passing-brightgreen)

---

## What it does

### Table profiling
One row per column, with:

| Metric | Meaning |
|---|---|
| **Filled / Fill %** | non-NULL count — is anything still writing here? |
| **Blank** | `''` and whitespace-only, counted separately from NULL |
| **Distinct** | **exact** `COUNT(DISTINCT)` — never approximated |
| **Last fill** | `MAX(DateCreated)` over rows where *this* column is non-NULL |
| **Min / Max / Avg len** | value range and average size |
| **Collation** | why a distinct count is what it is |
| **Flags** | `DEAD` (never filled), `CONSTANT`, `UNIQUE`, `SPARSE` (<5%) |

*Last fill* is the reason this tool exists: it distinguishes "this column is empty" from
"this column stopped being written on 2024-01-10", which is the evidence you need to retire one.

### Find in results
Right-click any SSMS query results grid → **Find…**. SSMS has no built-in find for query
results; this searches every row (not just the rendered ones), highlights matches, and steps
through them.

### Go to source
On a foreign-key column, jump to the referenced table — from the profiler grid *or* from any
SSMS results grid cell, filtered by that cell's actual value. Opens a connected query window
using the connection you were already working in.

---

## Design decisions worth knowing

**Exact distinct counts, always.** `APPROX_COUNT_DISTINCT` is banned from the codebase and a
test enforces it. Approximate cardinality is fine for query planning and useless for deciding
whether a column is a de-facto key.

**One scan for everything else.** Pass 1 computes fill counts, last-fill dates, min/max and
average length for *every* column in a single table scan. Distinct counts get their own pass,
using index-backed queries where an index exists and batching the rest with a capped memory
grant so a profile can't starve a production workload.

**Report, never guess.** Distinct counts are collation-dependent and reported as the database
computes them. A composite foreign key offers a table jump but *not* a value jump, because
filtering on half a composite key returns plausible-but-wrong rows. Sampled data never produces
a distinct count. Where the tool can't be certain, it declines and says why.

**Nothing is silently partial.** Cancellation keeps completed work. A pass-1 timeout returns
the metadata it has plus a warning rather than discarding the profile. A capped search reports
`10000+` rather than truncating quietly.

---

## Repository layout

```
src/SsmsDataAnalyzer.Core/   netstandard2.0 — profiling engine, zero VS dependencies
src/SsmsDataAnalyzer.Cli/    net8.0 — same engine, scriptable
src/SsmsDataAnalyzer.Vsix/   net472 — the SSMS 22 extension
tests/                       xUnit — 75 tests, unit + integration
tools/seed/                  seeded test database + verified ground truth
docs/                        reverse-engineering notes on SSMS's internals
spikes/OeProbe/              metadata inspector used to produce those notes
```

`Core` deliberately has no dependency on Visual Studio or WPF, which is why the engine is
testable and the CLI exists.

## Building

Requires MSBuild from a VS 2022+ install. The VS "extension development" workload is **not**
required — the VSSDK NuGet package supplies the build targets, but they must be imported
explicitly (see `src/SsmsDataAnalyzer.Vsix/README-BUILD.md`).

```
msbuild src/SsmsDataAnalyzer.Vsix/SsmsDataAnalyzer.Vsix.csproj -restore -p:Configuration=Release
```

Install the resulting `.vsix` with SSMS 22's own `VSIXInstaller.exe`.

## Tests

```
dotnet test tests/SsmsDataAnalyzer.Tests/SsmsDataAnalyzer.Tests.csproj
```

Integration tests need a local SQL Server and the seeded database:

```
sqlcmd -S . -E -C -i tools/seed/seed.sql
```

`dotnet test --filter "Speed!=Slow"` skips the one deliberately slow timeout test.

---

## Status

Working in SSMS 22: profiling, grid search, FK navigation from both surfaces, auto-executed
queries on the inherited connection. See `PLAN.md` for the roadmap and `CONTRACT.md` for the
frozen interfaces and the amendment history behind each design decision.
