# expected.md — verified ground truth for `SsmsDataAnalyzerTest`

Every number below was produced by actually running the equivalent `COUNT_BIG` /
`COUNT(DISTINCT …)` / `MAX(...)` query via `sqlcmd` against the live seeded database
(`tools/seed/seed.sql`), **after** seeding. Nothing here is hand-derived-and-trusted; where a
hand derivation and the query disagreed (see the `ColStringBlank` note below), the query wins
and the derivation note explains why.

Seeded on 2026-08-25 against `Server=.;Integrated Security=true;TrustServerCertificate=true`.
Re-run `sqlcmd -S . -E -C -i tools\seed\seed.sql` to reproduce; the script is drop-and-recreate
and idempotent.

Column type/nullability/identity/PK/leading-index metadata (`dbo.Orders`) was independently
confirmed via `sys.columns` / `sys.index_columns` (see bottom of this file) and matches the
`CREATE TABLE` exactly.

---

## dbo.Orders — 1000 rows

`DateCreated` spans 50 distinct days, 20 rows/day, `DATEADD(DAY, (n-1)/20, '2024-01-01')` for
`n` = 1..1000 (row `OrderId`). Day 1 = 2024-01-01, day 50 = **2024-02-19** (the newest date in
the table).

| Column | Type | Nullable | Filled | Blank | Distinct | LastFillDate | Flags |
|---|---|---|---|---|---|---|---|
| OrderId | int (identity, PK) | N | 1000 | — | 1000 | 2024-02-19 | Unique |
| DateCreated | datetime2 | N | 1000 | — | 50 | 2024-02-19 | — |
| ColFilledAlways | nvarchar(50) | N | 1000 | 0 | 5 | 2024-02-19 | — |
| ColStoppedDay10 | int | Y | 200 | — | 200 | **2024-01-10** | — |
| ColStoppedDay30 | int | Y | 600 | — | 600 | **2024-01-30** | — |
| ColRecentOnly | int | Y | 20 | — | 20 | 2024-02-19 | **Sparse** (2.0% fill) |
| ColDead | int | Y | 0 | — | 0 | **NULL** | **Dead** |
| ColConstant | int | N | 1000 | — | 1 | 2024-02-19 | **Constant** |
| ColUniqueGuid | uniqueidentifier | N | 1000 | — | 1000 | 2024-02-19 | **Unique** |
| ColStringBlank | nvarchar(50) | Y | 750 | 500 | 251 | 2024-02-19 | — |
| ColCaseDbDefault | nvarchar(50), collation `Croatian_CI_AS` (database default) | Y | 750 | 0 | **251** | 2024-02-19 | — |
| ColCaseLatin1CI | nvarchar(50), collation `Latin1_General_CI_AS_KS_WS` (explicit, non-default) | Y | 750 | 0 | **251** | 2024-02-19 | — |
| ColCaseBin2 | nvarchar(50), collation `Latin1_General_BIN2` (explicit, binary) | Y | 750 | 0 | **252** | 2024-02-19 | — |
| ColIndexed | int (leading key of `IX_Orders_ColIndexed`) | N | 1000 | — | 200 | 2024-02-19 | — |
| ColNotIndexed | int (no index) | N | 1000 | — | 200 | 2024-02-19 | — |
| ColBigInt | bigint | N | 1000 | — | 1000 | 2024-02-19 | **Unique** |
| ColDecimal | decimal(18,2) | N | 1000 | — | 1000 | 2024-02-19 | **Unique** |
| ColBit | bit | N | 1000 | — | 2 | 2024-02-19 | — |
| ColDate | date | N | 1000 | — | 50 | 2024-02-19 | — |
| ColNvarcharMax | nvarchar(max) | Y | 1000 | 0 | 1000 | 2024-02-19 | **Unique** |
| ColVarbinaryMax | varbinary(max) | Y | 1000 | n/a (not string) | 1000 | 2024-02-19 | **Unique** |
| ColXml | xml | Y | 1000 | n/a | **null (skipped)** | 2024-02-19 | — |

Verification queries run (row counts/dist/lastfill all confirmed 1:1 against this table):

```
SELECT COUNT_BIG(*) FROM dbo.Orders;                                  -- 1000
SELECT COUNT_BIG(ColFilledAlways), COUNT(DISTINCT ColFilledAlways) FROM dbo.Orders;   -- 1000, 5
SELECT COUNT_BIG(ColStoppedDay10), COUNT(DISTINCT ColStoppedDay10) FROM dbo.Orders;   -- 200, 200
SELECT COUNT_BIG(ColStoppedDay30), COUNT(DISTINCT ColStoppedDay30) FROM dbo.Orders;   -- 600, 600
SELECT COUNT_BIG(ColRecentOnly),   COUNT(DISTINCT ColRecentOnly)   FROM dbo.Orders;   -- 20, 20
SELECT COUNT_BIG(ColDead)          FROM dbo.Orders;                                    -- 0
SELECT COUNT_BIG(ColConstant), COUNT(DISTINCT ColConstant) FROM dbo.Orders;            -- 1000, 1
SELECT COUNT_BIG(ColUniqueGuid), COUNT(DISTINCT ColUniqueGuid) FROM dbo.Orders;        -- 1000, 1000
SELECT COUNT_BIG(ColIndexed), COUNT(DISTINCT ColIndexed) FROM dbo.Orders;              -- 1000, 200
SELECT COUNT_BIG(ColNotIndexed), COUNT(DISTINCT ColNotIndexed) FROM dbo.Orders;        -- 1000, 200
SELECT COUNT_BIG(ColBigInt), COUNT(DISTINCT ColBigInt) FROM dbo.Orders;                -- 1000, 1000
SELECT COUNT_BIG(ColDecimal), COUNT(DISTINCT ColDecimal) FROM dbo.Orders;              -- 1000, 1000
SELECT COUNT_BIG(ColBit), COUNT(DISTINCT ColBit) FROM dbo.Orders;                      -- 1000, 2
SELECT COUNT_BIG(ColDate), COUNT(DISTINCT ColDate) FROM dbo.Orders;                    -- 1000, 50
SELECT COUNT_BIG(ColNvarcharMax), COUNT(DISTINCT ColNvarcharMax) FROM dbo.Orders;      -- 1000, 1000
SELECT COUNT_BIG(ColVarbinaryMax), COUNT(DISTINCT ColVarbinaryMax) FROM dbo.Orders;    -- 1000, 1000
SELECT COUNT_BIG(ColXml) FROM dbo.Orders;                                              -- 1000
SELECT CONVERT(varchar(20), MAX(DateCreated), 120) FROM dbo.Orders;                    -- 2024-02-19 00:00:00
```

### `ColCaseDbDefault` / `ColCaseLatin1CI` / `ColCaseBin2` — CONTRACT Amendment 11: collation-dependent distinct counts

Raised as CONTRACT.md Amendment 11 after a lead verification query hit `Msg 451: Cannot
resolve collation conflict` — this instance's server (and both `SsmsDataAnalyzerTest` and
`Test`) run collation **`Croatian_CI_AS`**, not the `Latin1_General_*` most examples assume.
The ruling: the profiler must report what `COUNT(DISTINCT …)` actually returns for a column
under *that column's own collation*, never normalise it, but must surface the collation so a
surprising number is explicable.

All three columns hold **identical data** — by `n % 4`: `0`→NULL (250 rows), `1`→`'value'`
(lowercase, 250 rows), `2`→`'VALUE'` (uppercase, 250 rows), `3`→`'Val'+n` (250 distinct real
values) — differing only in declared column collation:

```sql
SELECT
  COUNT_BIG(ColCaseDbDefault) AS filled_dbdefault,
  COUNT(DISTINCT ColCaseDbDefault) AS dist_dbdefault,   -- 251
  COUNT_BIG(ColCaseLatin1CI) AS filled_latin1ci,
  COUNT(DISTINCT ColCaseLatin1CI) AS dist_latin1ci,     -- 251
  COUNT_BIG(ColCaseBin2) AS filled_bin2,
  COUNT(DISTINCT ColCaseBin2) AS dist_bin2              -- 252
FROM dbo.Orders;
-- observed: 750, 251, 750, 251, 750, 252
```

`ColCaseDbDefault` (`Croatian_CI_AS`) and `ColCaseLatin1CI` (`Latin1_General_CI_AS_KS_WS`,
explicit and different from the database default) both agree at **251**: both are
case-*insensitive*, so `'value'` and `'VALUE'` collapse into one `DISTINCT` group, plus 250
unique `'Val'+n` values. `ColCaseBin2` (`Latin1_General_BIN2`) reports **252**: binary
collation is case-*sensitive*, so `'value'` and `'VALUE'` count as two distinct values. Same
data, same table, three genuinely different, equally correct answers — this is the executable
proof for why the profiler must never normalise distinct counts across collations.

Confirmed the collations actually landed as declared (`sys.columns.collation_name`):
`ColCaseDbDefault` → `Croatian_CI_AS`, `ColCaseLatin1CI` → `Latin1_General_CI_AS_KS_WS`,
`ColCaseBin2` → `Latin1_General_BIN2`.

Also confirmed — supporting Agent A's Amendment 11 concern 1 — that a pass-1-shaped query
touching all three differently-collated columns in one `SELECT`, including the blank-check
comparison against a literal (`LTRIM(RTRIM(col)) = ''`), runs without `Msg 451`:

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT COUNT_BIG(*) AS total,
  COUNT_BIG(ColCaseLatin1CI) AS filled_latin1, SUM(CASE WHEN LTRIM(RTRIM(ColCaseLatin1CI)) = '' THEN 1 ELSE 0 END) AS blank_latin1,
  COUNT_BIG(ColCaseBin2) AS filled_bin2, SUM(CASE WHEN LTRIM(RTRIM(ColCaseBin2)) = '' THEN 1 ELSE 0 END) AS blank_bin2
FROM dbo.Orders;
-- observed: 1000, 750, 0, 750, 0 -- no error
```

**Correction to the amendment's stated reasoning (found while seeding, verified twice, escalated
to the lead rather than silently designed around):** Amendment 11 as issued predicted the
`ColStringBlank` trailing-space case (`''` vs `'   '`) itself would diverge under `_BIN2` —
i.e., that it would stay collapsed at 251 under the database default but split to 252 under a
binary collation. **That is not what happens.** Tested directly: a table with `''` and `'   '`
in a column declared `COLLATE Latin1_General_BIN2` still reports `COUNT(DISTINCT …) = 1` for
that pair — trailing-space collapsing in SQL Server is ANSI-padding behaviour at the engine
level for `char`/`varchar`/`nvarchar` comparison, **not** governed by collation, and it holds
even under a binary collation. The genuinely collation-dependent axis turned out to be case
sensitivity, not trailing spaces, which is why `ColCaseDbDefault` / `ColCaseLatin1CI` /
`ColCaseBin2` use `'value'`/`'VALUE'` rather than `''`/`'   '`. (`ColStringBlank`'s own 251 is
therefore collation-independent on this instance — re-verified: it reports the same 251 under
`Croatian_CI_AS`, `Latin1_General_CI_AS_KS_WS`, and `Latin1_General_BIN2` alike.)

### `ColStringBlank` — the blank-vs-null exercise (IMPORTANT gotcha, verified twice)

Seeded by `n % 4`: `0`→NULL (250 rows), `1`→`''` (250 rows), `2`→`'   '` three spaces
(250 rows), `3`→`'Val'+n` (250 distinct real values).

```sql
SELECT
  COUNT_BIG(ColStringBlank) AS filled,                                            -- 750
  SUM(CASE WHEN LTRIM(RTRIM(ColStringBlank)) = '' THEN 1 ELSE 0 END) AS blank,    -- 500
  COUNT(DISTINCT ColStringBlank) AS dist,                                         -- 251
  SUM(CASE WHEN ColStringBlank IS NULL THEN 1 ELSE 0 END) AS nullcount            -- 250
FROM dbo.Orders;
```

**Observed, not assumed:** `filled` = 750 (excludes only the 250 NULLs, confirming blanks
count as "filled" — a NULL and an empty string are NOT the same thing). `blank` = 500 (the
`''` and `'   '` groups, 250 each) — this is the number that proves blank-detection is
distinct from null-detection. `distinct` = **251**, not the naively-expected 252: SQL Server's
default (non-binary) collation treats trailing spaces as insignificant in comparison
(ANSI padding), so `''` and `'   '` collapse into **one** distinct group under
`COUNT(DISTINCT …)`. This was caught only because the number was actually queried instead of
hand-derived — exactly the kind of gotcha this file exists to catch. Any profiler
implementation using `COUNT(DISTINCT ColStringBlank)` will and should also report 251.

### `ColXml` — AggregateSupport = NoDistinct (per Core's `ColumnMeta.Support`)

`xml` rejects `MIN`/`MAX`/`COUNT(DISTINCT …)` but accepts `COUNT_BIG` and `DATALENGTH`. Expect
`FilledCount = 1000`, `DistinctCount = null` with a non-null `SkipReason`, `MinValue`/`MaxValue
= null`. Raw `COUNT_BIG(ColXml)` was verified as 1000; `COUNT(DISTINCT ColXml)` was **not**
run because SQL Server actively rejects it (`Msg 306: xml data type cannot be compared`) —
confirmed interactively as a sanity check, not included in the table above as a "number".

### `ColBit` — AggregateSupport = NoMinMax

`bit` rejects `MIN`/`MAX` but accepts `COUNT_BIG`, `COUNT(DISTINCT …)`. Filled=1000,
Distinct=2 (0 and 1) confirmed above.

---

## dbo.WideTable — 120 rows, 160 columns (`Col001`..`Col160`)

Exercises the ~60-column pass-1 chunking boundary (columns 1-60, 61-120, 121-160/180 in
3 chunks of ~60 depending on the chunk size chosen by Core). `DateCreated` spans 12 distinct
days, 10 rows/day, `DATEADD(DAY, (n-1)/10, '2024-03-01')`; day 12 = **2024-03-12** (max date).
Every `ColNNN = (RowId + NNN) % 37`, always `NOT NULL` — filled 100% of rows for every column
by construction (verified for the chunk-boundary columns below; the identical formula and
`NOT NULL` constraint make this true for all 160 by construction, not by 160 individual
queries).

| Column | Filled | Distinct | LastFillDate |
|---|---|---|---|
| Col001 (first) | 120 | 37 | 2024-03-12 |
| Col060 (end of chunk 1 @ batch size 60) | 120 | 37 | 2024-03-12 |
| Col061 (start of chunk 2) | 120 | 37 | 2024-03-12 |
| Col120 (end of chunk 2) | 120 | 37 | 2024-03-12 |
| Col121 (start of chunk 3) | 120 | 37 | 2024-03-12 |
| Col160 (last) | 120 | 37 | 2024-03-12 |

All six confirmed live via `sqlcmd`; distinct = 37 in every case because
`(RowId + NNN) % 37` cycles through all 37 residues as `RowId` ranges 1..120 (120 ≥ 37, so
every residue is hit for any fixed `NNN`). `TotalRows` confirmed = 120.

No flags expected on any `WideTable` column: fill = 100% (not Sparse/Dead), distinct = 37
≠ 120 (not Unique) and ≠ 1 (not Constant).

---

## dbo.NoDateTable — 20 rows, NO DateCreated / fallback-candidate column at all

Columns: `Id` (int, identity, PK), `Name` (nvarchar(50)), `Amount` (decimal(10,2)). None of
`DateCreated, CreatedDate, CreatedOn, Created, InsertDate, DateInserted, RowCreatedAt,
ModifiedDate` exist on this table.

| Column | Filled | Distinct | LastFillDate |
|---|---|---|---|
| Id | 20 | 20 | **null — no DateCreated column resolved** |
| Name | 20 | 20 | null |
| Amount | 20 | 20 | null |

Verified: `COUNT_BIG`/`COUNT(DISTINCT)` = 20/20 for all three columns (all always-filled,
all-unique by construction — `Name = 'Item'+n`, `Amount = n*3.5`). Expect
`TableProfile.DateCreatedColumn = null`; every other metric (rows, filled, distinct, flags)
profiles normally — only `LastFillDate` is unavailable.

---

## dbo.FallbackDateTable — 20 rows, has `CreatedOn` (candidate #3) but no `DateCreated`

Columns: `Id` (int, identity, PK), `CreatedOn` (datetime2, NOT NULL), `Value` (int, NOT NULL).
Proves the fallback candidate-list order resolves to `CreatedOn` (3rd in the default list:
`DateCreated, CreatedDate, CreatedOn, …`) since `DateCreated` and `CreatedDate` are both
absent.

`CreatedOn = DATEADD(DAY, (n-1)/5, '2024-05-01')` for n=1..20 → 4 distinct days
(dayindex 0,1,2,3), max = **2024-05-04**.

| Column | Filled | Distinct | LastFillDate |
|---|---|---|---|
| Id | 20 | 20 | 2024-05-04 |
| CreatedOn | 20 | 4 | 2024-05-04 |
| Value | 20 | 20 | 2024-05-04 |

Verified live: all three filled/distinct pairs and the 2024-05-04 max confirmed via `sqlcmd`.
Expect `TableProfile.DateCreatedColumn = "CreatedOn"`.

---

## dbo.EmptyTable — 0 rows (division-by-zero risk)

Columns: `Id` (int), `Name` (nvarchar(50)), `DateCreated` (datetime2), all nullable, no rows
inserted.

| Column | TotalRows | Filled | Distinct | LastFillDate |
|---|---|---|---|---|
| Id | 0 | 0 | 0 | null |
| Name | 0 | 0 | 0 | null |
| DateCreated | 0 | 0 | 0 | null |

Verified: `SELECT COUNT_BIG(*), COUNT_BIG(Id), COUNT_BIG(Name), COUNT_BIG(DateCreated) FROM
dbo.EmptyTable;` returned `0, 0, 0, 0`.

**Flags: `ColumnFlag.None` for every column — per CONTRACT.md Amendment 2, not `Dead`.**

This started as an open finding from this seed: on a 0-row table every column trivially has
`FilledCount == 0`, so the literal `Dead` rule ("no rows filled") fires on every single column,
degenerating into a restatement of "the table is empty" rather than a finding about any one
column. Escalated to the lead and ruled on as CONTRACT Amendment 2: **when
`TotalRowsContext == 0`, every column's `Flags` is `ColumnFlag.None`** (Sparse/Constant/Unique
carry no information at zero rows either), and `TableProfile.Warnings` must contain exactly one
entry with this exact text:

```
Table is empty — per-column flags are not meaningful.
```

Also per Amendment 1, `ColumnProfile.TotalRowsContext` is now a public settable property
(was `internal`), specifically so this rule is unit-testable against a hand-built
`ColumnProfile` and not only through the live profiler — see
`tests/SsmsDataAnalyzer.Tests/Model/ColumnProfileFlagsTests.cs`.

Every derived percentage (`FillRatio`, fill %, distinct %, Sparse-threshold check) must still
guard the `TotalRowsContext == 0` / `FilledCount == 0` denominators regardless: `FillRatio`
and `DistinctRatio` both explicitly return `null` rather than dividing by zero.

---

## dbo.[Bracket]Table] / [Value]Col] — bracket-doubling exercise

Table name is literally `Bracket]Table` (declared as `[dbo].[Bracket]]Table]`); one column
name is literally `Value]Col` (declared as `[Value]]Col]`). 10 rows.
`DateCreated = DATEADD(DAY, n, '2024-06-01')` for n=1..10, max = **2024-06-11**.

| Column | Filled | Distinct | LastFillDate |
|---|---|---|---|
| Id | 10 | 10 | 2024-06-11 |
| Value]Col | 10 | 10 | 2024-06-11 |
| DateCreated | 10 | 10 | 2024-06-11 |

Verified live via `sqlcmd` referencing `[dbo].[Bracket]]Table]` and `[Value]]Col]` — both
queries succeed only if bracket-doubling is correct; a single-bracket escape would either fail
to parse or reference the wrong object.
`TableRef("dbo","Bracket]Table").QualifiedName` must equal `[dbo].[Bracket]]Table]`.

---

## dbo.Orders metadata (`sys.columns` / `sys.index_columns`, confirmed live)

```
name              typ               max_length is_nullable is_identity is_pk leading_index
OrderId           int               4          0           1           1     PK_Orders
DateCreated       datetime2         6          0           0           0     NULL
ColFilledAlways   nvarchar          100        0           0           0     NULL
ColStoppedDay10   int               4          1           0           0     NULL
ColStoppedDay30   int               4          1           0           0     NULL
ColRecentOnly     int               4          1           0           0     NULL
ColDead           int               4          1           0           0     NULL
ColConstant       int               4          0           0           0     NULL
ColUniqueGuid     uniqueidentifier  16         0           0           0     NULL
ColStringBlank    nvarchar          100        1           0           0     NULL
ColCaseDbDefault  nvarchar          100        1           0           0     NULL  (collation Croatian_CI_AS)
ColCaseLatin1CI   nvarchar          100        1           0           0     NULL  (collation Latin1_General_CI_AS_KS_WS)
ColCaseBin2       nvarchar          100        1           0           0     NULL  (collation Latin1_General_BIN2)
ColIndexed        int               4          0           0           0     IX_Orders_ColIndexed
ColNotIndexed     int               4          0           0           0     NULL
ColBigInt         bigint            8          0           0           0     NULL
ColDecimal        decimal           9          0           0           0     NULL
ColBit            bit               1          0           0           0     NULL
ColDate           date              3          0           0           0     NULL
ColNvarcharMax    nvarchar          -1         1           0           0     NULL
ColVarbinaryMax   varbinary         -1         1           0           0     NULL
ColXml            xml               -1         1           0           0     NULL
```

`max_length` for `nvarchar(50)` is 100 (bytes, 2 bytes/char) — matches `ColumnMeta.CharLength`
semantics in Core. `max_length = -1` on the three MAX/LOB columns confirms `IsLob = true`.

---

## Row counts, all tables (confirmed live in one pass)

```
Orders               1000
WideTable             120
NoDateTable            20
FallbackDateTable      20
EmptyTable               0
[Bracket]Table]         10
```

---

## dbo.FkChild and its parents — CONTRACT Amendments 14 & 15: FK metadata, the four-state rule

`dbo.FkChild` (3 rows) exercises every case in the four-state rule in one table, joined against
six parent objects: `ref.ParentSingle` (cross-schema), `dbo.ParentComposite` (two-column key),
`dbo.ParentMultiA` / `dbo.ParentMultiB` (disjoint id spaces `{1,2}` / `{101,102}`),
`dbo.ParentDisabled`, `dbo.SelfRefTable` (self-referencing, no `FkChild` column involved), and
`[dbo].[Intervention.ABB.Request.Change.History]` (dotted name).

Ground truth, read directly from `sys.foreign_keys` / `sys.foreign_key_columns` (not the
profiler — this is what Core's pass-0 FK read must reproduce):

```sql
SELECT fk.name AS fk_name, fk.is_disabled, fk.is_not_trusted,
  COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS child_col,
  SCHEMA_NAME(t.schema_id) AS ref_schema, t.name AS ref_table,
  COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ref_col,
  fkc.constraint_column_id
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables t ON t.object_id = fk.referenced_object_id
WHERE fk.parent_object_id = OBJECT_ID('dbo.FkChild')
ORDER BY fk.name, fkc.constraint_column_id;
```

Observed:

| fk_name | is_disabled | is_not_trusted | child_col | ref_schema | ref_table | ref_col | constraint_column_id |
|---|---|---|---|---|---|---|---|
| FK_FkChild_Composite | 0 | 0 | CompFkA | dbo | ParentComposite | KeyA | 1 |
| FK_FkChild_Composite | 0 | 0 | CompFkB | dbo | ParentComposite | KeyB | 2 |
| FK_FkChild_Disabled | 1 | 1 | DisabledFkCol | dbo | ParentDisabled | Id | 1 |
| FK_FkChild_Dotted | 0 | 0 | DottedFkCol | dbo | Intervention.ABB.Request.Change.History | Id | 1 |
| FK_FkChild_MultiA | 0 | 0 | MultiFkCol | dbo | ParentMultiA | Id | 1 |
| FK_FkChild_MultiB | 0 | 0 | MultiFkCol | dbo | ParentMultiB | Id | 1 |
| FK_FkChild_Single | 0 | 0 | SingleFkCol | ref | ParentSingle | Id | 1 |

**The composite row (`FK_FkChild_Composite`) proves the pairing the catalog holds**:
`constraint_column_id` 1↔2 lines up `CompFkA`→`KeyA` and `CompFkB`→`KeyB` exactly —
`sys.foreign_key_columns` genuinely knows which child column maps to which parent column. Core
nulling `ReferencedColumn` for this case (Amendment 15) is a deliberate choice to withhold an
answer it actually has, not a gap in what the catalog reports.

### Expected `ColumnMeta` four-state table, per Amendment 15's exact rule

| Column | IsForeignKey | ForeignKeyCount | ReferencedSchema | ReferencedTable | ReferencedColumn | ForeignKeyName | HasUnresolvedForeignKey | ReferencedQualifiedName |
|---|---|---|---|---|---|---|---|---|
| SingleFkCol | true | 1 | `ref` | `ParentSingle` | `Id` | `FK_FkChild_Single` | false | `[ref].[ParentSingle]` |
| CompFkA | true | 1 | `dbo` | `ParentComposite` | **NULL** | `FK_FkChild_Composite` | false | `[dbo].[ParentComposite]` |
| CompFkB | true | 1 | `dbo` | `ParentComposite` | **NULL** | `FK_FkChild_Composite` | false | `[dbo].[ParentComposite]` |
| MultiFkCol | true | **2** | NULL | NULL | NULL | NULL | **true** | NULL |
| DisabledFkCol | true | 1 | `dbo` | `ParentDisabled` | `Id` | `FK_FkChild_Disabled` | false | `[dbo].[ParentDisabled]` |
| DottedFkCol | true | 1 | `dbo` | `Intervention.ABB.Request.Change.History` | `Id` | `FK_FkChild_Dotted` | false | `` `[dbo].[Intervention.ABB.Request.Change.History]` `` |
| PlainCol | false | 0 | NULL | NULL | NULL | NULL | false | NULL |
| Id (PK, not FK) | false | 0 | NULL | NULL | NULL | NULL | false | NULL |

`ParentId` on `dbo.SelfRefTable` (self-referencing case, separate table): `IsForeignKey=true`,
`ForeignKeyCount=1`, `ReferencedSchema='dbo'`, `ReferencedTable='SelfRefTable'` (itself),
`ReferencedColumn='Id'`, `ForeignKeyName='FK_SelfRefTable_Parent'`, `HasUnresolvedForeignKey=false`.

**Disabled FK really is unenforced, not just cosmetically flagged** — proven with live data, not
assumed from `is_disabled=1`: every `DisabledFkCol` value in the 3 seeded rows is `999`, which
does not exist in `ParentDisabled` (`{1, 2}`). The insert only succeeded because the constraint
was disabled (`ALTER TABLE dbo.FkChild NOCHECK CONSTRAINT FK_FkChild_Disabled`) *before* the
insert ran. Per Amendment 14, Core must resolve this FK identically to an enabled one — disabled
and untrusted are not flagged differently, only real vs. absent is.

**`MultiFkCol` is always NULL in every seeded row, by construction, not by accident** —
`ParentMultiA` holds `{1, 2}` and `ParentMultiB` holds `{101, 102}` (deliberately disjoint), so
no single value could ever satisfy both `FK_FkChild_MultiA` and `FK_FkChild_MultiB`
simultaneously. Tried on this exact data during seeding: any non-NULL value fails one of the two
constraints (`Msg 547`) — living proof of the reasoning in Amendment 15 for why a value-filtered
"go to source" jump on a multi-FK column is meaningless, not merely inconvenient.

**Dotted table name round-trips as a single identifier**, confirmed live:

```sql
SELECT OBJECT_ID('[dbo].[Intervention.ABB.Request.Change.History]') AS resolved_oid,
       OBJECT_NAME(OBJECT_ID('[dbo].[Intervention.ABB.Request.Change.History]')) AS resolved_name;
-- observed: a real object id, resolved_name = 'Intervention.ABB.Request.Change.History'
SELECT COUNT_BIG(*) FROM [dbo].[Intervention.ABB.Request.Change.History];  -- observed: 2
```
So `ColumnMeta.ReferencedTable` for `DottedFkCol` must be the single string
`"Intervention.ABB.Request.Change.History"` (not four segments), and
`ReferencedQualifiedName` must bracket-double it as one bracketed identifier:
`[dbo].[Intervention.ABB.Request.Change.History]` — no internal brackets around the dots.

### `dbo.FkChild` row data (verified: `SELECT * FROM dbo.FkChild ORDER BY Id`)

| Id | SingleFkCol | CompFkA | CompFkB | MultiFkCol | DisabledFkCol | DottedFkCol | PlainCol |
|---|---|---|---|---|---|---|---|
| 1 | 1 | 1 | 1 | NULL | 999 | 1 | 100 |
| 2 | 2 | 1 | 2 | NULL | 999 | 2 | 200 |
| 3 | 3 | 2 | 1 | NULL | 999 | 1 | 300 |

`TotalRows` for `dbo.FkChild` = 3 (confirmed via `COUNT_BIG(*)`).

---

## Row counts, all tables (confirmed live in one pass, FK objects included)

```
Orders               1000
WideTable             120
NoDateTable            20
FallbackDateTable      20
EmptyTable               0
[Bracket]Table]         10
FkChild                  3
ParentSingle (ref)       3
ParentComposite          3
ParentMultiA             2
ParentMultiB             2
ParentDisabled           2
SelfRefTable             3
Intervention.ABB.Request.Change.History   2
```
