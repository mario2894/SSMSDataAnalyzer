using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Metadata
{
    /// <summary>Result of pass 0 — metadata only, never touches the table's data pages.</summary>
    public sealed class TableSchema
    {
        public IList<ColumnMeta> Columns { get; set; }
        public long EstimatedRows { get; set; }
        public string DateCreatedColumn { get; set; }

        /// <summary>Every date/time-typed column, offered as DateCreated override candidates.</summary>
        public IList<string> DateTimeColumns { get; set; }

        public TableSchema()
        {
            Columns = new List<ColumnMeta>();
            DateTimeColumns = new List<string>();
        }
    }

    /// <summary>
    /// Pass 0: sys.columns + sys.types + PK + leading-index-key columns, and the row estimate
    /// from sys.dm_db_partition_stats. Everything is parameterised — the table name is bound,
    /// never concatenated.
    /// </summary>
    public sealed class SchemaReader
    {
        private const string ColumnsSql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
DECLARE @oid int = OBJECT_ID(@table);
IF @oid IS NULL
    THROW 51000, 'Table not found, or the caller lacks permission to see it.', 1;

SELECT
    c.name                                                        AS ColumnName,
    c.column_id                                                   AS ColumnId,
    t.name                                                        AS TypeName,
    c.max_length                                                  AS MaxLength,
    c.is_nullable                                                 AS IsNullable,
    c.is_identity                                                 AS IsIdentity,
    c.is_computed                                                 AS IsComputed,
    CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END          AS IsPrimaryKey,
    li.name                                                       AS LeadingIndexName,
    c.collation_name                                              AS Collation,
    fkAgg.FkCount                                                 AS FkCount,
    fkRef.RefSchema                                               AS ReferencedSchema,
    fkRef.RefTable                                                AS ReferencedTable,
    fkRef.RefColumn                                               AS ReferencedColumn,
    fkRef.FkName                                                  AS ForeignKeyName
FROM sys.columns c
JOIN sys.types t
    ON t.user_type_id = c.user_type_id
OUTER APPLY (
    SELECT TOP (1) ic.column_id
    FROM sys.index_columns ic
    JOIN sys.indexes i
        ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE ic.object_id = c.object_id
      AND ic.column_id = c.column_id
      AND i.is_primary_key = 1
) pk
OUTER APPLY (
    SELECT TOP (1) i.name
    FROM sys.index_columns ic
    JOIN sys.indexes i
        ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE ic.object_id = c.object_id
      AND ic.column_id = c.column_id
      AND ic.key_ordinal = 1
      AND i.type IN (1, 2)          -- clustered / nonclustered b-tree
      AND i.is_disabled = 0
      AND i.has_filter = 0          -- a filtered index would under-count DISTINCT
    -- prefer a nonclustered index: scanning it is far cheaper than the clustered index,
    -- which is the table itself.
    ORDER BY CASE WHEN i.type = 2 THEN 0 ELSE 1 END, i.index_id
) li
-- How many distinct FK constraints involve this column, and (if exactly one) which.
-- Disabled and untrusted FKs are included on purpose: they still describe a real relationship.
OUTER APPLY (
    SELECT COUNT(*) AS FkCount, MIN(fkc.constraint_object_id) AS OnlyFkId
    FROM sys.foreign_key_columns fkc
    WHERE fkc.parent_object_id = c.object_id
      AND fkc.parent_column_id = c.column_id
) fkAgg
-- Amendment 15: resolve the TABLE whenever exactly one FK involves this column — a composite
-- FK is one constraint referencing one table, so the table is never ambiguous. Resolve the
-- COLUMN only when that constraint is single-column: the catalog does record the composite
-- pairing, but filtering a parent on half a composite key returns plausible-but-wrong rows.
OUTER APPLY (
    SELECT TOP (1)
        rs.name AS RefSchema,
        rt.name AS RefTable,
        f.name  AS FkName,
        CASE WHEN fkw.Width = 1 THEN rc.name END AS RefColumn
    FROM sys.foreign_key_columns fkc
    JOIN sys.foreign_keys f  ON f.object_id  = fkc.constraint_object_id
    JOIN sys.tables       rt ON rt.object_id = fkc.referenced_object_id
    JOIN sys.schemas      rs ON rs.schema_id = rt.schema_id
    JOIN sys.columns      rc ON rc.object_id = fkc.referenced_object_id
                            AND rc.column_id = fkc.referenced_column_id
    CROSS APPLY (
        SELECT COUNT(*) AS Width
        FROM sys.foreign_key_columns w
        WHERE w.constraint_object_id = fkAgg.OnlyFkId
    ) fkw
    WHERE fkAgg.FkCount = 1
      AND fkc.constraint_object_id = fkAgg.OnlyFkId
      AND fkc.parent_object_id = c.object_id
      AND fkc.parent_column_id = c.column_id
) fkRef
WHERE c.object_id = @oid
ORDER BY c.column_id;";

        private const string RowEstimateSql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT ISNULL(SUM(ps.row_count), 0)
FROM sys.dm_db_partition_stats ps
WHERE ps.object_id = OBJECT_ID(@table)
  AND ps.index_id IN (0, 1);";

        public async Task<TableSchema> ReadAsync(
            SqlConnection connection,
            TableRef table,
            ProfileOptions options,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (table == null) throw new ArgumentNullException("table");
            options = options ?? new ProfileOptions();

            var schema = new TableSchema();
            string qualified = table.QualifiedName;

            using (var pc = SqlCommandFactory.Create(connection, ColumnsSql, options, cancellationToken))
            {
                var cmd = pc.Cmd;
                cmd.Parameters.Add("@table", SqlDbType.NVarChar, 512).Value = qualified;
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        schema.Columns.Add(new ColumnMeta
                        {
                            Name = reader.GetString(0),
                            ColumnId = reader.GetInt32(1),
                            TypeName = reader.GetString(2),
                            MaxLength = reader.GetInt16(3),
                            IsNullable = reader.GetBoolean(4),
                            IsIdentity = reader.GetBoolean(5),
                            IsComputed = reader.GetBoolean(6),
                            IsPrimaryKey = reader.GetInt32(7) == 1,
                            LeadingIndexName = reader.IsDBNull(8) ? null : reader.GetString(8),
                            Collation = reader.IsDBNull(9) ? null : reader.GetString(9),
                            ForeignKeyCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                            IsForeignKey = !reader.IsDBNull(10) && reader.GetInt32(10) > 0,
                            ReferencedSchema = reader.IsDBNull(11) ? null : reader.GetString(11),
                            ReferencedTable = reader.IsDBNull(12) ? null : reader.GetString(12),
                            ReferencedColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
                            ForeignKeyName = reader.IsDBNull(14) ? null : reader.GetString(14)
                        });
                    }
                }
            }

            if (schema.Columns.Count == 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "No columns readable for {0}.", qualified));

            using (var pc = SqlCommandFactory.Create(connection, RowEstimateSql, options, cancellationToken))
            {
                var cmd = pc.Cmd;
                cmd.Parameters.Add("@table", SqlDbType.NVarChar, 512).Value = qualified;
                object value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                schema.EstimatedRows = value == null || value == DBNull.Value ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            foreach (var c in schema.Columns)
            {
                if (ColumnMeta.DateCreatedTypes.Contains(c.TypeName))
                    schema.DateTimeColumns.Add(c.Name);
            }

            schema.DateCreatedColumn = ResolveDateCreated(schema.Columns, options.DateCreatedCandidates);
            return schema;
        }

        /// <summary>
        /// Walks the candidate list in order, case-insensitively, and returns the first match that
        /// is actually a date/time-typed column. Returns null when nothing matches.
        /// </summary>
        public static string ResolveDateCreated(IList<ColumnMeta> columns, IList<string> candidates)
        {
            if (columns == null || candidates == null) return null;

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                foreach (var column in columns)
                {
                    if (!string.Equals(column.Name, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ColumnMeta.DateCreatedTypes.Contains(column.TypeName)) continue;
                    return column.Name;
                }
            }

            return null;
        }
    }
}
