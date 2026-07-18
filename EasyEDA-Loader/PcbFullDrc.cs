using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace EasyEDA_Loader
{
    internal sealed class DrcIssue
    {
        public string Severity { get; set; } = "warning";
        public string Source { get; set; } = "MCP";
        public string Kind { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string NetA { get; set; } = string.Empty;
        public string NetB { get; set; } = string.Empty;
        public string Layer { get; set; } = string.Empty;
        public double XMils { get; set; }
        public double YMils { get; set; }
        public double ValueMils { get; set; }
        public double RequiredMils { get; set; }
        public object Primitive1 { get; set; }
        public object Primitive2 { get; set; }
    }

    /// <summary>
    /// Full pre-fab DRC: runs Altium's native batch design-rule check, then adds
    /// geometric checks Altium often under-reports (power-rail clearance, pad-vs-track,
    /// fab min width / neckdown).
    /// </summary>
    internal static class PcbFullDrc
    {
        public const double FabMinTrackWidthMils = 3.5;
        public const double FabMinClearanceMils = 5.0;
        public const double PowerRailMinClearanceMils = 8.0;
        public const double PadTrackMinClearanceMils = 6.0;

        /// <summary>Fab house min track width from selected JLCPCB preset (falls back to 3.5).</summary>
        public static double ActiveFabMinTrackWidthMils
        {
            get
            {
                try
                {
                    var p = PcbStackupAdvisor.LoadPreference();
                    return p.MinTraceMils > 0 ? p.MinTraceMils : FabMinTrackWidthMils;
                }
                catch { return FabMinTrackWidthMils; }
            }
        }

        public static double ActiveFabMinClearanceMils
        {
            get
            {
                try
                {
                    var p = PcbStackupAdvisor.LoadPreference();
                    return p.MinClearanceMils > 0 ? p.MinClearanceMils : FabMinClearanceMils;
                }
                catch { return FabMinClearanceMils; }
            }
        }

        public static string DefaultReportPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "drc_full_report.json");

        public static string DefaultAltiumDrcTextPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "altium_batch_drc.txt");

        public static Dictionary<string, object> RunFullCheck(bool runAltiumBatch = true)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document first.");

            var issues = new List<DrcIssue>();

            if (runAltiumBatch)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DefaultAltiumDrcTextPath) ?? ".");
                    // eDRC_Text = 1 typically; use enum if available.
                    var format = 1;
                    try
                    {
                        format = Convert.ToInt32(Enum.Parse(typeof(TDRCReportFileFormat), "eDRC_Text"));
                    }
                    catch { }

                    board.RunBatchDesignRuleCheck(DefaultAltiumDrcTextPath, format, false, false);
                    issues.AddRange(CollectAltiumViolations(board));
                }
                catch (Exception ex)
                {
                    issues.Add(new DrcIssue
                    {
                        Severity = "warning",
                        Source = "Altium",
                        Kind = "batch_drc_error",
                        Message = "Altium batch DRC failed to run: " + ex.Message,
                    });
                }
            }
            else
            {
                issues.AddRange(CollectAltiumViolations(board));
            }

            issues.AddRange(PcbClearanceDrcExtras.FindPowerRailClearances(board));
            issues.AddRange(PcbClearanceDrcExtras.FindPadTrackCrossNetHits(board));
            issues.AddRange(PcbClearanceDrcExtras.FindFabWidthAndNeckdown(board));
            issues.AddRange(PcbClearanceDrcExtras.FindViaPadConflicts(board));

            // De-dupe similar messages near the same coordinate.
            issues = Deduplicate(issues);

            var critical = issues.Count(i => string.Equals(i.Severity, "critical", StringComparison.OrdinalIgnoreCase));
            var error = issues.Count(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase));
            var warning = issues.Count - critical - error;
            var pass = critical == 0 && error == 0;

            var report = new Dictionary<string, object>
            {
                ["checkedAt"] = DateTime.UtcNow.ToString("o"),
                ["pass"] = pass,
                ["criticalCount"] = critical,
                ["errorCount"] = error,
                ["warningCount"] = warning,
                ["issueCount"] = issues.Count,
                ["altiumViolationCount"] = issues.Count(i => i.Source == "Altium"),
                ["mcpExtraCount"] = issues.Count(i => i.Source == "MCP"),
                ["summary"] = pass
                    ? (warning == 0
                        ? $"FULL DRC PASS — {issues.Count} issues (none)."
                        : $"FULL DRC PASS with {warning} warning(s).")
                    : $"FULL DRC FAIL — {critical} critical, {error} error(s), {warning} warning(s).",
                ["issues"] = issues.Select(ToDict).ToList(),
                ["altiumReportPath"] = DefaultAltiumDrcTextPath,
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultReportPath) ?? ".");
                File.WriteAllText(DefaultReportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch { }

            // Keep legacy clearance report in sync for MCP tools.
            try
            {
                File.WriteAllText(
                    PcbClearanceDrc.DefaultReportPath,
                    JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch { }

            report["_issues"] = issues; // for UI (not serialized above as objects)
            return report;
        }

        public static List<DrcIssue> GetIssuesFromReport(Dictionary<string, object> report)
        {
            if (report != null && report.TryGetValue("_issues", out var raw) && raw is List<DrcIssue> list)
                return list;
            return new List<DrcIssue>();
        }

        public static string FormatUserMessage(Dictionary<string, object> report)
        {
            if (report == null)
                return "No DRC report.";

            var lines = new List<string> { Safe(report.TryGetValue("summary", out var s) ? s?.ToString() : null) };
            if (report.TryGetValue("issues", out var obj) && obj is System.Collections.IEnumerable enumerable)
            {
                var shown = 0;
                foreach (var item in enumerable)
                {
                    if (shown >= 15)
                        break;
                    string msg = null;
                    if (item is Dictionary<string, object> d)
                        msg = Safe(d.TryGetValue("message", out var m) ? m?.ToString() : null);
                    else if (item is DrcIssue issue)
                        msg = Safe(issue.Message);
                    else
                        msg = Safe(item?.ToString());
                    if (!string.IsNullOrEmpty(msg))
                    {
                        lines.Add(" • " + msg);
                        shown++;
                    }
                }

                var total = report.TryGetValue("issueCount", out var ic) ? Convert.ToInt32(ic) : shown;
                if (total > shown)
                    lines.Add($" • … and {total - shown} more (open Full PCB DRC window)");
            }

            lines.Add("");
            lines.Add("Report: " + DefaultReportPath);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Run full DRC and open the interactive error list (Jump / Re-Run).</summary>
        public static Dictionary<string, object> RunAndShowResults(System.Windows.Window owner = null)
        {
            var report = RunFullCheck(runAltiumBatch: true);
            var issues = GetIssuesFromReport(report);
            var win = new DrcResultsWindow(report, issues);
            if (owner != null)
                win.Owner = owner;
            win.Show();
            return report;
        }

        public static void JumpToIssue(DrcIssue issue)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null || issue == null)
                return;

            try
            {
                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                if (issue.Primitive1 != null)
                    board.SelectedObjects_Add(issue.Primitive1);
                if (issue.Primitive2 != null)
                    board.SelectedObjects_Add(issue.Primitive2);
                board.SelectedObjects_EndUpdate();
            }
            catch { }

            try
            {
                var x = AltiumApi.MilsToCoord(issue.XMils);
                var y = AltiumApi.MilsToCoord(issue.YMils);
                var pad = AltiumApi.MilsToCoord(80);
                board.GraphicalView_ZoomOnRect(x - pad, y - pad, x + pad, y + pad);
                board.GraphicalView_ZoomRedraw();
                board.ViewManager_FullUpdate();
            }
            catch
            {
                // Zoom may fail on some builds; selection still helps.
            }
        }

        private static List<DrcIssue> CollectAltiumViolations(IPCB_Board board)
        {
            var list = new List<DrcIssue>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eViolationObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Violation violation)
                        continue;

                    var desc = Safe(violation.GetState_Description());
                    if (string.IsNullOrWhiteSpace(desc))
                        desc = Safe(violation.GetState_ShortDescriptorString());
                    if (string.IsNullOrWhiteSpace(desc))
                        desc = Safe(violation.GetState_Name());

                    object ruleObj = null;
                    object p1 = null;
                    object p2 = null;
                    try { ruleObj = violation.Internal_GetState_Rule(); } catch { }
                    try { p1 = violation.Internal_GetState_Primitive1(); } catch { }
                    try { p2 = violation.Internal_GetState_Primitive2(); } catch { }

                    var ruleName = string.Empty;
                    try
                    {
                        if (ruleObj is IPCB_Rule rule)
                            ruleName = Safe(rule.GetState_Name());
                    }
                    catch { }

                    GetPrimitivePoint(board, p1, out var x, out var y, out var layer, out var netA);
                    if (x == 0 && y == 0)
                        GetPrimitivePoint(board, p2, out x, out y, out layer, out _);
                    GetPrimitivePoint(board, p2, out _, out _, out _, out var netB);

                    var severity = ClassifyAltiumSeverity(desc, ruleName);

                    list.Add(new DrcIssue
                    {
                        Severity = severity,
                        Source = "Altium",
                        Kind = "altium_violation",
                        Message = string.IsNullOrWhiteSpace(ruleName) ? desc : $"{ruleName}: {desc}",
                        RuleName = ruleName,
                        NetA = netA,
                        NetB = netB,
                        Layer = layer,
                        XMils = x,
                        YMils = y,
                        Primitive1 = p1,
                        Primitive2 = p2,
                    });
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return list;
        }

        private static string ClassifyAltiumSeverity(string desc, string ruleName)
        {
            var t = (desc + " " + ruleName).ToLowerInvariant();
            if (t.Contains("short") || t.Contains("clearance") && (t.Contains("gnd") || t.Contains("power") || t.Contains("vcc") || t.Contains("3v3") || t.Contains("+5")))
                return "critical";
            if (t.Contains("short circuit") || t.Contains("un-routed") || t.Contains("broken net") || t.Contains("net antennae"))
                return "error";
            if (t.Contains("width") || t.Contains("hole") || t.Contains("silk") || t.Contains("mask") || t.Contains("clearance"))
                return "error";
            return "warning";
        }

        internal static void GetPrimitivePoint(
            IPCB_Board board,
            object primitive,
            out double xMils,
            out double yMils,
            out string layer,
            out string net)
        {
            xMils = 0;
            yMils = 0;
            layer = string.Empty;
            net = string.Empty;
            if (primitive == null)
                return;

            try
            {
                if (primitive is IPCB_Primitive prim && prim.Internal_GetState_Net() is IPCB_Net n)
                    net = Safe(n.GetState_Name());
            }
            catch { }

            try
            {
                if (primitive is IPCB_Track track)
                {
                    xMils = (CoordUtils.CoordToMils(track.GetState_X1()) + CoordUtils.CoordToMils(track.GetState_X2())) / 2.0;
                    yMils = (CoordUtils.CoordToMils(track.GetState_Y1()) + CoordUtils.CoordToMils(track.GetState_Y2())) / 2.0;
                    layer = LayerName(board, track.GetState_V7Layer());
                    return;
                }
            }
            catch { }

            try
            {
                if (primitive is IPCB_Via via)
                {
                    xMils = CoordUtils.CoordToMils(via.GetState_XLocation());
                    yMils = CoordUtils.CoordToMils(via.GetState_YLocation());
                    layer = $"{LayerName(board, via.GetState_LowLayer())}->{LayerName(board, via.GetState_HighLayer())}";
                    return;
                }
            }
            catch { }

            try
            {
                if (primitive is IPCB_Pad pad)
                {
                    xMils = CoordUtils.CoordToMils(pad.GetState_XLocation());
                    yMils = CoordUtils.CoordToMils(pad.GetState_YLocation());
                    layer = "Pad";
                    return;
                }
            }
            catch { }

            // Bounding rectangle fallback via reflection.
            try
            {
                var method = primitive.GetType().GetMethod("Internal_BoundingRectangle")
                    ?? primitive.GetType().GetMethod("BoundingRectangle");
                var rect = method?.Invoke(primitive, null);
                if (rect != null)
                {
                    var left = InvokeCoord(rect, "GetLeft", "Left");
                    var right = InvokeCoord(rect, "GetRight", "Right");
                    var top = InvokeCoord(rect, "GetTop", "Top");
                    var bottom = InvokeCoord(rect, "GetBottom", "Bottom");
                    if (left != null && right != null && top != null && bottom != null)
                    {
                        xMils = (CoordUtils.CoordToMils(left.Value) + CoordUtils.CoordToMils(right.Value)) / 2.0;
                        yMils = (CoordUtils.CoordToMils(top.Value) + CoordUtils.CoordToMils(bottom.Value)) / 2.0;
                    }
                }
            }
            catch { }
        }

        private static int? InvokeCoord(object target, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    var m = target.GetType().GetMethod(name);
                    if (m != null)
                        return Convert.ToInt32(m.Invoke(target, null));
                    var p = target.GetType().GetProperty(name);
                    if (p != null)
                        return Convert.ToInt32(p.GetValue(target));
                }
                catch { }
            }
            return null;
        }

        private static string LayerName(IPCB_Board board, object layer)
        {
            if (layer == null) return string.Empty;
            try
            {
                if (layer is IV7_Layer v7)
                {
                    var named = Safe(board.LayerName(v7));
                    if (!string.IsNullOrWhiteSpace(named))
                        return named;
                }
            }
            catch { }
            return Safe(layer.ToString());
        }

        private static List<DrcIssue> Deduplicate(List<DrcIssue> issues)
        {
            var result = new List<DrcIssue>();
            foreach (var issue in issues.OrderBy(i => SeverityRank(i.Severity)).ThenBy(i => i.Message))
            {
                var dup = result.Any(e =>
                    string.Equals(e.Message, issue.Message, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(e.XMils - issue.XMils) < 5 &&
                    Math.Abs(e.YMils - issue.YMils) < 5);
                if (!dup)
                    result.Add(issue);
            }
            return result;
        }

        private static int SeverityRank(string s)
        {
            if (string.Equals(s, "critical", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        private static Dictionary<string, object> ToDict(DrcIssue i) => new Dictionary<string, object>
        {
            ["severity"] = i.Severity,
            ["source"] = i.Source,
            ["kind"] = i.Kind,
            ["message"] = i.Message,
            ["ruleName"] = i.RuleName,
            ["netA"] = i.NetA,
            ["netB"] = i.NetB,
            ["layer"] = i.Layer,
            ["xMils"] = Math.Round(i.XMils, 1),
            ["yMils"] = Math.Round(i.YMils, 1),
            ["valueMils"] = Math.Round(i.ValueMils, 2),
            ["requiredMils"] = Math.Round(i.RequiredMils, 2),
        };

        private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
    }

    /// <summary>Extra geometric checks beyond Altium markers.</summary>
    internal static class PcbClearanceDrcExtras
    {
        public static List<DrcIssue> FindPowerRailClearances(IPCB_Board board)
        {
            // Reuse PcbClearanceDrc analysis and map to DrcIssue.
            var report = PcbClearanceDrc.AnalyzeBoard(board);
            var issues = new List<DrcIssue>();
            if (!(report.TryGetValue("violations", out var obj) && obj is List<Dictionary<string, object>> list))
                return issues;

            foreach (var v in list)
            {
                issues.Add(new DrcIssue
                {
                    Severity = Safe(v, "severity") == "critical" ? "critical" : "warning",
                    Source = "MCP",
                    Kind = Safe(v, "kind"),
                    Message = Safe(v, "message"),
                    NetA = Safe(v, "netA"),
                    NetB = Safe(v, "netB"),
                    Layer = Safe(v, "layer"),
                    XMils = ToDouble(v, "xMils"),
                    YMils = ToDouble(v, "yMils"),
                    ValueMils = ToDouble(v, "edgeClearanceMils"),
                    RequiredMils = ToDouble(v, "requiredMils"),
                });
            }
            return issues;
        }

        public static List<DrcIssue> FindPadTrackCrossNetHits(IPCB_Board board)
        {
            var issues = new List<DrcIssue>();
            var pads = CollectPads(board);
            var tracks = CollectCopperTracks(board);

            foreach (var pad in pads)
            {
                if (string.IsNullOrWhiteSpace(pad.Net))
                    continue;
                var padR = Math.Max(pad.Width, pad.Height) / 2.0;
                foreach (var track in tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.Net))
                        continue;
                    if (string.Equals(pad.Net, track.Net, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Same physical copper layer or through-pad vs any signal layer under EPAD.
                    var sameLayer = string.Equals(pad.Layer, track.Layer, StringComparison.OrdinalIgnoreCase);
                    var epadVsBottom = pad.Width >= 60 && pad.Height >= 60 &&
                                      track.Layer.IndexOf("Bottom", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!sameLayer && !epadVsBottom)
                        continue;

                    var d = PointToSegment(pad.X, pad.Y, track) - track.Width / 2.0 - padR;
                    var limit = PcbFullDrc.PadTrackMinClearanceMils;
                    if (d >= limit)
                        continue;

                    var bothPower = PcbClearanceDrc.PowerRail(pad.Net) != null &&
                                    PcbClearanceDrc.PowerRail(track.Net) != null &&
                                    PcbClearanceDrc.PowerRail(pad.Net) != PcbClearanceDrc.PowerRail(track.Net);

                    issues.Add(new DrcIssue
                    {
                        Severity = bothPower || d < 0 ? "critical" : "error",
                        Source = "MCP",
                        Kind = "pad_track_clearance",
                        Message = d < 0
                            ? $"SHORT RISK: pad {pad.Designator}.{pad.Name} ({pad.Net}) overlaps track {track.Net} on {track.Layer} (edge {d:F1} mil)"
                            : $"Pad {pad.Designator}.{pad.Name} ({pad.Net}) near track {track.Net} on {track.Layer}: edge {d:F1} mil < {limit:F0} mil",
                        NetA = pad.Net,
                        NetB = track.Net,
                        Layer = track.Layer,
                        XMils = pad.X,
                        YMils = pad.Y,
                        ValueMils = d,
                        RequiredMils = limit,
                        Primitive1 = pad.Primitive,
                        Primitive2 = track.Primitive,
                    });
                }
            }

            return issues;
        }

        public static List<DrcIssue> FindFabWidthAndNeckdown(IPCB_Board board)
        {
            var issues = new List<DrcIssue>();
            var tracks = CollectCopperTracks(board);
            var byNet = tracks.GroupBy(t => t.Net, StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                if (track.Width + 0.01 < PcbFullDrc.ActiveFabMinTrackWidthMils)
                {
                    issues.Add(new DrcIssue
                    {
                        Severity = "error",
                        Source = "MCP",
                        Kind = "fab_min_width",
                        Message = $"Track {track.Net} on {track.Layer} width {track.Width:F2} mil < fab min {PcbFullDrc.ActiveFabMinTrackWidthMils:F1} mil",
                        NetA = track.Net,
                        Layer = track.Layer,
                        XMils = (track.X1 + track.X2) / 2,
                        YMils = (track.Y1 + track.Y2) / 2,
                        ValueMils = track.Width,
                        RequiredMils = PcbFullDrc.ActiveFabMinTrackWidthMils,
                        Primitive1 = track.Primitive,
                    });
                }
            }

            // Neckdown: along a net, a segment much thinner than the net's typical width.
            foreach (var group in byNet)
            {
                var list = group.Where(t => !string.IsNullOrWhiteSpace(t.Net)).ToList();
                if (list.Count < 2)
                    continue;
                var widths = list.Select(t => t.Width).OrderBy(w => w).ToList();
                var median = widths[widths.Count / 2];
                if (median < 8)
                    continue;

                foreach (var track in list)
                {
                    // Intentional neckdown is OK if >= fab min, but flag aggressive thin spots.
                    if (track.Width <= median * 0.45 && track.Width + 0.01 < median - 4)
                    {
                        var len = Math.Sqrt(
                            (track.X2 - track.X1) * (track.X2 - track.X1) +
                            (track.Y2 - track.Y1) * (track.Y2 - track.Y1));
                        issues.Add(new DrcIssue
                        {
                            Severity = track.Width < PcbFullDrc.ActiveFabMinTrackWidthMils ? "error" : "warning",
                            Source = "MCP",
                            Kind = "neckdown",
                            Message = $"Neckdown on {track.Net} ({track.Layer}): {track.Width:F1} mil vs typical {median:F1} mil, length {len:F0} mil",
                            NetA = track.Net,
                            Layer = track.Layer,
                            XMils = (track.X1 + track.X2) / 2,
                            YMils = (track.Y1 + track.Y2) / 2,
                            ValueMils = track.Width,
                            RequiredMils = median,
                            Primitive1 = track.Primitive,
                        });
                    }
                }
            }

            return issues;
        }

        public static List<DrcIssue> FindViaPadConflicts(IPCB_Board board)
        {
            var issues = new List<DrcIssue>();
            var pads = CollectPads(board);
            var vias = CollectVias(board);

            foreach (var via in vias)
            {
                if (string.IsNullOrWhiteSpace(via.Net))
                    continue;
                foreach (var pad in pads)
                {
                    if (string.IsNullOrWhiteSpace(pad.Net))
                        continue;
                    if (string.Equals(via.Net, pad.Net, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var d = Math.Sqrt((via.X - pad.X) * (via.X - pad.X) + (via.Y - pad.Y) * (via.Y - pad.Y));
                    var edge = d - via.Size / 2.0 - Math.Max(pad.Width, pad.Height) / 2.0;
                    if (edge >= PcbFullDrc.PadTrackMinClearanceMils)
                        continue;

                    var bothPower = PcbClearanceDrc.PowerRail(via.Net) != null &&
                                    PcbClearanceDrc.PowerRail(pad.Net) != null &&
                                    PcbClearanceDrc.PowerRail(via.Net) != PcbClearanceDrc.PowerRail(pad.Net);
                    if (!bothPower && edge >= 0)
                        continue;

                    issues.Add(new DrcIssue
                    {
                        Severity = edge < 0 || bothPower ? "critical" : "error",
                        Source = "MCP",
                        Kind = "via_pad_clearance",
                        Message = $"Via {via.Net} near pad {pad.Designator}.{pad.Name} ({pad.Net}): edge {edge:F1} mil",
                        NetA = via.Net,
                        NetB = pad.Net,
                        Layer = via.Span,
                        XMils = via.X,
                        YMils = via.Y,
                        ValueMils = edge,
                        RequiredMils = PcbFullDrc.PadTrackMinClearanceMils,
                        Primitive1 = via.Primitive,
                        Primitive2 = pad.Primitive,
                    });
                }
            }

            return issues;
        }

        private static List<PadInfo> CollectPads(IPCB_Board board)
        {
            var pads = new List<PadInfo>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Component component)
                        continue;
                    var des = Safe(component.GetState_SourceDesignator());
                    object gItObj = null;
                    try
                    {
                        gItObj = component.Internal_GroupIterator_Create();
                        var gIt = (IPCB_AbstractIterator)gItObj;
                        gIt.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.ePadObject));
                        for (var pObj = gIt.FirstPCBObject(); pObj != null; pObj = gIt.NextPCBObject())
                        {
                            if (pObj is not IPCB_Pad pad)
                                continue;
                            var net = string.Empty;
                            try
                            {
                                if ((pad as IPCB_Primitive)?.Internal_GetState_Net() is IPCB_Net n)
                                    net = Safe(n.GetState_Name());
                            }
                            catch { }

                            pads.Add(new PadInfo
                            {
                                Designator = des,
                                Name = Safe(pad.GetState_Name()),
                                Net = net,
                                X = CoordUtils.CoordToMils(pad.GetState_XLocation()),
                                Y = CoordUtils.CoordToMils(pad.GetState_YLocation()),
                                Width = CoordUtils.CoordToMils(pad.GetState_TopXSize()),
                                Height = CoordUtils.CoordToMils(pad.GetState_TopYSize()),
                                Layer = component.GetState_FlippedOnLayer() ? "Bottom Layer" : "Top Layer",
                                Primitive = pad,
                            });
                        }
                    }
                    finally
                    {
                        if (gItObj != null)
                        {
                            try { component.GroupIterator_Destroy(ref gItObj); } catch { }
                        }
                    }
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return pads;
        }

        private static List<TrackInfo> CollectCopperTracks(IPCB_Board board)
        {
            var tracks = new List<TrackInfo>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eTrackObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Track track)
                        continue;
                    var layer = string.Empty;
                    try { layer = Safe(board.LayerName(track.GetState_V7Layer())); }
                    catch { layer = Safe(track.GetState_V7Layer()?.ToString()); }
                    var low = layer.ToLowerInvariant();
                    if (low.Contains("mechanical") || low.Contains("courtyard") || low.Contains("assembly") ||
                        low.Contains("overlay") || low.Contains("component center") || low.Contains("keep"))
                        continue;

                    var net = string.Empty;
                    try
                    {
                        if ((track as IPCB_Primitive)?.Internal_GetState_Net() is IPCB_Net n)
                            net = Safe(n.GetState_Name());
                    }
                    catch { }

                    tracks.Add(new TrackInfo
                    {
                        Net = net,
                        Layer = layer,
                        Width = CoordUtils.CoordToMils(track.GetState_Width()),
                        X1 = CoordUtils.CoordToMils(track.GetState_X1()),
                        Y1 = CoordUtils.CoordToMils(track.GetState_Y1()),
                        X2 = CoordUtils.CoordToMils(track.GetState_X2()),
                        Y2 = CoordUtils.CoordToMils(track.GetState_Y2()),
                        Primitive = track,
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
                    var net = string.Empty;
                    try
                    {
                        if ((via as IPCB_Primitive)?.Internal_GetState_Net() is IPCB_Net n)
                            net = Safe(n.GetState_Name());
                    }
                    catch { }

                    vias.Add(new ViaInfo
                    {
                        Net = net,
                        X = CoordUtils.CoordToMils(via.GetState_XLocation()),
                        Y = CoordUtils.CoordToMils(via.GetState_YLocation()),
                        Size = CoordUtils.CoordToMils(via.GetState_Size()),
                        Span = "Via",
                        Primitive = via,
                    });
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return vias;
        }

        private static double PointToSegment(double px, double py, TrackInfo t)
        {
            var dx = t.X2 - t.X1;
            var dy = t.Y2 - t.Y1;
            var len2 = dx * dx + dy * dy;
            if (len2 < 1e-9)
                return Math.Sqrt((px - t.X1) * (px - t.X1) + (py - t.Y1) * (py - t.Y1));
            var u = Math.Max(0, Math.Min(1, ((px - t.X1) * dx + (py - t.Y1) * dy) / len2));
            var cx = t.X1 + u * dx;
            var cy = t.Y1 + u * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
        private static string Safe(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) ? Safe(v?.ToString()) : string.Empty;
        private static double ToDouble(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && double.TryParse(v?.ToString(), out var n) ? n : 0;

        private sealed class PadInfo
        {
            public string Designator, Name, Net, Layer;
            public double X, Y, Width, Height;
            public object Primitive;
        }

        private sealed class TrackInfo
        {
            public string Net, Layer;
            public double Width, X1, Y1, X2, Y2;
            public object Primitive;
        }

        private sealed class ViaInfo
        {
            public string Net, Span;
            public double X, Y, Size;
            public object Primitive;
        }
    }
}
