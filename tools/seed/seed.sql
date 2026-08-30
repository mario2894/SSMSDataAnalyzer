/*
    seed.sql — SsmsDataAnalyzerTest ground-truth seed database.
    Owner: Agent D (test-data). Drop-and-recreate, idempotent, safe to re-run any number of times.

    Every table here is engineered so every profiler metric has a KNOWN, hand-computable /
    hand-verified expected value. See tools/seed/expected.md for the verified ground truth
    (every number there was produced by actually running the equivalent COUNT/MAX query).
*/

SET NOCOUNT ON;
GO

USE master;
GO

IF DB_ID('SsmsDataAnalyzerTest') IS NOT NULL
BEGIN
    ALTER DATABASE SsmsDataAnalyzerTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SsmsDataAnalyzerTest;
END
GO

CREATE DATABASE SsmsDataAnalyzerTest;
GO

USE SsmsDataAnalyzerTest;
GO

-- ============================================================================
-- Helper: a reusable Numbers sequence 1..2000, built set-based (no loops).
-- ============================================================================
;WITH E1(N) AS (SELECT N FROM (VALUES (1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) v(N)), -- 10
E2(N) AS (SELECT 1 FROM E1 a CROSS JOIN E1 b),                                        -- 100
E3(N) AS (SELECT 1 FROM E2 a CROSS JOIN E2 b)                                         -- 10000
SELECT TOP (2000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS INT) AS n
INTO dbo.Numbers
FROM E3;
GO
ALTER TABLE dbo.Numbers ALTER COLUMN n int NOT NULL;
GO
ALTER TABLE dbo.Numbers ADD CONSTRAINT PK_Numbers PRIMARY KEY CLUSTERED (n);
GO

-- ============================================================================
-- Table 1: dbo.Orders — the kitchen-sink table.
--   1000 rows. DateCreated spread across 50 distinct days (20 rows/day),
--   base date 2024-01-01, day50 = 2024-02-19.
--   Deliberately engineered so different columns have DIFFERENT last-fill dates.
-- ============================================================================
CREATE TABLE dbo.Orders
(
    OrderId          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED,
    DateCreated      datetime2(0)      NOT NULL,

    -- normal, always-filled column (5 distinct statuses)
    ColFilledAlways  nvarchar(50)      NOT NULL,

    -- filled only through day 10 -> LastFillDate = 2024-01-10, then NULL forever after
    ColStoppedDay10  int               NULL,

    -- filled only through day 30 -> LastFillDate = 2024-01-30
    ColStoppedDay30  int               NULL,

    -- filled ONLY on day 50 (last 20 rows = 2% of table) -> Sparse AND LastFillDate = 2024-02-19
    ColRecentOnly    int               NULL,

    -- fully NULL -> Dead flag, LastFillDate = NULL
    ColDead          int               NULL,

    -- single value in every row -> Constant flag (distinct = 1)
    ColConstant      int               NOT NULL,

    -- unique GUID per row, always filled -> Unique flag (distinct = row count)
    ColUniqueGuid    uniqueidentifier  NOT NULL,

    -- NULL / '' / whitespace-only / real value, by OrderId % 4 — exercises blank-vs-null logic
    ColStringBlank   nvarchar(50)      NULL,

    -- CONTRACT Amendment 11: collation-dependent distinct counts.
    -- Same NULL/'value'/'VALUE'/unique data in all three, different collations:
    --   ColCaseDbDefault — implicit database default collation (Croatian_CI_AS on this
    --                      instance), case-insensitive: 'value' and 'VALUE' collapse.
    --   ColCaseLatin1CI  — EXPLICIT non-default collation, still case-insensitive: proves
    --                      generated SQL stays collation-safe (no Msg 451) with an explicit,
    --                      differing column collation, and gives the same collapsed count as
    --                      the database-default column.
    --   ColCaseBin2      — binary (_BIN2): case-sensitive, so 'value' and 'VALUE' do NOT
    --                      collapse. This — not trailing spaces — is the genuine
    --                      collation-dependent axis for this seed; see expected.md for the
    --                      empirical correction to the amendment's original trailing-space
    --                      hypothesis (verified: trailing-space collapsing turned out to be
    --                      collation-INDEPENDENT ANSI padding behaviour, not a _BIN2 effect).
    ColCaseDbDefault nvarchar(50)      NULL,
    ColCaseLatin1CI  nvarchar(50)      COLLATE Latin1_General_CI_AS_KS_WS NULL,
    ColCaseBin2      nvarchar(50)      COLLATE Latin1_General_BIN2 NULL,

    -- leading key of a nonclustered index -> DistinctPlanner index-backed fast path
    ColIndexed       int               NOT NULL,

    -- identical distribution, NO index -> DistinctPlanner batched path
    ColNotIndexed    int               NOT NULL,

    -- remaining type coverage (all always-filled)
    ColBigInt        bigint            NOT NULL,
    ColDecimal       decimal(18,2)     NOT NULL,
    ColBit           bit               NOT NULL,
    ColDate          date              NOT NULL,
    ColNvarcharMax   nvarchar(max)     NULL,
    ColVarbinaryMax  varbinary(max)    NULL,
    ColXml           xml               NULL
);
GO

INSERT INTO dbo.Orders
    (DateCreated, ColFilledAlways, ColStoppedDay10, ColStoppedDay30, ColRecentOnly,
     ColDead, ColConstant, ColUniqueGuid, ColStringBlank,
     ColCaseDbDefault, ColCaseLatin1CI, ColCaseBin2,
     ColIndexed, ColNotIndexed,
     ColBigInt, ColDecimal, ColBit, ColDate, ColNvarcharMax, ColVarbinaryMax, ColXml)
SELECT
    DATEADD(DAY, ((n - 1) / 20), '2024-01-01'),                                   -- DateCreated
    'Status' + CAST(n % 5 AS varchar(1)),                                         -- ColFilledAlways
    CASE WHEN ((n - 1) / 20) + 1 <= 10 THEN n END,                                -- ColStoppedDay10
    CASE WHEN ((n - 1) / 20) + 1 <= 30 THEN n * 2 END,                            -- ColStoppedDay30
    CASE WHEN ((n - 1) / 20) + 1 = 50 THEN n END,                                 -- ColRecentOnly
    NULL,                                                                         -- ColDead
    42,                                                                           -- ColConstant
    NEWID(),                                                                      -- ColUniqueGuid
    CASE n % 4
        WHEN 0 THEN NULL
        WHEN 1 THEN ''
        WHEN 2 THEN '   '
        ELSE 'Val' + CAST(n AS varchar(10))
    END,                                                                          -- ColStringBlank
    CASE n % 4
        WHEN 0 THEN NULL
        WHEN 1 THEN 'value'
        WHEN 2 THEN 'VALUE'
        ELSE 'Val' + CAST(n AS varchar(10))
    END,                                                                          -- ColCaseDbDefault
    CASE n % 4
        WHEN 0 THEN NULL
        WHEN 1 THEN 'value'
        WHEN 2 THEN 'VALUE'
        ELSE 'Val' + CAST(n AS varchar(10))
    END,                                                                          -- ColCaseLatin1CI
    CASE n % 4
        WHEN 0 THEN NULL
        WHEN 1 THEN 'value'
        WHEN 2 THEN 'VALUE'
        ELSE 'Val' + CAST(n AS varchar(10))
    END,                                                                          -- ColCaseBin2
    n % 200,                                                                      -- ColIndexed
    n % 200,                                                                      -- ColNotIndexed
    CAST(900000000000 + n AS bigint),                                             -- ColBigInt
    CAST(n AS decimal(18,2)) * 1.11,                                              -- ColDecimal
    n % 2,                                                                        -- ColBit
    CAST(DATEADD(DAY, ((n - 1) / 20), '2024-01-01') AS date),                     -- ColDate
    REPLICATE('x', 50) + CAST(n AS varchar(10)),                                  -- ColNvarcharMax
    CONVERT(varbinary(max), 'bin' + CAST(n AS varchar(10))),                      -- ColVarbinaryMax
    CAST('<a>' + CAST(n AS varchar(10)) + '</a>' AS xml)                          -- ColXml
FROM dbo.Numbers
WHERE n <= 1000;
GO

CREATE NONCLUSTERED INDEX IX_Orders_ColIndexed ON dbo.Orders (ColIndexed);
GO

-- ============================================================================
-- Table 2: dbo.WideTable — 160 columns, exercises ~60-column pass-1 chunking.
--   120 rows, DateCreated over 12 distinct days (10 rows/day), base 2024-03-01.
--   ColNNN = ((RowId + N) % 37), always NOT NULL.
-- ============================================================================
DECLARE @sql nvarchar(max) = N'CREATE TABLE dbo.WideTable (
    RowId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_WideTable PRIMARY KEY CLUSTERED,
    DateCreated datetime2(0) NOT NULL,
';
DECLARE @i int = 1;
WHILE @i <= 160
BEGIN
    SET @sql += N'    Col' + RIGHT('000' + CAST(@i AS varchar(3)), 3) + N' int NOT NULL,' + CHAR(10);
    SET @i += 1;
END
SET @sql = LEFT(@sql, LEN(@sql) - 2) + N'
);';
EXEC (@sql);
GO

DECLARE @cols nvarchar(max) = N'';
DECLARE @vals nvarchar(max) = N'';
DECLARE @j int = 1;
WHILE @j <= 160
BEGIN
    SET @cols += N'Col' + RIGHT('000' + CAST(@j AS varchar(3)), 3) + N', ';
    SET @vals += N'((n + ' + CAST(@j AS varchar(3)) + N') % 37), ';
    SET @j += 1;
END
DECLARE @insertSql nvarchar(max) =
    N'INSERT INTO dbo.WideTable (DateCreated, ' + LEFT(@cols, LEN(@cols) - 1) + N')
      SELECT DATEADD(DAY, ((n - 1) / 10), ''2024-03-01''), ' + LEFT(@vals, LEN(@vals) - 1) + N'
      FROM dbo.Numbers WHERE n <= 120;';
EXEC (@insertSql);
GO

-- ============================================================================
-- Table 3: dbo.NoDateTable — no DateCreated column and no fallback-candidate name.
--   Expect DateCreatedColumn = null, LastFillDate = null for every column,
--   everything else still profiles normally.
-- ============================================================================
CREATE TABLE dbo.NoDateTable
(
    Id      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_NoDateTable PRIMARY KEY CLUSTERED,
    Name    nvarchar(50)      NULL,
    Amount  decimal(10,2)     NULL
);
GO

INSERT INTO dbo.NoDateTable (Name, Amount)
SELECT 'Item' + CAST(n AS varchar(10)), CAST(n AS decimal(10,2)) * 3.5
FROM dbo.Numbers
WHERE n <= 20;
GO

-- ============================================================================
-- Table 4: dbo.FallbackDateTable — has "CreatedOn" (candidate #3) but no
--   "DateCreated" or "CreatedDate" — proves the fallback candidate-list order.
-- ============================================================================
CREATE TABLE dbo.FallbackDateTable
(
    Id         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FallbackDateTable PRIMARY KEY CLUSTERED,
    CreatedOn  datetime2(0)      NOT NULL,
    Value      int               NOT NULL
);
GO

INSERT INTO dbo.FallbackDateTable (CreatedOn, Value)
SELECT DATEADD(DAY, ((n - 1) / 5), '2024-05-01'), n * 7
FROM dbo.Numbers
WHERE n <= 20;
GO

-- ============================================================================
-- Table 5: dbo.EmptyTable — 0 rows. Division-by-zero risk in percentage math.
-- ============================================================================
CREATE TABLE dbo.EmptyTable
(
    Id           int              NOT NULL,
    Name         nvarchar(50)     NULL,
    DateCreated  datetime2(0)     NULL
);
GO

-- ============================================================================
-- Table 6: dbo.[Bracket]Table] (name contains a literal ']') with a column
--   [Value]Col] that also contains a literal ']' — proves bracket-doubling
--   for both table and column identifiers.
-- ============================================================================
CREATE TABLE [dbo].[Bracket]]Table]
(
    [Id]          int IDENTITY(1,1) NOT NULL,
    [Value]]Col]  nvarchar(50)      NULL,
    [DateCreated] datetime2(0)      NOT NULL,
    CONSTRAINT [PK_BracketTable] PRIMARY KEY CLUSTERED ([Id])
);
GO

INSERT INTO [dbo].[Bracket]]Table] ([Value]]Col], [DateCreated])
SELECT 'Bracket' + CAST(n AS varchar(10)), DATEADD(DAY, n, '2024-06-01')
FROM dbo.Numbers
WHERE n <= 10;
GO

-- ============================================================================
-- Cleanup: drop the helper Numbers table (not part of the profiling surface).
-- ============================================================================
DROP TABLE dbo.Numbers;
GO

PRINT 'Seed complete.';
GO

-- ============================================================================
-- Foreign-key metadata seed (CONTRACT Amendments 14 & 15 — the four-state rule).
-- Second schema "ref" exercises the cross-schema single-column case.
-- ============================================================================
CREATE SCHEMA ref;
GO

-- Single-column FK target, cross-schema (dbo.FkChild -> ref.ParentSingle).
CREATE TABLE ref.ParentSingle (Id int NOT NULL CONSTRAINT PK_ParentSingle PRIMARY KEY);
INSERT INTO ref.ParentSingle (Id) VALUES (1), (2), (3);
GO

-- Composite-key target: one constraint, two columns.
CREATE TABLE dbo.ParentComposite
(
    KeyA int NOT NULL,
    KeyB int NOT NULL,
    CONSTRAINT PK_ParentComposite PRIMARY KEY (KeyA, KeyB)
);
INSERT INTO dbo.ParentComposite (KeyA, KeyB) VALUES (1, 1), (1, 2), (2, 1);
GO

-- Two DISJOINT-id targets for the multi-FK case: no value can satisfy both
-- constraints at once (proves the "Msg 547 on any non-NULL value" reasoning
-- CONTRACT Amendment 15 cites for why the value-jump is meaningless there).
CREATE TABLE dbo.ParentMultiA (Id int NOT NULL CONSTRAINT PK_ParentMultiA PRIMARY KEY);
INSERT INTO dbo.ParentMultiA (Id) VALUES (1), (2);
GO
CREATE TABLE dbo.ParentMultiB (Id int NOT NULL CONSTRAINT PK_ParentMultiB PRIMARY KEY);
INSERT INTO dbo.ParentMultiB (Id) VALUES (101), (102);
GO

-- Disabled/untrusted FK target.
CREATE TABLE dbo.ParentDisabled (Id int NOT NULL CONSTRAINT PK_ParentDisabled PRIMARY KEY);
INSERT INTO dbo.ParentDisabled (Id) VALUES (1), (2);
GO

-- FK target whose name contains periods — a live case from the user's real database
-- (e.g. "Intervention.ABB.Request.Change.History"). Proves ReferencedTable round-trips
-- as ONE identifier and ReferencedQualifiedName bracket-doubles it as a single name,
-- not split on the dots.
CREATE TABLE [dbo].[Intervention.ABB.Request.Change.History]
(
    Id int NOT NULL CONSTRAINT PK_InterventionHistory PRIMARY KEY
);
INSERT INTO [dbo].[Intervention.ABB.Request.Change.History] (Id) VALUES (1), (2);
GO

-- Self-referencing FK: parent and child in the same table.
CREATE TABLE dbo.SelfRefTable
(
    Id       int NOT NULL CONSTRAINT PK_SelfRefTable PRIMARY KEY,
    ParentId int NULL,
    CONSTRAINT FK_SelfRefTable_Parent FOREIGN KEY (ParentId) REFERENCES dbo.SelfRefTable (Id)
);
INSERT INTO dbo.SelfRefTable (Id, ParentId) VALUES (1, NULL), (2, 1), (3, 1);
GO

-- The child table exercising every FK case (plus one plain non-FK column) in one place.
CREATE TABLE dbo.FkChild
(
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FkChild PRIMARY KEY,
    DateCreated   datetime2(0)      NOT NULL,

    -- Single-column FK, cross-schema (dbo -> ref).
    SingleFkCol   int               NULL,

    -- Composite FK: one constraint, two columns -> dbo.ParentComposite(KeyA, KeyB).
    CompFkA       int               NULL,
    CompFkB       int               NULL,

    -- Participates in TWO separate FKs (-> ParentMultiA and -> ParentMultiB), which have
    -- disjoint id spaces, so this column can only ever legally be NULL.
    MultiFkCol    int               NULL,

    -- Disabled + untrusted FK -> dbo.ParentDisabled. Still a real, declared relationship —
    -- must resolve exactly like an enabled one. Seeded with a value (999) that does NOT
    -- exist in ParentDisabled, which only succeeds BECAUSE the constraint is disabled —
    -- live proof the constraint is genuinely unenforced, not just cosmetically flagged.
    DisabledFkCol int               NULL,

    -- FK to the dotted-name table.
    DottedFkCol   int               NULL,

    -- No FK at all.
    PlainCol      int               NULL
);
GO

ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_Single
    FOREIGN KEY (SingleFkCol) REFERENCES ref.ParentSingle (Id);
GO

ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_Composite
    FOREIGN KEY (CompFkA, CompFkB) REFERENCES dbo.ParentComposite (KeyA, KeyB);
GO

ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_MultiA
    FOREIGN KEY (MultiFkCol) REFERENCES dbo.ParentMultiA (Id);
GO
ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_MultiB
    FOREIGN KEY (MultiFkCol) REFERENCES dbo.ParentMultiB (Id);
GO

ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_Disabled
    FOREIGN KEY (DisabledFkCol) REFERENCES dbo.ParentDisabled (Id);
GO
ALTER TABLE dbo.FkChild NOCHECK CONSTRAINT FK_FkChild_Disabled;
GO

ALTER TABLE dbo.FkChild ADD CONSTRAINT FK_FkChild_Dotted
    FOREIGN KEY (DottedFkCol) REFERENCES [dbo].[Intervention.ABB.Request.Change.History] (Id);
GO

INSERT INTO dbo.FkChild
    (DateCreated, SingleFkCol, CompFkA, CompFkB, MultiFkCol, DisabledFkCol, DottedFkCol, PlainCol)
VALUES
    (SYSDATETIME(), 1, 1, 1, NULL, 999, 1, 100),
    (SYSDATETIME(), 2, 1, 2, NULL, 999, 2, 200),
    (SYSDATETIME(), 3, 2, 1, NULL, 999, 1, 300);
GO
