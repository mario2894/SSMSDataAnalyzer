# CONTRACT — frozen interfaces (owned by team lead, do NOT edit)

Every agent codes against these exact shapes. If you believe a change is required,
**stop and report it to the lead** — do not unilaterally alter this file. Divergence here is
the one thing that will make four parallel workstreams fail to integrate.

Namespace root: `SsmsDataAnalyzer`. Target: **SSMS 22 only**. Distinct counts: **exact only**
(`COUNT(DISTINCT …)`); `APPROX_COUNT_DISTINCT` is banned from this codebase.

## Solution layout — directory ownership is exclusive

| Path | Owner |
|---|---|
| `src/SsmsDataAnalyzer.Core/` | Agent A (core-engine) |
| `src/SsmsDataAnalyzer.Cli/` | Agent A (core-engine) |
| `src/SsmsDataAnalyzer.Vsix/` | Agent B (vsix-shell) |
| `spikes/OeProbe/`, `docs/oe-api.md` | Agent C (oe-spike) |
| `tests/` , `tools/seed/` | Agent D (test-data) |
| `SsmsDataAnalyzer.sln`, `PLAN.md`, `CONTRACT.md`, `Directory.Build.props` | Lead only |

**Never create or edit a file outside your own directory.** Need something from another
area? Report it; the lead will route it.

## Target frameworks

- `SsmsDataAnalyzer.Core` → `netstandard2.0`
- `SsmsDataAnalyzer.Cli` → `net8.0`
- `SsmsDataAnalyzer.Vsix` → `net472`
- `SsmsDataAnalyzer.Tests` → `net8.0`

Core's only NuGet dependency is `Microsoft.Data.SqlClient`. Core must not reference
anything from Visual Studio, WPF, or `System.Windows.*`.

## Model types (Core, namespace `SsmsDataAnalyzer.Core.Model`)

```csharp
public sealed class TableRef {
    public string Server { get; set; }
    public string Database { get; set; }
    public string Schema { get; set; }      // e.g. "dbo"
    public string Name { get; set; }        // e.g. "Orders"
    public string QualifiedName { get; }    // "[dbo].[Orders]" — bracket-doubled
}

public enum AggregateSupport { Full, NoMinMax, NoDistinct, MetadataOnly }

public sealed class ColumnMeta {
    public string Name { get; set; }
    public int ColumnId { get; set; }
    public string TypeName { get; set; }        // sys.types.name
    public int MaxLength { get; set; }          // sys.columns.max_length, -1 = MAX
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsComputed { get; set; }
    public string LeadingIndexName { get; set; } // index where key_ordinal = 1, else null
    public string Collation { get; set; }        // AMENDMENT 12 — sys.columns.collation_name
    public bool IsForeignKey { get; set; }       // AMENDMENT 14
    public int ForeignKeyCount { get; set; }     // AMENDMENT 15 — 0, 1, or n
    public string ReferencedSchema { get; set; } // AMENDMENT 15 — set for single-column AND composite
    public string ReferencedTable { get; set; }  // AMENDMENT 15 — null only when ForeignKeyCount > 1
    public string ReferencedColumn { get; set; } // AMENDMENT 15 — null for composite FKs
    public string ForeignKeyName { get; set; }   // AMENDMENT 15 — set whenever ForeignKeyCount == 1
    public bool IsStringType { get; }
    public bool IsLob { get; }                   // MAX types / text / ntext / image / xml
    public AggregateSupport Support { get; }
}

public enum ColumnFlag { None = 0, Dead = 1, Constant = 2, Unique = 4, Sparse = 8 }

public sealed class ColumnProfile {
    public ColumnMeta Meta { get; set; }
    public long TotalRowsContext { get; set; }  // AMENDMENT 1 — see below
    public long? FilledCount { get; set; }      // COUNT_BIG(col)
    public long? BlankCount { get; set; }       // '' or all-whitespace, string cols only
    public long? DistinctCount { get; set; }    // EXACT. null = not yet computed / skipped
    public DateTime? LastFillDate { get; set; } // MAX(DateCreated) WHERE col IS NOT NULL
    public object MinValue { get; set; }
    public object MaxValue { get; set; }
    public double? AvgByteLength { get; set; }
    public string SkipReason { get; set; }      // non-null => aggregates deliberately skipped
    public ColumnFlag Flags { get; }            // derived from TotalRows + the above
}

public sealed class TableProfile {
    public TableRef Table { get; set; }
    public long TotalRows { get; set; }
    public long EstimatedRows { get; set; }     // from sys.dm_db_partition_stats (pass 0)
    public string DateCreatedColumn { get; set; } // resolved name, or null
    public bool WasSampled { get; set; }
    public TimeSpan Elapsed { get; set; }
    public IList<ColumnProfile> Columns { get; set; }
    public IList<string> Warnings { get; set; }
}

public sealed class ProfileOptions {
    public bool IncludeDistinct { get; set; } = true;
    public int DistinctBatchSize { get; set; } = 8;      // columns per batched distinct query
    public int MaxGrantPercent { get; set; } = 25;       // OPTION (MAX_GRANT_PERCENT = n)
    public int QueryTimeoutSeconds { get; set; } = 120;
    public int? MaxDop { get; set; }
    public double? SamplePercent { get; set; }           // null = no sampling
    public long LargeTableThreshold { get; set; } = 10_000_000;
    public ISet<string> IncludedColumns { get; set; }    // null = all columns
    public IList<string> DateCreatedCandidates { get; set; }
        // default order: DateCreated, CreatedDate, CreatedOn, Created,
        //                InsertDate, DateInserted, RowCreatedAt, ModifiedDate
}
```

`SamplePercent != null` **must** force `DistinctCount = null` with
`SkipReason = "Distinct counts are not computed on sampled data"`. Sampled cardinality is not
extrapolatable and must never be presented as a distinct count.

## Service interfaces (namespace `SsmsDataAnalyzer.Core`)

```csharp
public sealed class ProfileProgress {
    public string Stage { get; set; }        // "metadata" | "pass1" | "distinct"
    public int CompletedUnits { get; set; }
    public int TotalUnits { get; set; }
    public string CurrentDetail { get; set; } // e.g. "columns 9-16"
    public TableProfile Snapshot { get; set; } // partial result, safe to bind to UI
}

public interface ITableProfiler {
    Task<TableProfile> ProfileAsync(
        string connectionString,
        TableRef table,
        ProfileOptions options,
        IProgress<ProfileProgress> progress,
        CancellationToken cancellationToken);
}
```

`ProfileAsync` must report a `ProfileProgress` with a usable `Snapshot` **after pass 1
completes**, then again after **each** distinct batch. Cancellation returns the partial
profile built so far — it never throws away completed work and never throws
`OperationCanceledException` past the caller boundary with results discarded.

## Non-negotiable safety rules (apply to all generated SQL)

1. `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` precedes every profiling query.
2. Identifiers are escaped by bracket-doubling (`]` → `]]`). No user value is ever
   concatenated into SQL text as a literal; schema/table go through parameters wherever the
   syntax allows.
3. Every command gets `CommandTimeout = ProfileOptions.QueryTimeoutSeconds` and honours the
   `CancellationToken` via `SqlCommand.Cancel()`.
4. Batched distinct queries carry `OPTION (MAX_GRANT_PERCENT = n)`.
5. Pass 1 select-lists are chunked at ~60 columns to stay clear of expression limits.
6. `APPROX_COUNT_DISTINCT` must not appear anywhere in the repository.

## Test target

A live default instance **`MSSQLSERVER`** is running on this machine (connect via
`Server=.;Integrated Security=true;TrustServerCertificate=true`). LocalDB (`MSSQLLocalDB`)
and `sqlcmd` are also available. Agent D owns the seeded database `SsmsDataAnalyzerTest`.
No other agent may create or drop databases.

---

# AMENDMENTS (lead-issued, binding)

Numbered so agents can confirm which revision they built against. The base contract above is
otherwise unchanged; every amendment here is **additive** and breaks no existing compile.

## Amendment 1 — `ColumnProfile.TotalRowsContext` is public

Raised by Agent A: `Flags` (Dead / Sparse / Unique) cannot be derived from `ColumnProfile`'s
members alone, because the rules need the table's row count, which lives on `TableProfile`.
A implemented it as an `internal` field, correctly refusing to alter the frozen public shape.

**Decision: promote it to a public settable property**, as shown in the type above. Rationale:
an `internal` value silently makes hand-constructed `ColumnProfile` instances report
`Flags = None`, which turns legitimate unit tests of flag derivation into false passes. A flag
rule that can only be exercised through the full profiler is a rule that will not stay correct.
Additive, so no existing code breaks.

## Amendment 2 — flag derivation requires a non-empty table

Raised by Agent D from the seeded `EmptyTable` (0 rows): every column trivially has
`FilledCount == 0`, so every column in an empty table satisfies the `Dead` rule and the grid
lights up entirely red.

**Decision: when `TotalRowsContext == 0`, every column's `Flags` is `ColumnFlag.None`**, and
`TableProfile.Warnings` gets one entry: `"Table is empty — per-column flags are not meaningful."`

Rationale: `Dead` must mean *"this column was never populated even though the table holds
data"* — a real finding about a real column. In an empty table it degenerates into a restatement
of the table being empty, which the row count already says. The same reasoning applies to
`Sparse`, `Constant` and `Unique`: none carry information at zero rows.

## Amendment 3 — numeric literals in generated SQL are permitted where T-SQL rejects parameters

Raised by Agent A: `TABLESAMPLE SYSTEM (@p PERCENT)` fails at runtime with error 497,
"Variables are not allowed in the TABLESAMPLE or REPEATABLE clauses". `MAX_GRANT_PERCENT` and
`MAXDOP` reject parameters for the same reason — query hints are parsed before parameter binding.

**Decision: approved as implemented.** Safety rule 2 already carried the "wherever the syntax
allows" carve-out, and this is precisely the case it anticipated. Conditions, all already met by
A's `SqlIdentifier.Percent`: the value is range-checked before emission, formatted with the
invariant culture, and can only ever produce digits and at most one decimal point. It must
remain impossible for a caller-supplied value to reach SQL text through any other path.

## Amendment 4 — `AggregateSupport.NoDistinct` implies `NoMinMax`

Raised by Agent A: `xml`, `geography` and `geometry` reject `MIN`/`MAX` *and* `DISTINCT`, but
accept `COUNT_BIG` and `DATALENGTH` — two independent conditions competing for one enum slot.

**Decision: approved.** `NoDistinct` is defined as the strictly stronger state, implying
`NoMinMax`. A must keep this documented on the enum itself, not only here. Binding type mapping,
which A verified empirically against the live instance rather than assuming:

| State | Types |
|---|---|
| `MetadataOnly` | `text`, `ntext`, `image` (reject even `COUNT_BIG`), unknown CLR UDTs |
| `NoDistinct` (implies `NoMinMax`) | `xml`, `geography`, `geometry` |
| `NoMinMax` | `bit` |
| `Full` | everything else, **including** `varchar(max)` / `nvarchar(max)` / `varbinary(max)` |

The last row is load-bearing: MAX types support all five aggregates, which is what makes the
DistinctPlanner's 2c "LOB batches of one" path meaningful rather than dead code.

## Amendment 5 — VSIX packaging requires an explicit targets import

Established by the lead directly, after Agent B's build produced no `.cto`, `.pkgdef` or `.vsix`
despite a clean restore.

`Microsoft.VSSDK.BuildTools`'s auto-imported `build/Microsoft.VSSDK.BuildTools.targets` is
**18 lines that only set environment variables** (`VsSDKToolsPath` and friends). It contains no
packaging logic. The real targets ship in the same package at
`tools/vssdk/Microsoft.VsSDK.targets` and are **not** imported automatically — a legacy VSIX
project reaches them through `$(VSToolsPath)\VSSDK\Microsoft.VsSDK.targets`, which resolves into
the VS install and therefore requires the workload.

**Decision:** the VSIX project imports the NuGet-provided targets explicitly:

```xml
<PropertyGroup>
  <VsSDKTargetsPath>$(NuGetPackageRoot)microsoft.vssdk.buildtools/17.9.3168/tools/vssdk/Microsoft.VsSDK.targets</VsSDKTargetsPath>
</PropertyGroup>
<Import Project="$(VsSDKTargetsPath)" Condition="Exists('$(VsSDKTargetsPath)')" />
```

Verified by lead spike: with this import, **`VSCommandTable.cto` is produced** from B's actual
`.vsct` — VSCT compilation runs and succeeds with no VS workload installed. Confirmed on this
machine that no `VSSDK` folder and no `Microsoft.VsSDK.targets` exist under either VS 2022
install, so the NuGet copy is genuinely the only one in play.

**Still open, assigned to Agent B:** the build then fails `VSSDK1207` trying to write
`C:\resources.json` — an output-path property resolving empty, not a missing tool. Pkgdef
generation and `.vsix` container creation remain unproven past that point.

## Amendment 6 — Tier A Object Explorer integration is approved, with a changed mechanism

Agent C's spike (`docs/oe-api.md`) resolved the project's highest-risk unknown. **Tier A is
feasible on SSMS 22 and requires no private reflection.** Independently re-verified by the lead
with C's own probe tool against the shipped binaries:

- `HierarchyObject` is `public`, exposing `public void AddChild(string, object)` — the injection
  point — so a plain virtual call reaches `DefaultMenuHandler.AddChild`. `DefaultMenuHandler`
  being internal does not matter.
- `IMenuHandler`, `IWinformsMenuHandler`, `INodeInformation`, `IObjectExplorerService` are all
  `public` interfaces in `SqlWorkbench.Interfaces.dll` (v22.200.0.0).
- **Red Gate SQL Prompt 11 does exactly this in this SSMS 22 install today.** Confirmed by
  direct metadata inspection of
  `C:\Program Files (x86)\Red Gate\SQL Prompt 11\RedGate.SqlPrompt.ShellAbstraction.22.dll`:
  `internal RedGate.SqlPrompt.Shell.MenuManagerManager` implements
  `…ObjectExplorer.IMenuHandler` and `…ObjectExplorer.IWinformsMenuHandler`, with
  `public ToolStripItem[] GetMenuItems()`. A shipping commercial extension is the strongest
  possible evidence that this path works and keeps working.

**Decisions:**

1. **Use compile-time references, not late-bound reflection.** PLAN.md's original reflection
   design was insurance against an API that might not exist; it does exist and is public.
   Reflection now buys nothing and triples the size of `OeContextBridge.cs`. A try/catch plus
   the feature flag preserves identical degrade-to-Tier-B behaviour.
2. **`.vsct` is removed from the Tier A design.** The OE node context menu is a WinForms
   `ContextMenuStrip` built from `IWinformsMenuHandler.GetMenuItems()`; the legacy `CommandID`
   ctmenu branch is dead code for SQL nodes. A `.vsct` group parented there would silently never
   appear — a bug that would have cost days to diagnose. `.vsct` remains correct for Tier B/C.
3. **Two constraints are binding on whoever implements M3:**
   - `GetMenuItems()` must never throw. It runs inside SSMS's menu construction, so one
     exception destroys the entire node context menu, not just our item. Wrap the body and
     return an empty array on failure.
   - The injection must be de-duped by reference identity. `CurrentContextChanged` fires on
     every selection and `AddChild` appends unconditionally, so an unguarded implementation adds
     one duplicate menu item per click. Red Gate carries exactly this guard.

**Recorded dead end** (so nobody rediscovers it): SSMS 22 has a real `.oexml` extension
mechanism, but `<MenuItem>` only composes SSMS's own internal `ExtensionMenuHandler`, and
`<ActionMenuItem>` — the element that would define a new action — is stubbed to `ldnull; ret`
in 22.9. Vestigial; do not build on it.

## Amendment 7 — the VSIX must not ship its own `Microsoft.Data.SqlClient`

Found by the lead while verifying Agent B's `.vsix`. The package builds clean, installs clean,
and would fail at runtime the first time a user clicked Analyze. Two defects, one fix.

**Defect 1 — the native SNI library is missing from the package.** The build output contains
`Microsoft.Data.SqlClient.SNI.{x64,x86,arm64}.dll`, but **none of them are inside the `.vsix`**.
On .NET Framework, `Microsoft.Data.SqlClient` P/Invokes into that native library to open any
connection. Packaged as-is, every profiling attempt throws at `SqlConnection.Open()`.

**Defect 2 — and the reason defect 1 must not be fixed by simply adding the natives.**
SSMS 22 already ships `Microsoft.Data.SqlClient.dll` **and** all three SNI natives in its IDE
root, at a *different major version*:

| Copy | FileVersion | ProductVersion |
|---|---|---|
| SSMS 22 host | `6.15.26114.3` | **6.1.5** |
| Our VSIX | `5.22.24240.06` | **5.2.2** |

Loading a second, older copy of the same assembly into a process that already has 6.1.5 loaded
is precisely the type-identity/binding hazard that `<Private>false</Private>` exists to prevent
— we applied that discipline to every SSMS assembly and then let the data-access stack in
through the transitive `ProjectReference` to Core.

**Decision:** the VSIX **binds to the host's `Microsoft.Data.SqlClient`** and ships neither the
managed assembly nor the SNI natives, exactly as it already does for `sqlmgmt.dll` and friends.
This resolves both defects at once: no duplicate assembly, and the host's SNI natives are
already present and version-matched.

Required work:

- **Agent B** — exclude `Microsoft.Data.SqlClient` and its transitive tree (`Azure.*`,
  `Microsoft.Identity*`, `Microsoft.IdentityModel.*`, `System.IdentityModel.*`,
  `System.ClientModel`, `System.Text.Json`, and the SNI natives) from the VSIX container; add a
  binding redirect / `codeBase` so Core's 5.2.2-compiled references resolve to the host's 6.1.5;
  re-verify by unzipping that none of them remain. Keep a guard so a future dependency cannot
  silently re-enter the package.
- **Agent A** — Core is compiled against 5.2.2 but will execute against 6.1.5 inside SSMS.
  Verify the API surface Core actually uses is compatible across that major-version jump, and
  report whether Core should move to 6.x so the CLI and the VSIX exercise the same client. Note
  the CLI (net8, its own process) is unaffected and may keep its own copy either way.

**General rule this establishes:** anything already loaded by the SSMS host is referenced, never
shipped. Before release, audit the full `.vsix` payload against the IDE folder for any other
assembly we are duplicating.

## Amendment 8 — a pass-1 timeout must not discard the profile

Raised by Agent A while mapping exception paths for the 6.x audit. Pre-existing, unrelated to
the version jump.

`RunDistinctAsync` already catches `SqlException` when the token is *not* cancelled, records the
failure per-column and as a warning, and continues the plan — one bad batch never abandons the
rest. **Pass 1 has no equivalent.** A `CommandTimeout` expiry during pass 1 propagates out of
`ProfileAsync` and throws the entire profile away.

A timeout is correctly *not* treated as cancellation, so it legitimately bypasses the
partial-results path. But the outcome is backwards: on a large table — exactly the case where a
timeout is likely — a slow pass 1 yields **nothing**, when it could yield the full column
metadata, the row count, and a clear warning explaining what timed out.

**Decision: pass 1 adopts the same contract as the distinct pass.** On `SqlException` where the
cancellation token is not signalled, record the failure, add a warning naming the affected
column chunk and the timeout value, and return the profile built so far rather than throwing.
The metadata from pass 0 is already in hand and is genuinely useful on its own — column list,
types, nullability, PK, index information and the resolved `DateCreated` column all survive.

This follows directly from the base contract's stated principle that the profiler "never throws
away completed work". That principle was written about cancellation; it applies with equal force
to a timeout, and the inconsistency between pass 1 and the distinct pass was an oversight rather
than a design decision.

Assigned to Agent A. Agent D adds coverage: a pass-1 timeout must return a profile with
populated metadata plus the warning, not raise.

## Amendment 9 — Core is pinned to Microsoft.Data.SqlClient 6.1.5

Consequence of Amendment 7, implemented and verified by Agent A.

`Microsoft.Data.SqlClient` **6.1.5 still ships `netstandard2.0`** in both `lib/` and `ref/`, so
Core keeps its TFM — the gating constraint does not bite. Core is pinned to **6.1.5**, the exact
version SSMS 22 hosts, so the CLI and the extension exercise the same client and "it works in
the CLI" remains evidence about the extension.

Audit result: all 32 MDS members Core touches resolve identically in 5.2.2, in 6.1.5, and in the
binary SSMS actually ships. None obsolete; the only delta is an additive `OpenAsync` overload.
The SSMS-shipped assembly's ProductVersion carries the **same git commit hash** as the public
package — SSMS ships the stock build, not a patched fork. Connection-string and TLS defaults are
identical across both versions (`Encrypt=True`, `TrustServerCertificate=False`); the `Encrypt`
false→true move happened at 4.0, before both. The CLI now states `Encrypt` explicitly regardless,
so a future default shift cannot move behaviour silently.

Cancellation semantics verified behaviourally, not just by metadata: a cancelled command
surfaces as **`SqlException` Number=0, Class=11 — never `OperationCanceledException`** — in both
versions. The `catch (SqlException) when (token.IsCancellationRequested)` branch is the one that
actually fires.

**Follow-on for Amendment 7:** MDS keeps AssemblyVersion at `major.0.0.0`, so Core now compiles
against `6.0.0.0` and the host provides `6.0.0.0`. **The binding redirect is no longer required**
— the reference resolves directly. The exclusion of the assembly and its transitive tree from
the VSIX payload still stands in full. Only a hypothetical 7.x host would reintroduce skew.

## Amendment 10 — pass 1 halts on timeout, continues on other errors

Ruling on the question Agent A raised when implementing Amendment 8: after a pass-1 chunk
fails, should pass 1 continue to the next chunk or stop?

A implemented "continue", matching the distinct pass, and correctly flagged the cost: on a wide
table where every chunk times out, the user waits `chunks × timeout`. At the default 120 s that
is 6 minutes for a 162-column table and over half an hour for a 1,000-column one. Bounded and
interruptible, but a poor first experience.

"Always stop" is also wrong, and A's own test data shows why: a computed column dividing by zero
(SQL 8134) is a **column-specific** failure. Later chunks are genuinely likely to succeed, and
abandoning them would discard results we could have had.

**Decision: distinguish by failure kind.**

- **Timeout (SQL `-2`): stop pass 1 immediately.** A timeout is a statement about the *table* —
  its size, its width, the server's current load — not about the columns in that particular
  chunk. Whatever made chunk 1 exceed the budget applies to chunk 2. Continuing pays the full
  timeout again to learn the same fact. Record the chunks that were never attempted in the
  warning, so the user can see the profile is partial by choice rather than by accident, and can
  re-run with a longer `QueryTimeoutSeconds` if they want the rest.
- **Any other `SqlException`: continue to the next chunk**, exactly as implemented. These are
  column-specific and later chunks carry different columns.

The distinct pass keeps its current per-batch continue-on-failure behaviour unchanged. Its
batches are independently costed — 2a index-backed queries are narrow index scans that routinely
succeed where a full-table pass 1 cannot — so the reasoning above does not transfer to it.

Everything else in A's implementation stands as built, in particular: permission errors (229/230)
excluded from the catch so "SELECT denied" surfaces properly from either pass; the empty-table
guard preventing a failed chunk from declaring a slow table empty; `RestorePass1FailureReasons`
so a column can show a real distinct count *and* its pass-1 note; and the `TotalRows == 0`
disambiguation warning.

## Amendment 11 — generated SQL must be collation-safe

Found by the lead, accidentally: a routine verification query against the `Test` database failed
with

```
Msg 451: Cannot resolve collation conflict between "Latin1_General_CI_AS_KS_WS"
and "Croatian_CI_AS" in add operator occurring in SELECT statement column 1.
```

**This machine's server collation is `Croatian_CI_AS`, not the `Latin1_General_*` that most
examples assume**, and both `SsmsDataAnalyzerTest` and `Test` inherit it. Every result verified
on this project so far was produced under a non-default collation. That is a useful accident: it
means the numbers are not silently dependent on the most common configuration. But it also
proves the tool will meet collation variety in the wild, and we have never tested for it.

Two distinct concerns:

**1. Collation conflicts in generated SQL.** Our pass-1 blank-count compares a column against a
literal (`LTRIM(RTRIM([col])) = ''`). Collation *precedence* means an explicit column collation
wins over a literal's, so that specific comparison should be safe even when a column carries a
collation differing from its database default. Conflicts arise when two differently-collated
*columns* are combined, which the current SQL does not do. **This reasoning must be verified
against a real column with an explicit non-default collation, not accepted as argument.** A
single `Msg 451` from generated SQL would fail an entire profile.

**2. Distinct counts are collation-dependent, and that is correct behaviour.** Agent D already
documented the case: `''` and `'   '` collapse into one `DISTINCT` group because trailing spaces
are insignificant under these collations, giving 251 rather than the naive 252. Under a binary
(`_BIN2`) collation the same data yields a different, equally correct answer.

**Decision: this is reported, never "corrected".** The profiler's contract is to report what
`COUNT(DISTINCT …)` actually returns for that column under that column's collation — that is the
number that governs the user's real queries, indexes and constraints. Normalising it would make
the tool disagree with the database it is describing. It must, however, be *discoverable*: the
tool should surface the collation alongside distinct counts so a surprising number is
explicable rather than mysterious.

Assigned: **Agent A** — verify concern 1 empirically against columns carrying explicit
non-default collations (including a `_BIN2` column and one differing from its database default),
and surface collation in the profile output. **Agent D** — seed such columns and add coverage,
including a case proving distinct counts differ under `_BIN2` versus the database default.

### Amendment 11 — CORRECTION (lead error)

Amendment 11 as written asserted that the trailing-space case (`''` vs `'   '`) "under a binary
(`_BIN2`) collation ... yields a different, equally correct answer." **That claim is wrong, and
it was mine.** Agent D tested the hypothesis directly instead of building a seed around it, found
it false, and reported the discrepancy rather than fabricating data to match the contract.

Verified independently by the lead on this instance:

| Case | DB default (`Croatian_CI_AS`) | `Latin1_General_BIN2` |
|---|---|---|
| `''` vs `'   '` (trailing space) | 1 group | **1 group — no divergence** |
| `'x'` vs `'X'` (case) | 1 group | **2 groups — diverges** |

Trailing-space insignificance is **ANSI-padding behaviour at the engine level, not collation
semantics**, so a binary collation does not change it. Case sensitivity *is* collation-governed
and does change.

The amendment's substance is unaffected: distinct counts remain collation-dependent, we still
report rather than normalise them, and collation must still be surfaced so a surprising number is
explicable. Only the illustrating mechanism was wrong. D re-based the seeded coverage on
case-sensitivity — a mechanism verified to actually work — and documented the correction in
`expected.md`.

Recorded at length because the failure mode matters more than the fact: a plausible-sounding
claim from the lead went into a binding contract without being tested, and would have produced a
seeded test asserting a divergence that does not exist. **Test the hypothesis before it becomes a
requirement.** D was right to push back.

## Amendment 12 — `ColumnMeta.Collation`

Requested by Agent A while implementing Amendment 11; added to the frozen type above. Additive,
breaks nothing. Sourced from `sys.columns.collation_name`, null for non-character types.

A deliberately did **not** also add `TableProfile.DatabaseCollation`: the per-column *effective*
collation is what actually governs each distinct count, so it answers the question on its own.
One addition beats two.

Surfaced in all three renderers. The CLI inserts the COLLATION column after TYPE **only when some
column in the table has one**, so all-numeric tables keep their previous layout unchanged.
Amendment 11's requirement — that a surprising distinct count be explicable — is now met by the
output itself:

```
COLUMN            TYPE          COLLATION                   FILLED  BLANK  DISTINCT
ColStringBlank    nvarchar(50)  Croatian_CI_AS                 750    500       251
ColCaseDbDefault  nvarchar(50)  Croatian_CI_AS                 750      0       251
ColCaseLatin1CI   nvarchar(50)  Latin1_General_CI_AS_KS_WS     750      0       251
ColCaseBin2       nvarchar(50)  Latin1_General_BIN2            750      0       252
```

Identical data in the last three; the divergence is visible and attributable at a glance.

### Collation safety — closed, with the method that closed it

Agent A verified all four generated statement shapes (pass 0, pass 1, 2a index-backed, 2b
batched, 2c LOB) against a probe spanning five collations, including a non-Unicode `varchar`
column and a nonclustered index on a `_BIN2` column. All clean.

The methodology is why this counts as closed rather than merely untriggered. **"No error" proves
nothing unless the test could have produced one**, so A ran a negative control — two
differently-collated columns in a comparison — and got the failure on demand:

```
Msg 468: Cannot resolve the collation conflict between "Latin1_General_BIN2"
and "Latin1_General_CI_AS" in the equal to operation.
```

The check detects conflicts; our SQL simply never creates one.

**No fix was applied, and adding one would have been wrong.** Forcing `COLLATE DATABASE_DEFAULT`
onto the literal side would override each column's own collation and make our blank counts
disagree with the user's real queries — the exact failure Amendment 11 forbids.

Confirmed structurally as well as by sampling: scanning every generated statement for two
bracketed identifiers around a comparison operator returns nothing. The only two-column
expression we emit is the last-fill `MAX(CASE WHEN [col] IS NOT NULL THEN [DateCreated] END)`,
and `[DateCreated]` is restricted to date/datetime/datetime2/datetimeoffset/smalldatetime — types
that carry no collation at all.

**Load-bearing consequence, flagged for the future:** that type restriction was added for an
unrelated reason and is now quietly what keeps the last-fill expression collation-safe. Anyone
relaxing it to permit a string-typed `DateCreated` column reintroduces the two-column comparison
this section rules out.

### Independent convergence on the Amendment 11 correction

Agents A and D reached the trailing-space correction **separately, by different routes** — D by
testing the hypothesis before seeding it, A by probing five collations while verifying safety.
Both produced the same table of results, and the lead reproduced it a third time. A claim that
survives three independent checks after failing its first is about as settled as this project
gets.

## Amendment 13 — the tool window must not invent its own connection

**First real-world failure, from the user running the extension inside SSMS 22.** The tool window
loaded correctly — package init, pkgdef registration and the WPF grid all work in the real host —
and then failed at the first connection:

```
Failed: Login failed. The login is from an untrusted domain and cannot be
used with Integrated authentication.
```

Cause, `ProfileViewModel.cs:127` — the connection string is hardcoded:

```csharp
$"Server={Server};Database={Database};Integrated Security=true;TrustServerCertificate=true"
```

Windows authentication is the *only* option the UI can produce. The user's server (`SQLTEST7`)
is on a domain their machine is not trusted by, so integrated auth cannot succeed — they are
connected in SSMS by other means, and the extension discards that entirely.

This is the Tier B design reaching its limit exactly where predicted. Typing a server name into
a textbox cannot inherit *how the user authenticated*, and re-authenticating is both a worse
experience and a credential-handling problem we should not take on.

**Decision, in priority order:**

**1. Inherit SSMS's existing connection — the real fix.** The user has already authenticated to
that server in SSMS. `docs/oe-api.md` (Agent C) documents the route: `INodeInformation.Connection`
yields a `SqlConnectionInfo` carrying `ServerName`, `DatabaseName`, `UserName`, `Password`,
`UseIntegratedSecurity` and `TrustServerCertificate`. Using the host's own connection means no
re-authentication, no credentials handled by us, and correct behaviour for every auth mode SSMS
supports — SQL logins, Entra, MFA — without implementing any of them.

**2. Explicit authentication choice as fallback**, for when the tool window is opened with no
Object Explorer selection: Windows Authentication / SQL Server Authentication (username +
password entered by the user) / Microsoft Entra. Preserves the standalone entry point.

**Credential handling rules, binding:** a password entered in our UI is used to build the
connection string and nothing else. It is never written to disk, never placed in settings or the
options page, never logged, and never included in an error message, warning or exported report.
`TrustServerCertificate=true` must stop being unconditional — it is a real security posture, so
expose it as a checkbox that defaults to off, and inherit the host's value on the Tier A path.

**Consequence for the roadmap:** this promotes the Object Explorer integration from "M3, nice to
have" to the correct primary entry point. It is also what the user asked for originally —
right-click a table, analyze it. The auth failure is the forcing function for building it.

Assigned to Agent B (owns `src/SsmsDataAnalyzer.Vsix/`), working from `docs/oe-api.md` as the
specification, including its two binding constraints: `GetMenuItems()` must never throw, and the
menu injection must be de-duped by reference identity.

## Amendment 14 — foreign-key metadata and "Go to source"

User request: click a foreign-key column in the results grid and jump to the referenced table,
ideally pre-filtered by a real value from that cell.

**Design note that makes this work.** The grid holds one row per *column* — metadata, not data
rows — so "filter by the data in that cell" has no meaning for most cells. But `MinValue` and
`MaxValue` are genuine values drawn from the user's column and are already computed in pass 1.
So the feature splits cleanly:

- Right-click a FK column row → **Go to source table** → new query window,
  `SELECT TOP (1000) * FROM [refSchema].[refTable];`
- Right-click specifically the **Min** or **Max** cell of a FK column → **Go to source for this
  value** → `SELECT * FROM [refSchema].[refTable] WHERE [refColumn] = <that value>;`

The second is the one the user actually asked for, and it is only offerable where a real value
exists. Do not fabricate a filter for cells that hold statistics.

### Core (Agent A) — `ColumnMeta` gains FK information

```csharp
public bool   IsForeignKey       { get; set; }
public string ReferencedSchema   { get; set; }   // null when not a FK
public string ReferencedTable    { get; set; }
public string ReferencedColumn   { get; set; }
public string ForeignKeyName     { get; set; }
```

Read in pass 0 from `sys.foreign_keys` / `sys.foreign_key_columns` joined to `sys.tables` and
`sys.schemas`. **Catalog-only — no extra scan of user data, no measurable cost.** Additive to the
frozen type, so nothing breaks.

Constraints:
- A column may participate in more than one FK, and a FK may be **composite** (multi-column).
  Populate the single-column case, and for composite or multiple FKs set `IsForeignKey = true`
  but leave the referenced fields null rather than guessing which one the user meant — a wrong
  jump is worse than no jump. Report how common this is if you find it awkward.
- Self-referencing FKs are normal and must work.
- Cross-database FKs do not exist in SQL Server; cross-schema ones do and must be handled.

### VSIX (Agent B) — the UI and the generated query

- Grid row context menu: "Go to source table" when the column is a FK with a resolved target.
  Disabled/absent otherwise — never show an action that cannot work.
- On a Min/Max cell of such a column, additionally offer "Go to source for this value".
- Open a **new query window** on the same server and database as the profiled table, pre-filled
  with the generated T-SQL. Do not execute it — the user reviews and runs it. That keeps the tool
  read-only by default and lets them adjust the query first.
- **Literal formatting is a correctness requirement, not a nicety.** Values reaching the WHERE
  clause must be emitted per type: strings single-quoted with `'` doubled and `N` prefixed for
  Unicode, dates in unambiguous ISO-8601 (`yyyy-MM-ddTHH:mm:ss.fff`), `uniqueidentifier` quoted,
  numerics invariant-formatted, `bit` as 0/1. A `NULL` min/max means the action is unavailable.
  Reuse Core's escaping conventions rather than inventing new ones — see `SqlIdentifier`.
- Identifiers go through the existing bracket-doubling. The user's own database has table names
  containing periods, so naive concatenation will produce broken SQL.

**Honest limitation to surface, not hide:** this works only where a FK is actually *declared*.
Many schemas encode relationships by convention alone. If the user's database turns out to have
few declared FKs, say so plainly rather than silently offering nothing — and we can then discuss
a clearly-labelled name-based heuristic as a separate, opt-in feature. Do not ship inference
disguised as fact.

## Amendment 15 — revises Amendment 14: composite FKs keep their table target

Agent A challenged Amendment 14's ambiguity rule and is right. Amendment 14 said composite and
multi-FK columns set `IsForeignKey = true` with all referenced fields NULL. That conflates two
different situations:

- **A column in a composite FK** belongs to exactly ONE constraint, which references exactly ONE
  table. Only the column-level pairing is ambiguous for a value filter. The *table* is not
  ambiguous at all.
- **A column in several FKs** genuinely has no single target.

Nulling everything for both throws away information we already hold, and needlessly disables a
navigation that would be correct.

**Revised rule:**

| Case | `ReferencedSchema`/`Table` | `ReferencedColumn` | `ForeignKeyName` | `ForeignKeyCount` |
|---|---|---|---|---|
| Single-column FK | populated | populated | populated | 1 |
| Composite FK (one constraint) | **populated** | **NULL** | **populated** | 1 |
| Multiple FKs on the column | NULL | NULL | NULL | n > 1 |
| Not a FK | NULL | NULL | NULL | 0 |

Consequence for the UI: **"Go to source table" is offered whenever `ReferencedTable != null`** —
including composite FKs. **"Go to source for this value" requires `ReferencedColumn != null`**
as well. A composite FK therefore gets the table jump and correctly withholds the value jump.

Also adding `public int ForeignKeyCount { get; set; }` to `ColumnMeta` — A already computes it
and currently discards it by collapsing to a bool. It distinguishes "one composite constraint"
from "three separate FKs", which is the difference between a normal schema and a smell.

A's evidence for keeping value-jumps off ambiguous columns stands and is the reason the
distinction matters: a probe column carrying FKs to two different tables **could not accept any
non-NULL value at all** (`Msg 547` on insert), because no value satisfies both constraints. A
filtered jump there is meaningless. And filtering on one half of a composite key returns
*plausible but wrong* rows — worse than no action, because nothing signals the error.

`HasUnresolvedForeignKey` must be redefined accordingly: it means "is a FK but has no navigable
table", i.e. `IsForeignKey && ReferencedTable == null` — which under this revision is true only
for the multi-FK case.

## Amendment 16 — "Go to source" in SSMS's query results grid

User asked for the FK jump in SSMS's **own query results grid**, not only our tool window — the
version where every cell holds a real value. Agent C's spike (`docs/resultsgrid-api.md`) says
**feasible**, with one part that is only partially solvable and must be gated hard.

### Menu placement — feasible, plain `.vsct`, low risk

Opposite of the Object Explorer finding: this menu **is** a real VS ctmenu. From `SQLEditors.dll`,
`DisplaySqlResultsTabControl::WndProc` handles `WM_CONTEXTMENU` and calls
`IVsUIShell.ShowContextMenu` with:

- `GUID_SQLEditorGroup` = `{33F13AC3-80BB-4ECB-85BC-225435603A5E}`
- `IDM_SQLWB_SQLRESGRID_CONTEXT` = `112` (0x0070)

Both read from IL and the literal-constant table, not guessed. A `.vsct` `<Group>` parented there
merges normally.

Ownership of the existing items, binary-searched: Copy / Copy with Headers / Select All / Save
Results As / Print are SSMS built-ins. **Script as INSERT, Copy as IN clause, Open in Excel and
Show Aggregate Results are Red Gate SQL Prompt 11**, registered via `IMenuCommandService.AddCommand`
+ a `Menus.ctmenu` pkgdef entry — direct proof the `.vsct` route works on this machine. SQL
Lizard's submenu instead uses DTE `CommandBars` located by caption heuristics; that is
localisation-fragile and is **not** our approach.

### Cell value and column name — feasible, public API, no reflection

`grid.GridStorage` is the `QEResultSet`, which implements the public `IGridResultSet`
(`GetCellData`, `ColumnNames`, `GetSchemaRow`). Connection and query text come from the public
`SqlScriptEditorControl.Connection` (`UIConnectionInfo`) and `.EditorText`.

**Index trap, verified from IL:** `GetCellData` takes the **grid** column index (0 is the
row-number gutter; it subtracts 1 internally), while `GetSchemaRow` takes the **data** index.
Mixing these silently reads the wrong column.

### Base table/column — PARTIAL, and the gating is the feature

`QESQLBatch::DoBatchExecution` passes `CommandBehavior` **0** (or 16), **never KeyInfo**. Proven
live: `BaseTableName`/`BaseSchemaName` come back empty and `BaseColumnName` is only the alias.
**The grid retains nothing usable and cannot be made to.**

The working route is `sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)` — the trailing **1**
(browse information) is load-bearing; with `0` every `source_*` is NULL and the feature looks
impossible. Verified live: aliases resolve to the true base column, cross-database resolves via
`source_database`, views resolve through to base tables. Expressions, aggregates and UNIONs
correctly return NULL. Browse mode adds `is_hidden = 1` rows which must be filtered, after which
ordinals align 1:1.

**The real fragility is "which text produced this grid."** SSMS runs the selection when there is
one, splits on `GO`, and one tab can hold many grids while the DM describes only the first.

**Binding precondition gate — ALL must hold before the action is offered:**
1. the grid is index 0 in its tab,
2. the tab holds exactly one grid,
3. the describe returned no error rows (error 11525 arrives as rows, not an exception),
4. described column count equals `NumberOfDataColumns`,
5. the described column name equals the grid column name at that ordinal.

Otherwise **decline** — offer nothing, or the action greyed with a reason. Without this gate the
feature is a silent-wrong-table generator, which Amendment 14 already forbids. Never guess a
source table.

### Recorded for later, not now

`Microsoft.SqlServer.Management.UI.VSIntegration.SqlEditor.BrokeredContracts.dll` (22.200.0.0) is
a **public, versioned brokered-service contract** added for Copilot —
`ISqlEditorServiceBrokered.GetCurrentConnectionAsync()` and
`IQueryEditorTabDataServiceBrokered.GetGridResultsSegmentAsync(...)`. It is the only *supported*
surface in this area and is the right long-term home. C read its metadata but did not call it;
treat as unverified until a follow-up spike exercises it.

Two things C explicitly could not verify: Red Gate's exact group placement (their CFCT command
table is compressed), and the brokered services' RPC behaviour.
