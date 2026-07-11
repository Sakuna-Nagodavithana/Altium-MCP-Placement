using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Loads a BOM CSV (such as Altium's File -> Reports -> Bill of Materials export)
    /// and turns each line into a BomRow. Common Altium column names are recognized:
    /// Designator, Comment, Quantity, Footprint/Pack, JLCPCB Part / LCSC Part.
    /// </summary>
    public static class BomLoader
    {
        public static List<BomRow> LoadCsv(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("BOM file not found", path);

            var rows = new List<BomRow>();

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
                return rows;

            int headerIdx = -1;
            string[] headers = null;
            for (int i = 0; i < Math.Min(20, lines.Length); i++)
            {
                var candidate = SplitCsvLine(lines[i]);
                if (candidate.Any(h =>
                    h.IndexOf("designator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    h.IndexOf("part reference", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    headers = candidate;
                    headerIdx = i;
                    break;
                }
            }

            if (headers == null)
                return rows;

            int idxDesignator = IndexOf(headers, "designator", "part reference", "ref des", "footprint ref");
            int idxComment = IndexOf(headers, "comment", "value", "part", "description", "manufacturer part");
            int idxQuantity = IndexOf(headers, "quantity", "qty");
            int idxLcsc = IndexOf(headers, "lcsc part", "jlcpcb part #", "jlcpcb part", "jlcpcb part no", "lcsc");
            int idxFootprint = IndexOf(headers, "footprint", "pack", "package", "pattern");

            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                var cells = SplitCsvLine(lines[i]);
                if (cells.Length == 0)
                    continue;

                string designator = idxDesignator >= 0 && idxDesignator < cells.Length ? cells[idxDesignator] : "";
                if (string.IsNullOrWhiteSpace(designator))
                    continue;

                // Some BOMs group multiple designators in one cell like "R5,R6,R7"
                int qty = 1;
                if (idxQuantity >= 0 && idxQuantity < cells.Length &&
                    int.TryParse(cells[idxQuantity], out var parsedQty))
                    qty = parsedQty;

                var desigList = designator.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string comment = idxComment >= 0 && idxComment < cells.Length ? cells[idxComment] : "";
                string lcsc = idxLcsc >= 0 && idxLcsc < cells.Length ? cells[idxLcsc] : "";
                string footprint = idxFootprint >= 0 && idxFootprint < cells.Length ? cells[idxFootprint] : "";

                foreach (var d in desigList)
                {
                    var trimmed = d.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;
                    rows.Add(new BomRow
                    {
                        Designator = trimmed,
                        OriginalValue = comment,
                        OriginalLcsc = lcsc,
                        Quantity = Math.Max(1, qty > 1 ? 1 : qty),
                        Package = !string.IsNullOrWhiteSpace(footprint) ? footprint : null,
                    });
                }
            }

            return rows;
        }

        private static int IndexOf(string[] headers, params string[] names)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i].Trim();
                foreach (var n in names)
                {
                    if (h.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;
                }
            }
            return -1;
        }

        private static string[] SplitCsvLine(string line)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                        inQuotes = true;
                    else if (c == ',')
                    {
                        result.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    else
                        current.Append(c);
                }
            }

            result.Add(current.ToString().Trim());
            return result.ToArray();
        }
    }
}
