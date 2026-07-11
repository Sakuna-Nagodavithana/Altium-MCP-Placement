using System;
using System.Collections.Generic;
using System.Linq;
using EasyEDA_Loader.Placement;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Two-phase PCB placement optimizer (research-backed):
    ///   1) Force-directed: HPWL springs + overlap repulsion (relative layout)
    ///   2) Simulated annealing: translate / rotate-90 / local swap (refinement)
    ///
    /// Rotation is first-class: pin offsets and bounding boxes update on each
    /// 90° turn so wirelength and packing stay consistent. Spacing uses a
    /// middle-ground target (default ~28 mil) — hard overlap is forbidden,
    /// soft penalties discourage both crushing and sparse packing.
    ///
    /// Inspired by OpenROAD SA-PCB / academic PCB placers that combine
    /// analytical global placement with annealing + discrete rotation.
    /// </summary>
    public static class ForceDirectedOptimizer
    {
        // Middle-ground assembly/routing clearance (mil).
        // ~10–20 mil is minimum assembly; ~40+ tends to look sparse.
        public const double DefaultSpacingMils = 28.0;

        public static JObject OptimizeBoard(
            JObject connectivity,
            int iterations = 220,
            double spacingMils = DefaultSpacingMils,
            double gridMils = 10.0,
            bool lockIcs = true,
            int annealSteps = 90,
            int annealTrials = 28)
        {
            var result = new JObject
            {
                ["found"] = false,
                ["optimizer"] = "force_directed_sa",
                ["iterations"] = iterations,
                ["anneal_steps"] = annealSteps,
                ["spacing_mils"] = spacingMils,
                ["grid_mils"] = gridMils,
            };

            if (connectivity == null)
            {
                result["error"] = "Connectivity export is null.";
                return result;
            }

            var pcbComponents = PlacementConstants.PcbComponentIndex(connectivity);
            if (pcbComponents.Count == 0)
            {
                result["error"] = "No PCB components in connectivity export. Open the PCB and re-export.";
                return result;
            }

            var states = new Dictionary<string, OptState>(StringComparer.OrdinalIgnoreCase);
            var initial = new Dictionary<string, Tuple<double, double, double>>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in pcbComponents)
            {
                var des = pair.Key;
                var comp = pair.Value;
                var xy = PlacementConstants.PlacementXy(comp["placement"]);
                if (xy == null)
                    continue;

                var half = PlacementLayout.GetBboxHalfSize(comp) ?? Tuple.Create(40.0, 40.0);
                var halfW = Math.Max(12.0, half.Item1);
                var halfH = Math.Max(12.0, half.Item2);
                var layer = NormalizeLayer(
                    PlacementConstants.JsonStr(comp["layer"]) ??
                    PlacementConstants.JsonStr(comp["placement"]?["layer"]));
                var rot = GetDouble(comp["placement"] as JObject, "rotation", 0.0);
                var locked = ShouldLock(des, lockIcs);

                states[des] = new OptState
                {
                    Designator = des,
                    X = xy.Item1,
                    Y = xy.Item2,
                    HalfWidth = halfW,
                    HalfHeight = halfH,
                    Layer = layer,
                    Rotation = NormalizeRotation(rot),
                    Locked = locked,
                    Component = comp,
                };
                initial[des] = Tuple.Create(xy.Item1, xy.Item2, NormalizeRotation(rot));
            }

            if (states.Count == 0)
            {
                result["error"] = "No placeable components with coordinates.";
                return result;
            }

            var pinsByNet = BuildPinsByNetFromComponents(states);
            double costBefore = ComputeCost(states, pinsByNet, spacingMils);

            // Phase 1 — force-directed relative placement
            RunForceDirected(states, pinsByNet, iterations, spacingMils);

            // Soft legalization before annealing
            ResolveOverlaps(states, spacingMils * 0.85);

            // Phase 2 — simulated annealing with XY + 90° rotation
            RunSimulatedAnnealing(states, pinsByNet, spacingMils, annealSteps, annealTrials);

            // Final hard legalization + grid snap
            ResolveOverlaps(states, spacingMils);
            SnapToGrid(states, gridMils);
            ResolveOverlaps(states, spacingMils);

            double costAfter = ComputeCost(states, pinsByNet, spacingMils);

            var moves = new JArray();
            foreach (var st in states.Values)
            {
                if (!initial.TryGetValue(st.Designator, out var before))
                    continue;
                var rotDelta = Math.Abs(NormalizeRotation(st.Rotation) - NormalizeRotation(before.Item3));
                if (rotDelta > 180.0)
                    rotDelta = 360.0 - rotDelta;
                bool moved =
                    Math.Abs(before.Item1 - st.X) >= 0.5 ||
                    Math.Abs(before.Item2 - st.Y) >= 0.5 ||
                    rotDelta >= 0.5;
                if (!moved)
                    continue;

                var layerOut = string.Equals(st.Layer, "BOTTOM", StringComparison.OrdinalIgnoreCase)
                    ? "bottom"
                    : "top";
                moves.Add(new JObject
                {
                    ["designator"] = st.Designator,
                    ["anchor"] = "BOARD",
                    ["comment"] = st.Component?["description"] ?? st.Component?["pattern"],
                    ["xMils"] = Math.Round(st.X, 3),
                    ["yMils"] = Math.Round(st.Y, 3),
                    ["rotation"] = Math.Round(st.Rotation, 3),
                    ["layer"] = layerOut,
                    ["mirror"] = layerOut == "bottom",
                    ["method"] = "force_directed_sa",
                    ["roles"] = new JArray("optimizer"),
                    ["nets"] = new JArray(),
                    ["current"] = new JObject
                    {
                        ["xMils"] = Math.Round(before.Item1, 3),
                        ["yMils"] = Math.Round(before.Item2, 3),
                        ["rotation"] = Math.Round(before.Item3, 3),
                        ["layer"] = layerOut,
                    },
                });
            }

            result["found"] = true;
            result["schemaVersion"] = PlacementConstants.PlanSchemaVersion;
            result["mode"] = "force_directed_sa";
            result["layoutMode"] = "force_directed_sa";
            result["anchor"] = "BOARD";
            result["anchors"] = new JArray("BOARD");
            result["cluster_count"] = 1;
            result["moves"] = moves;
            result["move_count"] = moves.Count;
            result["cost_before"] = Math.Round(costBefore, 1);
            result["cost_after"] = Math.Round(costAfter, 1);
            result["component_count"] = states.Count;
            result["locked_count"] = states.Values.Count(s => s.Locked);
            result["movable_count"] = states.Values.Count(s => !s.Locked);
            result["note"] =
                "Two-phase: force-directed (HPWL) then simulated annealing with 90° rotations. " +
                $"Target clearance ~{spacingMils:0} mil (middle-ground packing).";
            return result;
        }

        private static void RunForceDirected(
            Dictionary<string, OptState> states,
            Dictionary<string, List<OptPin>> pinsByNet,
            int iterations,
            double spacingMils)
        {
            var movable = states.Values.Where(s => !s.Locked).ToList();
            if (movable.Count == 0 || iterations <= 0)
                return;

            double springK = 0.045;
            double repulseK = 9200.0;
            double damping = 0.82;
            var velX = movable.ToDictionary(s => s.Designator, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            var velY = movable.ToDictionary(s => s.Designator, _ => 0.0, StringComparer.OrdinalIgnoreCase);

            for (int iter = 0; iter < iterations; iter++)
            {
                double cool = 1.0 - (iter / (double)Math.Max(1, iterations - 1));
                cool = 0.35 + 0.65 * cool;
                var fx = movable.ToDictionary(s => s.Designator, _ => 0.0, StringComparer.OrdinalIgnoreCase);
                var fy = movable.ToDictionary(s => s.Designator, _ => 0.0, StringComparer.OrdinalIgnoreCase);

                foreach (var kv in pinsByNet)
                {
                    var pins = kv.Value;
                    if (pins.Count < 2)
                        continue;

                    double cx = 0, cy = 0;
                    foreach (var p in pins)
                    {
                        var st = states[p.Designator];
                        cx += st.X + p.Dx;
                        cy += st.Y + p.Dy;
                    }
                    cx /= pins.Count;
                    cy /= pins.Count;

                    foreach (var p in pins)
                    {
                        var st = states[p.Designator];
                        if (st.Locked)
                            continue;
                        double px = st.X + p.Dx;
                        double py = st.Y + p.Dy;
                        fx[st.Designator] += springK * (cx - px);
                        fy[st.Designator] += springK * (cy - py);
                    }
                }

                for (int i = 0; i < movable.Count; i++)
                {
                    var a = movable[i];
                    foreach (var b in states.Values)
                    {
                        if (ReferenceEquals(a, b))
                            continue;
                        if (!SameLayer(a.Layer, b.Layer))
                            continue;

                        double dx = a.X - b.X;
                        double dy = a.Y - b.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist < 1e-3)
                        {
                            dx = 1;
                            dy = 0;
                            dist = 1;
                        }

                        double minDist =
                            a.HalfWidth + b.HalfWidth + spacingMils * 0.55 +
                            Math.Abs(a.HalfHeight + b.HalfHeight) * 0.15;
                        if (dist >= minDist)
                            continue;

                        double push = repulseK * (minDist - dist) / (dist * dist);
                        fx[a.Designator] += push * (dx / dist);
                        fy[a.Designator] += push * (dy / dist);
                    }
                }

                double maxStep = 18.0 + 55.0 * cool;
                foreach (var st in movable)
                {
                    velX[st.Designator] = damping * velX[st.Designator] + fx[st.Designator] * cool;
                    velY[st.Designator] = damping * velY[st.Designator] + fy[st.Designator] * cool;
                    double vx = velX[st.Designator];
                    double vy = velY[st.Designator];
                    double mag = Math.Sqrt(vx * vx + vy * vy);
                    if (mag > maxStep && mag > 1e-9)
                    {
                        vx *= maxStep / mag;
                        vy *= maxStep / mag;
                        velX[st.Designator] = vx;
                        velY[st.Designator] = vy;
                    }
                    st.X += vx;
                    st.Y += vy;
                }
            }
        }

        /// <summary>
        /// Simulated annealing refinement: small XY moves, ±90° rotations, and
        /// occasional swaps. Accepts uphill moves with Metropolis criterion.
        /// </summary>
        private static void RunSimulatedAnnealing(
            Dictionary<string, OptState> states,
            Dictionary<string, List<OptPin>> pinsByNet,
            double spacingMils,
            int steps,
            int trialsPerStep)
        {
            var movable = states.Values.Where(s => !s.Locked).ToList();
            if (movable.Count == 0 || steps <= 0)
                return;

            var rng = new Random(42);
            double temperature = EstimateInitialTemperature(states, pinsByNet, spacingMils);
            double currentCost = ComputeCost(states, pinsByNet, spacingMils);
            double cooling = Math.Pow(0.02, 1.0 / Math.Max(1, steps)); // cool to ~2% of T0

            for (int step = 0; step < steps; step++)
            {
                double stepScale = 1.0 - step / (double)Math.Max(1, steps - 1);
                double moveAmp = 8.0 + 40.0 * stepScale; // mils

                for (int t = 0; t < trialsPerStep; t++)
                {
                    var pick = movable[rng.Next(movable.Count)];
                    int moveType = rng.Next(100);

                    // Snapshot
                    double oldX = pick.X, oldY = pick.Y, oldRot = pick.Rotation;
                    double oldHw = pick.HalfWidth, oldHh = pick.HalfHeight;
                    var pinSnap = SnapshotPins(pick.Designator, pinsByNet);

                    if (moveType < 55)
                    {
                        // Translate
                        pick.X += (rng.NextDouble() * 2 - 1) * moveAmp;
                        pick.Y += (rng.NextDouble() * 2 - 1) * moveAmp;
                    }
                    else if (moveType < 88)
                    {
                        // Rotate ±90° — best for packing + pin alignment
                        int turns = rng.Next(2) == 0 ? 1 : -1;
                        ApplyRotationTurns(pick, pinsByNet, turns);
                    }
                    else
                    {
                        // Swap with a nearby unlocked part (classic SA PCB move)
                        var other = FindNearbyMovable(pick, movable, rng, 350.0);
                        if (other != null && !ReferenceEquals(other, pick))
                        {
                            double ox = other.X, oy = other.Y;
                            other.X = pick.X;
                            other.Y = pick.Y;
                            pick.X = ox;
                            pick.Y = oy;

                            double newCostSwap = ComputeCost(states, pinsByNet, spacingMils);
                            if (Accept(currentCost, newCostSwap, temperature, rng))
                            {
                                currentCost = newCostSwap;
                                continue;
                            }

                            // revert swap
                            pick.X = other.X;
                            pick.Y = other.Y;
                            other.X = ox;
                            other.Y = oy;
                            continue;
                        }

                        // fallback translate if no neighbor
                        pick.X += (rng.NextDouble() * 2 - 1) * moveAmp * 0.5;
                        pick.Y += (rng.NextDouble() * 2 - 1) * moveAmp * 0.5;
                    }

                    double newCost = ComputeCost(states, pinsByNet, spacingMils);
                    if (Accept(currentCost, newCost, temperature, rng))
                    {
                        currentCost = newCost;
                    }
                    else
                    {
                        pick.X = oldX;
                        pick.Y = oldY;
                        pick.Rotation = oldRot;
                        pick.HalfWidth = oldHw;
                        pick.HalfHeight = oldHh;
                        RestorePins(pick.Designator, pinsByNet, pinSnap);
                    }
                }

                temperature *= cooling;
            }
        }

        private static bool Accept(double oldCost, double newCost, double temperature, Random rng)
        {
            if (newCost <= oldCost)
                return true;
            if (temperature < 1e-9)
                return false;
            double prob = Math.Exp(-(newCost - oldCost) / temperature);
            return rng.NextDouble() < prob;
        }

        private static double EstimateInitialTemperature(
            Dictionary<string, OptState> states,
            Dictionary<string, List<OptPin>> pinsByNet,
            double spacingMils)
        {
            // Scale T0 to cost magnitude so Metropolis accepts ~30–40% uphill early on.
            double c = ComputeCost(states, pinsByNet, spacingMils);
            return Math.Max(800.0, c * 0.012);
        }

        private static OptState FindNearbyMovable(
            OptState pick,
            List<OptState> movable,
            Random rng,
            double radius)
        {
            var near = movable
                .Where(o => !ReferenceEquals(o, pick))
                .Where(o => SameLayer(o.Layer, pick.Layer))
                .Where(o =>
                {
                    double dx = o.X - pick.X;
                    double dy = o.Y - pick.Y;
                    return dx * dx + dy * dy <= radius * radius;
                })
                .ToList();
            if (near.Count == 0)
                return null;
            return near[rng.Next(near.Count)];
        }

        private static List<Tuple<OptPin, double, double>> SnapshotPins(
            string designator,
            Dictionary<string, List<OptPin>> pinsByNet)
        {
            var snap = new List<Tuple<OptPin, double, double>>();
            foreach (var pins in pinsByNet.Values)
            {
                foreach (var p in pins)
                {
                    if (string.Equals(p.Designator, designator, StringComparison.OrdinalIgnoreCase))
                        snap.Add(Tuple.Create(p, p.Dx, p.Dy));
                }
            }
            return snap;
        }

        private static void RestorePins(
            string designator,
            Dictionary<string, List<OptPin>> pinsByNet,
            List<Tuple<OptPin, double, double>> snap)
        {
            foreach (var t in snap)
            {
                t.Item1.Dx = t.Item2;
                t.Item1.Dy = t.Item3;
            }
        }

        /// <summary>
        /// Apply N quarter-turns (+1 = +90° CCW in board XY with Y-up).
        /// Updates rotation, swaps bbox on odd turns, rotates pin offsets.
        /// </summary>
        private static void ApplyRotationTurns(
            OptState st,
            Dictionary<string, List<OptPin>> pinsByNet,
            int turns)
        {
            turns %= 4;
            if (turns < 0)
                turns += 4;
            if (turns == 0)
                return;

            for (int i = 0; i < turns; i++)
            {
                foreach (var pins in pinsByNet.Values)
                {
                    foreach (var p in pins)
                    {
                        if (!string.Equals(p.Designator, st.Designator, StringComparison.OrdinalIgnoreCase))
                            continue;
                        // +90°: (dx, dy) -> (-dy, dx)
                        double ndx = -p.Dy;
                        double ndy = p.Dx;
                        p.Dx = ndx;
                        p.Dy = ndy;
                    }
                }

                double tmp = st.HalfWidth;
                st.HalfWidth = st.HalfHeight;
                st.HalfHeight = tmp;
                st.Rotation = NormalizeRotation(st.Rotation + 90.0);
            }
        }

        /// <summary>
        /// Build net → pin-offset map from PCB component pads (preferred over flat nets).
        /// Pin offsets are relative to component origin and stay valid under XY moves;
        /// rotations update them via ApplyRotationTurns.
        /// </summary>
        private static Dictionary<string, List<OptPin>> BuildPinsByNetFromComponents(
            Dictionary<string, OptState> states)
        {
            var pinsByNet = new Dictionary<string, List<OptPin>>(StringComparer.OrdinalIgnoreCase);

            foreach (var st in states.Values)
            {
                var pins = st.Component?["pins"] as JArray;
                if (pins == null)
                    continue;

                foreach (JObject pin in pins.OfType<JObject>())
                {
                    var net = (pin.Value<string>("net") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(net) ||
                        net.Equals("No Net", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsIgnoredNet(net))
                        continue;

                    double dx = 0, dy = 0;
                    var px = GetDouble(pin, "xMils", double.NaN);
                    var py = GetDouble(pin, "yMils", double.NaN);
                    if (!double.IsNaN(px) && !double.IsNaN(py))
                    {
                        dx = px - st.X;
                        dy = py - st.Y;
                    }

                    if (!pinsByNet.TryGetValue(net, out var list))
                    {
                        list = new List<OptPin>();
                        pinsByNet[net] = list;
                    }

                    list.Add(new OptPin
                    {
                        Designator = st.Designator,
                        Dx = dx,
                        Dy = dy,
                    });
                }
            }

            // Drop nets that don't connect at least two components (after rail filter).
            var keep = pinsByNet
                .Where(kv => kv.Value.Select(p => p.Designator).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return keep;
        }

        private static double ComputeCost(
            Dictionary<string, OptState> states,
            Dictionary<string, List<OptPin>> pinsByNet,
            double spacingMils)
        {
            double cost = 0;

            // HPWL (half-perimeter wirelength) — primary objective
            foreach (var pins in pinsByNet.Values)
            {
                if (pins.Count < 2)
                    continue;
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                foreach (var p in pins)
                {
                    var st = states[p.Designator];
                    double x = st.X + p.Dx;
                    double y = st.Y + p.Dy;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                cost += (maxX - minX) + (maxY - minY);
            }

            // Hard overlap + soft clearance (middle-ground packing)
            var list = states.Values.ToList();
            double preferred = spacingMils;
            double softK = 0.35;
            double crushK = 2.2;

            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (!SameLayer(a.Layer, b.Layer))
                        continue;

                    double gapX = Math.Abs(a.X - b.X) - (a.HalfWidth + b.HalfWidth);
                    double gapY = Math.Abs(a.Y - b.Y) - (a.HalfHeight + b.HalfHeight);
                    double gap = Math.Max(gapX, gapY);

                    if (gap < 0)
                    {
                        // Hard overlap — strongly discouraged
                        cost += 180000.0 + (-gap) * (-gap) * 90.0;
                    }
                    else if (gap < preferred)
                    {
                        // Too tight — soft push toward preferred clearance
                        double d = preferred - gap;
                        cost += softK * d * d * crushK;
                    }
                    else if (gap < preferred * 3.5)
                    {
                        // Mild preference not to leave huge local voids between neighbors
                        // (only within local neighborhood so distant parts aren't pulled)
                        double over = gap - preferred;
                        cost += softK * 0.04 * over * over;
                    }
                }
            }

            return cost;
        }

        private static void ResolveOverlaps(
            Dictionary<string, OptState> states,
            double spacingMils)
        {
            var occupied = new List<PlacementBox>();
            foreach (var st in states.Values.Where(s => s.Locked))
            {
                occupied.Add(ToBox(st));
            }

            foreach (var st in states.Values.Where(s => !s.Locked))
            {
                var box = ToBox(st);
                var clear = PlacementLayout.FindClearPlacement(
                    st.X,
                    st.Y,
                    Tuple.Create(st.X, st.Y),
                    box,
                    occupied,
                    null,
                    spacingMils,
                    2000.0);
                st.X = clear.Item1;
                st.Y = clear.Item2;
                box.X = st.X;
                box.Y = st.Y;
                occupied.Add(box);
            }
        }

        private static void SnapToGrid(Dictionary<string, OptState> states, double gridMils)
        {
            if (gridMils <= 0.1)
                return;
            foreach (var st in states.Values.Where(s => !s.Locked))
            {
                st.X = Math.Round(st.X / gridMils) * gridMils;
                st.Y = Math.Round(st.Y / gridMils) * gridMils;
            }
        }

        private static PlacementBox ToBox(OptState st)
        {
            return new PlacementBox
            {
                Designator = st.Designator,
                Layer = st.Layer,
                X = st.X,
                Y = st.Y,
                HalfWidth = st.HalfWidth,
                HalfHeight = st.HalfHeight,
            };
        }

        private static bool ShouldLock(string designator, bool lockIcs)
        {
            var d = (designator ?? "").Trim().ToUpperInvariant();
            if (d.StartsWith("J") || d.StartsWith("P") || d.StartsWith("H") ||
                d.StartsWith("MH") || d.StartsWith("MP") || d.StartsWith("TP"))
                return true;
            if (!lockIcs)
                return false;
            return d.StartsWith("U") || d.StartsWith("IC") || d.StartsWith("Q") ||
                   d.StartsWith("D") || d.StartsWith("Y") || d.StartsWith("X");
        }

        private static bool IsIgnoredNet(string name)
        {
            var n = (name ?? "").Trim().ToUpperInvariant();
            return n == "GND" || n == "AGND" || n == "DGND" || n == "PGND" ||
                   n == "VCC" || n == "VDD" || n == "VSS" || n == "3V3" ||
                   n == "5V" || n == "1V8" || n == "12V" ||
                   n.StartsWith("GND") || n.StartsWith("VCC") || n.StartsWith("VDD");
        }

        private static bool SameLayer(string a, string b)
        {
            return string.Equals(NormalizeLayer(a), NormalizeLayer(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLayer(string layer)
        {
            var l = (layer ?? "TOP").Trim().ToUpperInvariant();
            if (l.Contains("BOTTOM") || l == "BOT" || l == "B")
                return "BOTTOM";
            return "TOP";
        }

        private static double NormalizeRotation(double rot)
        {
            rot %= 360.0;
            if (rot < 0)
                rot += 360.0;
            // Snap near-multiples of 90 for stability
            double nearest = Math.Round(rot / 90.0) * 90.0;
            if (Math.Abs(rot - nearest) < 0.5)
                rot = nearest;
            if (rot >= 360.0)
                rot -= 360.0;
            return rot;
        }

        private static double GetDouble(JToken token, string key, double fallback)
        {
            if (token == null || !(token is JObject obj))
                return fallback;
            var v = obj[key];
            if (v == null || v.Type == JTokenType.Null)
                return fallback;
            double d;
            return double.TryParse(v.ToString(), out d) ? d : fallback;
        }

        private sealed class OptState
        {
            public string Designator;
            public double X;
            public double Y;
            public double HalfWidth;
            public double HalfHeight;
            public string Layer;
            public double Rotation;
            public bool Locked;
            public JObject Component;
        }

        private sealed class OptPin
        {
            public string Designator;
            public double Dx;
            public double Dy;
        }
    }
}
