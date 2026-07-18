using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EasyEDA_Loader.Floorplan;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// One-click place pipeline matching how Altium pros + modern placers work:
    /// needs → best floorplan (score by estimated wirelength) → auto-place passives
    /// → fanout vias → rooms. Force-directed refine optional.
    /// </summary>
    internal static class SmartPlacePipeline
    {
        public static string Run(
            double spacingMils = 55,
            double maxRadiusMils = 900,
            double maxSchematicDistanceMils = 2500,
            bool optimizeAfter = false,
            bool useRecommendedFloorplan = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Smart Place Pipeline (Altium rooms + academic force/SA style)");
            sb.AppendLine("────────────────────────────────────────────");

            // 1) Needs → stackup + via sizes
            BoardNeedsReport needs = null;
            try
            {
                needs = BoardNeedsAdvisor.Analyze();
                if (needs.RecommendedStackup != null)
                    PcbStackupAdvisor.SavePreference(needs.RecommendedStackup);
                BoardNeedsAdvisor.ApplyRecommendedViaSizesToProfile(needs);
                sb.AppendLine("1. Board Needs: " + needs.SummaryLine);
            }
            catch (Exception ex)
            {
                sb.AppendLine("1. Board Needs skipped: " + ex.Message);
            }

            // 2) Floorplan — pick best of variants by estimated HPWL (like Optimal Placement Vector idea)
            if (useRecommendedFloorplan)
            {
                try
                {
                    DesignExporter.ExportForPlacementPlanning();
                    var parts = FloorplanGenerator.LoadPartsFromConnectivity();
                    var board = FloorplanGenerator.BuildAutoBoard(parts);
                    var variants = FloorplanGenerator.GenerateVariants(board, parts);
                    var best = FloorplanScorer.PickBest(variants, DesignExporter.DefaultExportPath);
                    sb.AppendLine($"2. Floorplan: '{best.Title}' (best of {variants.Count} by estimated wirelength)");
                    var floorMsg = FloorplanApplier.Apply(
                        best,
                        drawBoardOutline: true,
                        autoPlacePassivesAfter: false);
                    sb.AppendLine("   " + floorMsg.Split('\n').FirstOrDefault());
                }
                catch (Exception ex)
                {
                    sb.AppendLine("2. Floorplan skipped: " + ex.Message);
                }
            }

            // 3) Pin-accurate passives (our strength vs Altium's generic Arrange Within Room)
            try
            {
                sb.AppendLine("3. Auto-Place passives (pin-accurate)…");
                var placeMsg = IcClusterRunner.RunFullAutoPlacement(
                    spacingMils, maxRadiusMils, maxSchematicDistanceMils);
                sb.AppendLine("   " + SummarizeFirstLines(placeMsg, 3));
            }
            catch (Exception ex)
            {
                sb.AppendLine("3. Auto-Place failed: " + ex.Message);
            }

            // 4) Fanout (pros dogbone to planes before signal route)
            try
            {
                sb.AppendLine("4. Fanout decap/power vias…");
                sb.AppendLine("   " + SummarizeFirstLines(DecapFanout.FanoutDecouplingAndPowerPads(), 2));
            }
            catch (Exception ex)
            {
                sb.AppendLine("4. Fanout skipped: " + ex.Message);
            }

            // 5) Rooms (Altium Connection Room / Arrange Within Room workflow)
            try
            {
                sb.AppendLine("5. Create Rooms + Unions…");
                sb.AppendLine("   " + SummarizeFirstLines(
                    PlacementRooms.CreateRoomsFromLastPlan(alsoAnchorUnions: true), 2));
            }
            catch (Exception ex)
            {
                sb.AppendLine("5. Rooms skipped: " + ex.Message);
            }

            // 6) Optional force+SA densify (OpenROAD / academic PCB placers)
            if (optimizeAfter)
            {
                try
                {
                    sb.AppendLine("6. Optimize (force-directed + anneal)…");
                    sb.AppendLine("   " + SummarizeFirstLines(
                        IcClusterRunner.RunForceDirectedOptimize(lockIcs: true), 2));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("6. Optimize skipped: " + ex.Message);
                }
            }

            sb.AppendLine();
            sb.AppendLine("Next (human): nudge RF/antenna & connectors, pour GND, route RF→HS→PWR→Logic, Full DRC.");
            if (needs?.ThermalHotspots?.Count > 0)
                sb.AppendLine($"Thermal: {needs.ThermalHotspots.Count} hotspot(s) — open Board Needs for EPAD copper steps.");

            return sb.ToString();
        }

        private static string SummarizeFirstLines(string text, int n)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "(ok)";
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" | ", lines.Take(n));
        }
    }

    /// <summary>
    /// Scores floorplan variants like Altium's Optimal Placement Vector:
    /// prefer layouts that shorten nets between placed ICs/connectors.
    /// </summary>
    internal static class FloorplanScorer
    {
        public static FloorplanVariant PickBest(IReadOnlyList<FloorplanVariant> variants, string connectivityPath)
        {
            if (variants == null || variants.Count == 0)
                throw new InvalidOperationException("No floorplan variants.");

            var edges = LoadIcEdges(connectivityPath);
            FloorplanVariant best = variants[0];
            var bestScore = double.MaxValue;

            foreach (var v in variants)
            {
                var score = Score(v, edges);
                // Prefer RF-quiet layouts slightly when RF parts exist
                if (v.Parts.Any(p => p.Role == FloorplanRole.Rf) &&
                    v.Id != null && v.Id.IndexOf("rf", StringComparison.OrdinalIgnoreCase) >= 0)
                    score *= 0.92;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = v;
                }
            }

            // Don't mutate shared description repeatedly when re-scoring
            return best;
        }

        private static double Score(FloorplanVariant v, List<Tuple<string, string>> edges)
        {
            var pos = v.Parts.ToDictionary(
                p => p.Designator,
                p => Tuple.Create(p.TargetXMils, p.TargetYMils),
                StringComparer.OrdinalIgnoreCase);

            double hpwl = 0;
            var linked = 0;
            foreach (var e in edges)
            {
                Tuple<double, double> a, b;
                if (!pos.TryGetValue(e.Item1, out a) || !pos.TryGetValue(e.Item2, out b))
                    continue;
                hpwl += Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2);
                linked++;
            }

            // Spread penalty: connectors should be near board edge (already in layout);
            // soft penalty if RF and power centers are too close
            var rf = v.Parts.Where(p => p.Role == FloorplanRole.Rf).ToList();
            var pwr = v.Parts.Where(p => p.Role == FloorplanRole.Power).ToList();
            if (rf.Count > 0 && pwr.Count > 0)
            {
                var rcx = rf.Average(p => p.TargetXMils);
                var rcy = rf.Average(p => p.TargetYMils);
                var pcx = pwr.Average(p => p.TargetXMils);
                var pcy = pwr.Average(p => p.TargetYMils);
                var dist = Math.Abs(rcx - pcx) + Math.Abs(rcy - pcy);
                if (dist < 400)
                    hpwl += (400 - dist) * 2; // prefer RF far from switching heat/noise
            }

            if (linked == 0)
                return hpwl + 1e6; // no connectivity — still pick something
            return hpwl;
        }

        private static List<Tuple<string, string>> LoadIcEdges(string path)
        {
            var edges = new List<Tuple<string, string>>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return edges;

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var nets = root["projectNets"] as JArray ?? root["nets"] as JArray ?? new JArray();
                foreach (var net in nets.OfType<JObject>())
                {
                    var name = (net.Value<string>("name") ?? "").Trim();
                    if (Placement.PlacementConstants.IsGlobalRail(name))
                        continue;

                    var pins = net["pins"] as JArray ?? net["nodes"] as JArray;
                    if (pins == null)
                        continue;

                    var dess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pin in pins)
                    {
                        string des = null;
                        if (pin is JObject po)
                            des = po.Value<string>("designator") ?? po.Value<string>("comp");
                        else
                            des = pin.ToString();
                        if (string.IsNullOrWhiteSpace(des))
                            continue;
                        var dash = des.IndexOf('-');
                        if (dash > 0)
                            des = des.Substring(0, dash);
                        des = des.Trim();
                        if (Placement.PlacementConstants.IcDesignatorPattern.IsMatch(des) ||
                            des.StartsWith("J", StringComparison.OrdinalIgnoreCase) ||
                            des.StartsWith("P", StringComparison.OrdinalIgnoreCase) ||
                            des.StartsWith("U", StringComparison.OrdinalIgnoreCase) ||
                            des.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                        {
                            dess.Add(des);
                        }
                    }

                    var list = dess.ToList();
                    for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                        edges.Add(Tuple.Create(list[i], list[j]));
                }
            }
            catch { }

            return edges;
        }
    }
}
