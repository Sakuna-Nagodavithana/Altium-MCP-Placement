using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Writes the JLCPCB-ready BOM CSV after rows have been resolved against JLCPCB.
    /// Columns match what JLCPCB's BOM upload tool ingests:
    /// Designator, Quantity, Comment/Value, LCSC Part #, Package, Basic/Extended, Stock, Unit Price, Datasheet.
    /// </summary>
    public static class BomExporter
    {
        public static string DefaultExportDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AltiumEE");

        public static string Export(IEnumerable<BomRow> rows, string outputPath = null)
        {
            var included = rows.Where(r => r.Include).OrderBy(r => r.Designator, StringComparer.OrdinalIgnoreCase).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath ?? string.Empty) ?? DefaultExportDirectory);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.Combine(
                    DefaultExportDirectory,
                    $"BOM_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            }

            var sb = new StringBuilder();
            sb.AppendLine("Designator,Quantity,Comment,LCSC Part #,Package,Library Type,Stock,Unit Price,Datasheet,Note");

            foreach (var r in included)
            {
                string lcsc = CsvSafe(r.ResolvedLcsc ?? r.OriginalLcsc ?? "");
                string libType = r.ResolvedLibraryType ?? "";
                string pkg = r.ResolvedPackage ?? r.Package ?? "";
                string desc = CsvSafe(r.ResolvedDescription ?? r.OriginalValue ?? "");
                string note = CsvSafe(r.ResolutionNote ?? "");
                string ds = CsvSafe(r.ResolvedDatasheet ?? "");

                sb.Append(CsvSafe(r.Designator ?? "")).Append(",");
                sb.Append(r.Quantity.ToString()).Append(",");
                sb.Append(desc).Append(",");
                sb.Append(lcsc).Append(",");
                sb.Append(CsvSafe(pkg)).Append(",");
                sb.Append(CsvSafe(libType)).Append(",");
                sb.Append(r.ResolvedStock.ToString()).Append(",");
                sb.Append(r.ResolvedUnitPrice.ToString("0.####")).Append(",");
                sb.Append(ds).Append(",");
                sb.AppendLine(note);
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(true));
            return outputPath;
        }

        private static string CsvSafe(string value)
        {
            if (value == null)
                return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
