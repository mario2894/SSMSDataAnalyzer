using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.Model
{
    /// <summary>
    /// Which aggregates a column's type can carry.
    /// Ordered by strictness: Full &lt; NoMinMax &lt; NoDistinct &lt; MetadataOnly.
    /// <para>
    /// NoDistinct implies NoMinMax — the SQL Server types that reject the DISTINCT operator
    /// (xml, geography, geometry) reject MIN/MAX as well, but still accept COUNT_BIG and
    /// DATALENGTH. MetadataOnly means no aggregate at all is emitted (text/ntext/image,
    /// which reject even COUNT_BIG, plus CLR UDTs).
    /// </para>
    /// <para>
    /// CONTRACT Amendment 4 fixes this mapping. It was established empirically against a live
    /// SQL Server instance by attempting every aggregate over every type, not assumed. Note that
    /// varchar(max) / nvarchar(max) / varbinary(max) are Full — they support all five aggregates,
    /// which is what makes DistinctPlanner's "LOB batches of one" path meaningful.
    /// </para>
    /// </summary>
    public enum AggregateSupport { Full, NoMinMax, NoDistinct, MetadataOnly }

    public sealed class ColumnMeta
    {
        public string Name { get; set; }
        public int ColumnId { get; set; }

        /// <summary>sys.types.name</summary>
        public string TypeName { get; set; }

        /// <summary>sys.columns.max_length, -1 = MAX</summary>
        public int MaxLength { get; set; }

        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsComputed { get; set; }

        /// <summary>Name of an index where this column has key_ordinal = 1, else null.</summary>
        public string LeadingIndexName { get; set; }

        /// <summary>
        /// True when this column participates in at least one declared foreign key — including
        /// disabled and untrusted ones, which still describe a real relationship.
        /// </summary>
        public bool IsForeignKey { get; set; }

        /// <summary>
        /// How many distinct FK constraints involve this column. 0 = not a FK, 1 = exactly one
        /// (single-column or composite), n &gt; 1 = the column sits in several FKs, which is a
        /// schema smell worth seeing rather than collapsing away.
        /// </summary>
        public int ForeignKeyCount { get; set; }

        /// <summary>
        /// Referenced schema, or null only when the column participates in MORE THAN ONE FK.
        /// A composite FK keeps its schema and table: it is one constraint referencing one
        /// table, so the table is not ambiguous at all (CONTRACT Amendment 15).
        /// </summary>
        public string ReferencedSchema { get; set; }

        /// <summary>
        /// Referenced table, or null when unresolved. Raw catalog name — it may contain periods,
        /// brackets or any other character, so it must be bracket-doubled via
        /// <see cref="Sql.SqlIdentifier.Bracket"/> before it goes anywhere near SQL text.
        /// </summary>
        public string ReferencedTable { get; set; }

        /// <summary>
        /// Referenced column, or null for a composite FK and for multi-FK columns.
        /// <para>
        /// For a composite FK the catalog DOES record the exact pairing (CompA→KeyA), so this
        /// null is a deliberate semantic choice, not missing information: filtering the parent on
        /// one half of a composite key returns plausible but wrong rows, and nothing in the result
        /// would signal the error. Do not "fix" this by populating it from
        /// sys.foreign_key_columns — consumers gate the value-jump on this being non-null.
        /// </para>
        /// </summary>
        public string ReferencedColumn { get; set; }

        /// <summary>
        /// The constraint name. Populated whenever exactly one FK involves this column —
        /// composite included, since a composite FK has exactly one name. Null for multi-FK columns.
        /// </summary>
        public string ForeignKeyName { get; set; }

        /// <summary>
        /// "Is a foreign key but has no navigable table" — true only for the multi-FK case.
        /// A composite FK is NOT unresolved: it has a table to navigate to, just no single
        /// column to filter on. Gate "go to source table" on <see cref="ReferencedTable"/>,
        /// and "go to source for this value" on <see cref="ReferencedColumn"/>.
        /// </summary>
        public bool HasUnresolvedForeignKey
        {
            get { return IsForeignKey && ReferencedTable == null; }
        }

        /// <summary>"[a14ref].[Parent]" for the resolved target, else null. Bracket-doubled.</summary>
        public string ReferencedQualifiedName
        {
            get
            {
                if (ReferencedTable == null) return null;
                string schema = string.IsNullOrEmpty(ReferencedSchema) ? "dbo" : ReferencedSchema;
                return Sql.SqlIdentifier.Bracket(schema) + "." + Sql.SqlIdentifier.Bracket(ReferencedTable);
            }
        }

        /// <summary>
        /// The column's effective collation (sys.columns.collation_name); null for non-character
        /// types. Reported, never acted on: <see cref="ColumnProfile.DistinctCount"/> is whatever
        /// COUNT(DISTINCT …) returns under THIS collation, which is the number that governs the
        /// user's real queries, indexes and constraints. Surfacing it is what makes a surprising
        /// distinct count explicable instead of mysterious (CONTRACT Amendment 11).
        /// </summary>
        public string Collation { get; set; }

        /// <summary>True for the character types (blank-counting applies).</summary>
        public bool IsStringType
        {
            get
            {
                switch (Lower(TypeName))
                {
                    case "char":
                    case "varchar":
                    case "nchar":
                    case "nvarchar":
                    case "sysname":
                    case "text":
                    case "ntext":
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>MAX types / text / ntext / image / xml.</summary>
        public bool IsLob
        {
            get
            {
                switch (Lower(TypeName))
                {
                    case "text":
                    case "ntext":
                    case "image":
                    case "xml":
                        return true;
                }
                if (MaxLength == -1)
                {
                    switch (Lower(TypeName))
                    {
                        case "varchar":
                        case "nvarchar":
                        case "varbinary":
                            return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Length in characters for string types (nchar/nvarchar store 2 bytes per char);
        /// int.MaxValue for MAX types.
        /// </summary>
        public int CharLength
        {
            get
            {
                if (MaxLength == -1) return int.MaxValue;
                switch (Lower(TypeName))
                {
                    case "nchar":
                    case "nvarchar":
                    case "sysname":
                        return MaxLength / 2;
                    default:
                        return MaxLength;
                }
            }
        }

        /// <summary>
        /// Derived from the type name. The mapping below was verified empirically against a live
        /// SQL Server instance by attempting each aggregate over every type.
        /// </summary>
        public AggregateSupport Support
        {
            get
            {
                switch (Lower(TypeName))
                {
                    // Nothing works, not even COUNT_BIG(col).
                    case "text":
                    case "ntext":
                    case "image":
                        return AggregateSupport.MetadataOnly;

                    // COUNT_BIG + DATALENGTH work; MIN/MAX and COUNT(DISTINCT) are rejected.
                    case "xml":
                    case "geography":
                    case "geometry":
                        return AggregateSupport.NoDistinct;

                    // MIN/MAX rejected, everything else fine.
                    case "bit":
                        return AggregateSupport.NoMinMax;
                }

                // Unknown / CLR user-defined types: do not guess, profile metadata only.
                if (!KnownTypes.Contains(Lower(TypeName)))
                    return AggregateSupport.MetadataOnly;

                return AggregateSupport.Full;
            }
        }

        public bool SupportsMinMax { get { return Support == AggregateSupport.Full; } }

        public bool SupportsCount { get { return Support != AggregateSupport.MetadataOnly; } }
        public bool SupportsDistinct { get { return Support == AggregateSupport.Full || Support == AggregateSupport.NoMinMax; } }
        public bool SupportsDataLength { get { return Support != AggregateSupport.MetadataOnly; } }

        private static string Lower(string s)
        {
            return (s ?? string.Empty).ToLowerInvariant();
        }

        private static readonly HashSet<string> KnownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bigint","int","smallint","tinyint","bit","decimal","numeric","money","smallmoney",
            "float","real","date","datetime","datetime2","datetimeoffset","smalldatetime","time",
            "char","varchar","nchar","nvarchar","sysname","binary","varbinary","uniqueidentifier",
            "timestamp","rowversion","sql_variant","hierarchyid","text","ntext","image","xml",
            "geography","geometry"
        };

        /// <summary>The date/time types acceptable as the DateCreated column.</summary>
        public static readonly HashSet<string> DateCreatedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "date","datetime","datetime2","datetimeoffset","smalldatetime"
        };
    }
}
