using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Runs the altium-mcp/pdf_bom_ocr.py helper to extract a component list from a
    /// schematic PDF. Falls back gracefully if Python or the OCR deps aren't installed.
    /// </summary>
    public static class SchematicPdfReader
    {
        public class PdfBomResult
        {
            public string Source { get; set; }
            public int PageCount { get; set; }
            public List<PdfBomComponent> Components { get; set; } = new List<PdfBomComponent>();
            public List<string> Warnings { get; set; } = new List<string>();
            public List<string> UnmatchedLcsc { get; set; } = new List<string>();
        }

        public class PdfBomComponent
        {
            public string Designator { get; set; }
            public string Value { get; set; }
            public string Lcsc { get; set; }
            public int? Page { get; set; }
        }

        /// <summary>Extract components from a schematic PDF.</summary>
        public static PdfBomResult ExtractComponents(string pdfPath)
        {
            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("PDF not found", pdfPath);

            string scriptPath = ResolveHelperScript();
            if (!File.Exists(scriptPath))
                throw new InvalidOperationException(
                    "Could not find pdf_bom_ocr.py. Expected next to altium-mcp/server.py in the repo.");

            string pythonPath = ResolvePythonPath();
            if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
                throw new InvalidOperationException(
                    "Python was not found. Set pythonPath in mcp-settings.json or install Python 3.12/3.13.");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{pdfPath}\"",
                WorkingDirectory = Path.GetDirectoryName(scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(120000);

                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            $"pdf_bom_ocr.py exited with code {process.ExitCode}.\n{stderr}");
                    }

                    return ParseResult(stdout);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to run OCR helper: {ex.Message}\n" +
                    "Install dependencies first: pip install pdf2image pytesseract pdfplumber Pillow " +
                    "(and poppler + Tesseract on the system).", ex);
            }
        }

        private static PdfBomResult ParseResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new PdfBomResult { Warnings = { "OCR helper returned no output." } };

            try
            {
                var obj = JObject.Parse(json);
                var result = new PdfBomResult
                {
                    Source = (string)obj["source"],
                    PageCount = (int?)obj["pageCount"] ?? 0,
                };

                foreach (var c in obj["components"] as JArray ?? new JArray())
                {
                    result.Components.Add(new PdfBomComponent
                    {
                        Designator = (string)c["designator"],
                        Value = (string)c["value"],
                        Lcsc = (string)c["lcsc"],
                        Page = (int?)c["page"],
                    });
                }

                foreach (var w in obj["warnings"] as JArray ?? new JArray())
                    result.Warnings.Add((string)w);

                foreach (var u in obj["unmatched_lcsc"] as JArray ?? new JArray())
                    result.UnmatchedLcsc.Add((string)u);

                return result;
            }
            catch (Exception ex)
            {
                return new PdfBomResult { Warnings = { $"Failed to parse OCR output: {ex.Message}" } };
            }
        }

        private static string ResolveHelperScript()
        {
            var settings = McpServerManager.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.ServerScriptPath) &&
                File.Exists(settings.ServerScriptPath))
            {
                string dir = Path.GetDirectoryName(settings.ServerScriptPath);
                string candidate = Path.Combine(dir, "pdf_bom_ocr.py");
                if (File.Exists(candidate))
                    return candidate;
            }

            for (var probe = AppContext.BaseDirectory; probe != null; )
            {
                string candidate = Path.Combine(probe, "altium-mcp", "pdf_bom_ocr.py");
                if (File.Exists(candidate))
                    return candidate;
                var parent = Directory.GetParent(probe);
                if (parent == null)
                    break;
                probe = parent.FullName;
            }

            return Path.Combine(
                @"C:\Users\SAKUNA\Downloads\EasyEDALoader-main\EasyEDALoader-main",
                "altium-mcp", "pdf_bom_ocr.py");
        }

        private static string ResolvePythonPath()
        {
            var settings = McpServerManager.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.PythonPath) && File.Exists(settings.PythonPath))
                return settings.PythonPath;

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
                @"C:\Python313\python.exe",
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "python";
        }
    }
}
