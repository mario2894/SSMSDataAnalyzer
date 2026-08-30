using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Core.Model
{
    /// <summary>Identifies one table to profile.</summary>
    public sealed class TableRef
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }

        /// <summary>"[dbo].[Orders]" — every identifier bracket-doubled.</summary>
        public string QualifiedName
        {
            get
            {
                var schema = string.IsNullOrEmpty(Schema) ? "dbo" : Schema;
                return SqlIdentifier.Bracket(schema) + "." + SqlIdentifier.Bracket(Name);
            }
        }

        public override string ToString() { return QualifiedName; }

        /// <summary>Parses "dbo.Orders", "[dbo].[Orders]" or "Orders" (schema defaults to dbo).</summary>
        public static TableRef Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new System.ArgumentException("Table name is required.", "text");

            string schema = "dbo";
            string name = text.Trim();

            // Split on the first '.' that is not inside brackets.
            int depth = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch == '[') depth++;
                else if (ch == ']') depth--;
                else if (ch == '.' && depth == 0)
                {
                    schema = name.Substring(0, i);
                    name = name.Substring(i + 1);
                    break;
                }
            }

            return new TableRef { Schema = Unbracket(schema), Name = Unbracket(name) };
        }

        private static string Unbracket(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length >= 2 && s[0] == '[' && s[s.Length - 1] == ']')
                s = s.Substring(1, s.Length - 2).Replace("]]", "]");
            return s;
        }
    }
}
