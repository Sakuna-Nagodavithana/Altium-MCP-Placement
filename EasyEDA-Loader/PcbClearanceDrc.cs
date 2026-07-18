using PCB;
using SchTObjectId = SCH.TObjectId;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Geometric clearance / short-risk checker that catches copper Altium DRC often misses
    /// (e.g. same-layer power tracks with edge clearance below fab minimum).
    /// </summary>
    internal static class PcbClearanceDrc
    {
        public const double DefaultMinClearanceMils = 6.0;
        public const double PowerRailMinClearanceMils = 8.0;

        public static Dictionary<string, object> AnalyzeCurrentBoard(
            double minClearanceMils = DefaultMinClearanceMils,
            double powerMinClearanceMils = PowerRailMinClearanceMils)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document first.");

            return AnalyzeBoard(board, minClearanceMils, powerMinClearanceMils);
        }

        public static Dictionary<string, object> AnalyzeBoard(
            IPCB_Board board,
            double minClearanceMils = DefaultMinClearanceMils,
            double powerMinClearanceMils = PowerRailMinClearanceMils)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            var tracks = CollectElectricalTracks(board);
            var vias = CollectVias(board);
            var violations = new List<Dictionary<string, object>>();

            // Same-layer track pairs with different nets.
            var byLayer = tracks.GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase);
            foreach (var layerGroup in byLayer)
            {
                var list = layerGroup.ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        var a = list[i];
                        var b = list[j];
                        if (string.Equals(a.Net, b.Net, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.IsNullOrWhiteSpace(a.Net) || string.IsNullOrWhiteSpace(b.Net))
                            continue;

                        var edge = SegmentEdgeClearance(a, b);
                        var aRail = PowerRail(a.Net);
                        var bRail = PowerRail(b.Net);
                        var bothPower = aRail != null && bRail != null && aRail != bRail;
                        var limit = bothPower ? powerMinClearanceMils : minClearanceMils;
                        if (edge >= limit)
                            continue;

                        violations.Add(new Dictionary<string, object>
                        {
                            ["severity"] = bothPower ? "critical" : "warning",
                            ["kind"] = bothPower ? "power_rail_clearance" : "clearance",
                            ["layer"] = a.Layer,
                            ["netA"] = a.Net,
                            ["netB"] = b.Net,
                            ["edgeClearanceMils"] = Math.Round(edge, 2),
                            ["requiredMils"] = limit,
                            ["xMils"] = Math.Round((a.X1 + a.X2 + b.X1 + b.X2) / 4.0, 1),
                            ["yMils"] = Math.Round((a.Y1 + a.Y2 + b.Y1 + b.Y2) / 4.0, 1),
                            ["message"] = bothPower
                                ? $"{a.Net} vs {b.Net} on {a.Layer}: edge {edge:F2} mil < {limit:F1} mil (fab short risk)"
                                : $"{a.Net} vs {b.Net} on {a.Layer}: edge {edge:F2} mil < {limit:F1} mil",
                        });
                    }
                }
            }

            // Via annular proximity between different power rails.
            for (var i = 0; i < vias.Count; i++)
            {
                for (var j = i + 1; j < vias.Count; j++)
                {
                    var a = vias[i];
                    var b = vias[j];
                    if (string.Equals(a.Net, b.Net, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var aRail = PowerRail(a.Net);
                    var bRail = PowerRail(b.Net);
                    if (aRail == null || bRail == null || aRail == bRail)
                        continue;

                    var center = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                    var edge = center - a.Size / 2.0 - b.Size / 2.0;
                    if (edge >= powerMinClearanceMils)
                        continue;

                    violations.Add(new Dictionary<string, object>
                    {
                        ["severity"] = "critical",
                        ["kind"] = "via_power_clearance",
                        ["layer"] = $"{a.LowLayer}->{a.HighLayer}",
                        ["netA"] = a.Net,
                        ["netB"] = b.Net,
                        ["edgeClearanceMils"] = Math.Round(edge, 2),
                        ["requiredMils"] = powerMinClearanceMils,
                        ["xMils"] = Math.Round((a.X + b.X) / 2.0, 1),
                        ["yMils"] = Math.Round((a.Y + b.Y) / 2.0, 1),
                        ["message"] = $"Via {a.Net} vs {b.Net}: annular edge {edge:F2} mil < {powerMinClearanceMils:F1} mil",
                    });
                }
            }

            violations = violations
                .OrderBy(v => string.Equals(Safe(v, "severity"), "critical", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => Convert.ToDouble(v["edgeClearanceMils"]))
                .Take(200)
                .ToList();

            var critical = violations.Count(v => string.Equals(Safe(v, "severity"), "critical", StringComparison.OrdinalIgnoreCase));
            var warning = violations.Count - critical;

            var report = new Dictionary<string, object>
            {
                ["checkedAt"] = DateTime.UtcNow.ToString("o"),
                ["minClearanceMils"] = minClearanceMils,
                ["powerMinClearanceMils"] = powerMinClearanceMils,
                ["electricalTrackCount"] = tracks.Count,
                ["viaCount"] = vias.Count,
                ["violationCount"] = violations.Count,
                ["criticalCount"] = critical,
                ["warningCount"] = warning,
                ["pass"] = critical == 0,
                ["violations"] = violations,
                ["summary"] = critical == 0
                    ? (warning == 0
                        ? $"MCP DRC PASS — {tracks.Count} tracks, {vias.Count} vias checked."
                        : $"MCP DRC PASS with {warning} warning(s) — no critical power-rail shorts.")
                    : $"MCP DRC FAIL — {critical} critical clearance issue(s) (e.g. GND/3v3/+5 too close).",
            };

            WriteReport(report);
            return report;
        }

        public static string FormatUserMessage(Dictionary<string, object> report)
        {
            if (report == null)
                return "No DRC report.";

            var lines = new List<string> { Safe(report, "summary") };
            if (report.TryGetValue("violations", out var obj) && obj is List<Dictionary<string, object>> list)
            {
                foreach (var v in list.Take(15))
                    lines.Add(" • " + Safe(v, "message"));
                if (list.Count > 15)
                    lines.Add($" • … and {list.Count - 15} more (see Documents\\AltiumEE\\drc_clearance.json)");
            }

            lines.Add("");
            lines.Add("Report: " + DefaultReportPath);
            return string.Join(Environment.NewLine, lines);
        }

        public static string DefaultReportPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "drc_clearance.json");

        private static void WriteReport(Dictionary<string, object> report)
        {
            try
            {
                var path = DefaultReportPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch
            {
                // Non-fatal.
            }
        }

        private static List<TrackSeg> CollectElectricalTracks(IPCB_Board board)
        {
            var tracks = new List<TrackSeg>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eTrackObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Track track)
                        continue;

                    var layer = LayerName(board, track.GetState_V7Layer());
                    if (!IsCopperSignalLayer(layer))
                        continue;

                    var net = ReadNetName(track as IPCB_Primitive);
                    tracks.Add(new TrackSeg
                    {
                        Net = net,
                        Layer = layer,
                        Width = CoordUtils.CoordToMils(track.GetState_Width()),
                        X1 = CoordUtils.CoordToMils(track.GetState_X1()),
                        Y1 = CoordUtils.CoordToMils(track.GetState_Y1()),
                        X2 = CoordUtils.CoordToMils(track.GetState_X2()),
                        Y2 = CoordUtils.CoordToMils(track.GetState_Y2()),
                    });
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return tracks;
        }

        private static List<ViaInfo> CollectVias(IPCB_Board board)
        {
            var vias = new List<ViaInfo>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eViaObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Via via)
                        continue;

                    vias.Add(new ViaInfo
                    {
                        Net = ReadNetName(via as IPCB_Primitive),
                        X = CoordUtils.CoordToMils(via.GetState_XLocation()),
                        Y = CoordUtils.CoordToMils(via.GetState_YLocation()),
                        Size = CoordUtils.CoordToMils(via.GetState_Size()),
                        LowLayer = LayerName(board, via.GetState_LowLayer()),
                        HighLayer = LayerName(board, via.GetState_HighLayer()),
                    });
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return vias;
        }

        private static double SegmentEdgeClearance(TrackSeg a, TrackSeg b)
        {
            const int samples = 10;
            var best = double.MaxValue;
            for (var i = 0; i <= samples; i++)
            {
                var ua = i / (double)samples;
                var ax = a.X1 + (a.X2 - a.X1) * ua;
                var ay = a.Y1 + (a.Y2 - a.Y1) * ua;
                for (var j = 0; j <= samples; j++)
                {
                    var ub = j / (double)samples;
                    var bx = b.X1 + (b.X2 - b.X1) * ub;
                    var by = b.Y1 + (b.Y2 - b.Y1) * ub;
                    var d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                    if (d < best)
                        best = d;
                }
            }

            return best - a.Width / 2.0 - b.Width / 2.0;
        }

        internal static string PowerRail(string net)
        {
            if (string.IsNullOrWhiteSpace(net))
                return null;
            var low = net.Trim().ToLowerInvariant();
            if (low == "gnd" || low == "gndnet" || low == "dgnd" || low == "agnd" || low == "vss")
                return "GND";
            if (low.Contains("3v3") || low.Contains("3.3"))
                return "3V3";
            if (low == "+5" || low == "5v" || low == "+5v" || low == "vbus" || low == "+5v0")
                return "5V";
            if (low.Contains("vcc") || low.Contains("vdd") || low.StartsWith("+"))
                return "PWR";
            return null;
        }

        private static bool IsCopperSignalLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return false;
            var n = layer.ToLowerInvariant();
            if (n.Contains("mechanical") || n.Contains("overlay") || n.Contains("paste") ||
                n.Contains("solder") || n.Contains("keep") || n.Contains("assembly") ||
                n.Contains("courtyard") || n.Contains("dimension") || n.Contains("drill") ||
                n.Contains("3d") || n.Contains("component center") || n.StartsWith("pcb.v7_layer"))
                return false;
            return n.Contains("top") || n.Contains("bottom") || n.Contains("mid") ||
                   n.Contains("signal") || n.Contains("plane") || n.Contains("inner");
        }

        private static string LayerName(IPCB_Board board, object layer)
        {
            if (layer == null)
                return string.Empty;
            try
            {
                if (layer is IV7_Layer v7)
                {
                    var named = Safe(board.LayerName(v7));
                    if (!string.IsNullOrWhiteSpace(named) && !named.StartsWith("PCB.V7_Layer", StringComparison.OrdinalIgnoreCase))
                        return named;
                }
            }
            catch { }

            return Safe(layer.ToString());
        }

        private static string ReadNetName(IPCB_Primitive primitive)
        {
            if (primitive == null)
                return string.Empty;
            try
            {
                if (primitive.Internal_GetState_Net() is IPCB_Net net && net != null)
                    return Safe(net.GetState_Name());
            }
            catch { }

            return string.Empty;
        }

        private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

        private static string Safe(Dictionary<string, object> dict, string key) =>
            dict != null && dict.TryGetValue(key, out var v) ? Safe(v?.ToString()) : string.Empty;

        private sealed class TrackSeg
        {
            public string Net;
            public string Layer;
            public double Width;
            public double X1, Y1, X2, Y2;
        }

        private sealed class ViaInfo
        {
            public string Net;
            public double X, Y, Size;
            public string LowLayer, HighLayer;
        }
    }
}
