using System.Collections.Generic;
using System.Text;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Minimal CSV reader - handles quoted fields (including embedded commas
    /// and escaped "" quotes) without pulling in a third-party dependency
    /// for what is a small, well-understood parsing task.
    /// </summary>
    public static class CsvParser
    {
        public static List<string[]> Parse(string content)
        {
            var rows = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (i < content.Length)
            {
                char c = content[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        inQuotes = false;
                        i++;
                        continue;
                    }
                    field.Append(c);
                    i++;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        i++;
                        break;
                    case ',':
                        fields.Add(field.ToString());
                        field.Clear();
                        i++;
                        break;
                    case '\r':
                        i++;
                        break;
                    case '\n':
                        fields.Add(field.ToString());
                        field.Clear();
                        if (fields.Count > 1 || !string.IsNullOrWhiteSpace(fields[0]))
                        {
                            rows.Add(fields.ToArray());
                        }
                        fields = new List<string>();
                        i++;
                        break;
                    default:
                        field.Append(c);
                        i++;
                        break;
                }
            }

            // Last field/row if the file doesn't end with a newline
            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                if (fields.Count > 1 || !string.IsNullOrWhiteSpace(fields[0]))
                {
                    rows.Add(fields.ToArray());
                }
            }

            return rows;
        }
    }
}
