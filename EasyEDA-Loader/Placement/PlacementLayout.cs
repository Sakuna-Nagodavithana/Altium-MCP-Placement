using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    internal sealed class PlacementTargetResult
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string Method { get; set; }
        public double? TargetPinAngle { get; set; }
        public double StandoffMils { get; set; }
        public int PinSlot { get; set; }
        public double AngleOffsetDeg { get; set; }
        public double? RotationDeg { get; set; }
        /// <summary>"top" or "bottom". Decoupling stays on top for multilayer plane boards.</summary>
        public string Layer { get; set; } = "top";
        public bool Mirror { get; set; }
    }

    internal sealed class PlacementBox
    {
        public string Designator { get; set; }
        public string Layer { get; set; } = "top";
        public double X { get; set; }
        public double Y { get; set; }
        public double HalfWidth { get; set; }
        public double HalfHeight { get; set; }
    }

    internal static class PlacementLayout
    {
        internal static double RoleStandoffMils(string role, double spacingMils)
        {
            // Middle-ground radii: close enough to route short, far enough for assembly.
            switch (role ?? "support")
            {
                case "decoupling": return Math.Max(spacingMils * 1.05, 85.0);
                case "power": return Math.Max(spacingMils * 1.45, 130.0);
                case "signal": return Math.Max(spacingMils * 1.85, 165.0);
                default: return Math.Max(spacingMils * 1.55, 120.0);
            }
        }

        internal static double PinEdgeStandoffMils(string role, double spacingMils)
        {
            switch (role ?? "support")
            {
                case "decoupling": return Math.Max(spacingMils * 0.45, 28.0);
                case "power": return Math.Max(spacingMils * 0.7, 40.0);
                case "signal": return Math.Max(spacingMils * 0.9, 48.0);
                default: return Math.Max(spacingMils * 1.0, 55.0);
            }
        }

        internal static PlacementTargetResult ChainTargetXy(
            JObject item,
            int chainIndex,
            int chainLength,
            Tuple<double, double> anchorXy,
            double spacingMils,
            double maxRadiusMils,
            List<Tuple<double, double>> placedPoints,
            Tuple<double, double> previousXy = null,
            Tuple<double, double> pinXy = null,
            JObject pcbComponent = null,
            List<Tuple<double, double, double, double>> keepoutBoxes = null,
            List<Tuple<double, double, double>> pcbObstacles = null,
            List<string> placedLayers = null,
            List<Tuple<double, double>> placedHalfSizes = null)
        {
            var sch = item["schematic"] as JObject ?? new JObject();
            var targetPinAngle = TokenDouble(sch["pinAngleDeg"]) ?? TokenDouble(sch["angleDeg"]) ?? 0.0;
            var role = PlacementConstants.JsonStr(item["primary_role"]);
            if (string.IsNullOrEmpty(role))
                role = "support";

            var angleDeg = targetPinAngle;
            var angleOffsetDeg = 0.0;
            var bodyRadius = PassiveBodyRadiusMils(item, pcbComponent);
            // Spacing between chain members = body + routing gap. Tight natural chain
            // along the pin ray; subsequent parts sit close to the previous member.
            var step = Math.Max(spacingMils * 0.75, bodyRadius * 2.0 + Math.Max(spacingMils * 0.28, 18.0));

            // Origin for this member: the IC pin for the first member, the previous
            // member's position for subsequent ones. Falls back to the IC center.
            var origin = chainIndex > 0 && previousXy != null
                ? previousXy
                : (pinXy ?? anchorXy);
            var standoff = chainIndex == 0
                ? RoleStandoffMils(role, spacingMils)
                : step;

            standoff = Math.Min(maxRadiusMils, standoff);
            var angle = angleDeg * Math.PI / 180.0;
            var x = origin.Item1 + standoff * Math.Cos(angle);
            var y = origin.Item2 + standoff * Math.Sin(angle);

            var courtyard = CourtyardHalfSizeMils(item, pcbComponent);
            var bboxHalf = GetBboxHalfSize(pcbComponent);
            var resolved = ResolveCollision(
                x,
                y,
                spacingMils,
                maxRadiusMils,
                origin,
                placedPoints,
                courtyard,
                keepoutBoxes,
                pcbObstacles,
                "top",
                placedLayers,
                bboxHalf ?? Tuple.Create(courtyard, courtyard),
                placedHalfSizes);
            x = resolved.Item1;
            y = resolved.Item2;
            standoff = Math.Sqrt(Math.Pow(x - origin.Item1, 2) + Math.Pow(y - origin.Item2, 2));

            return new PlacementTargetResult
            {
                X = x,
                Y = y,
                Method = chainIndex > 0 ? "pin_chain_rel" : "pin_chain",
                TargetPinAngle = targetPinAngle,
                StandoffMils = standoff,
                PinSlot = chainIndex,
                AngleOffsetDeg = angleOffsetDeg,
            };
        }

        internal static PlacementTargetResult PinNearTargetXy(
            JObject item,
            int index,
            Tuple<double, double> anchorXy,
            double spacingMils,
            double maxRadiusMils,
            Dictionary<Tuple<string, string>, int> pinSlotCounts,
            List<Tuple<double, double>> placedPoints,
            JObject pcbComponent = null,
            List<Tuple<double, double, double, double>> keepoutBoxes = null,
            List<Tuple<double, double, double>> pcbObstacles = null,
            List<string> placedLayers = null,
            List<Tuple<double, double>> placedHalfSizes = null)
        {
            var sch = item["schematic"] as JObject ?? new JObject();
            var targetPinAngle = TokenDouble(sch["pinAngleDeg"]) ?? TokenDouble(sch["angleDeg"]) ?? (index * 137.508) % 360.0;
            var role = PlacementConstants.JsonStr(item["primary_role"]);
            if (string.IsNullOrEmpty(role))
                role = "support";

            var pinKeyPin = PlacementConstants.JsonStr(item["primary_ic_pin"]);
            if (string.IsNullOrEmpty(pinKeyPin))
                pinKeyPin = "unknown";
            var pinNet = PlacementConstants.JsonStr(item["primary_net"]);
            if (string.IsNullOrEmpty(pinNet))
                pinNet = "unknown";
            var pinKey = Tuple.Create(pinKeyPin, pinNet);

            var slot = pinSlotCounts.TryGetValue(pinKey, out var existingSlot) ? existingSlot : 0;
            pinSlotCounts[pinKey] = slot + 1;

            var standoff = RoleStandoffMils(role, spacingMils) + slot * spacingMils * 0.75;
            var angleDeg = targetPinAngle + slot * 14.0;
            var angleOffsetDeg = slot * 14.0;

            standoff = Math.Min(maxRadiusMils, standoff);
            var angle = angleDeg * Math.PI / 180.0;
            var x = anchorXy.Item1 + standoff * Math.Cos(angle);
            var y = anchorXy.Item2 + standoff * Math.Sin(angle);

            var courtyard = CourtyardHalfSizeMils(item, pcbComponent);
            var bboxHalf = GetBboxHalfSize(pcbComponent);
            var resolved = ResolveCollision(
                x,
                y,
                spacingMils,
                maxRadiusMils,
                anchorXy,
                placedPoints,
                courtyard,
                keepoutBoxes,
                pcbObstacles,
                "top",
                placedLayers,
                bboxHalf ?? Tuple.Create(courtyard, courtyard),
                placedHalfSizes);
            x = resolved.Item1;
            y = resolved.Item2;
            standoff = Math.Sqrt(Math.Pow(x - anchorXy.Item1, 2) + Math.Pow(y - anchorXy.Item2, 2));

            return new PlacementTargetResult
            {
                X = x,
                Y = y,
                Method = "pin_near",
                TargetPinAngle = targetPinAngle,
                StandoffMils = standoff,
                PinSlot = slot,
                AngleOffsetDeg = angleOffsetDeg,
            };
        }

        internal static Tuple<double, double, string> CompactTargetXy(
            JObject item,
            int index,
            Tuple<double, double> anchorXy,
            double spacingMils,
            double maxRadiusMils,
            Dictionary<string, int> netSlotCounts)
        {
            var role = PlacementConstants.JsonStr(item["primary_role"]);
            if (string.IsNullOrEmpty(role))
                role = "support";

            double baseRadius;
            switch (role)
            {
                case "decoupling": baseRadius = Math.Max(spacingMils * 1.5, 180.0); break;
                case "power": baseRadius = Math.Max(spacingMils * 2.5, 280.0); break;
                case "signal": baseRadius = Math.Max(spacingMils * 3.5, 380.0); break;
                default: baseRadius = spacingMils * 3.0; break;
            }

            var sch = item["schematic"] as JObject ?? new JObject();
            var angleDeg = TokenDouble(sch["pinAngleDeg"]) ?? TokenDouble(sch["angleDeg"]) ?? (index * 137.508) % 360.0;
            var primaryNet = PlacementConstants.JsonStr(item["primary_net"]);
            if (string.IsNullOrEmpty(primaryNet))
                primaryNet = "unknown";

            var slot = netSlotCounts.TryGetValue(primaryNet, out var existingSlot) ? existingSlot : 0;
            netSlotCounts[primaryNet] = slot + 1;
            angleDeg += slot * 8.0;

            var ring = index / 8;
            var radius = Math.Min(maxRadiusMils, baseRadius + ring * spacingMils * 1.5);
            var angle = angleDeg * Math.PI / 180.0;
            return Tuple.Create(
                anchorXy.Item1 + radius * Math.Cos(angle),
                anchorXy.Item2 + radius * Math.Sin(angle),
                "compact_net_pin");
        }

        internal static Tuple<double, double, string> MirrorTargetXy(
            JObject item,
            Tuple<double, double> anchorXy,
            double schematicScale,
            double maxRadiusMils)
        {
            var sch = item["schematic"] as JObject ?? new JObject();
            var offsetX = TokenDouble(sch["offsetXMils"]);
            var offsetY = TokenDouble(sch["offsetYMils"]);
            if (!offsetX.HasValue || !offsetY.HasValue)
                return Tuple.Create(anchorXy.Item1, anchorXy.Item2, "mirror_missing");

            var scaledX = offsetX.Value * schematicScale;
            var scaledY = offsetY.Value * schematicScale;
            var dist = Math.Sqrt(scaledX * scaledX + scaledY * scaledY);
            if (dist > maxRadiusMils && maxRadiusMils > 0)
            {
                var scale = maxRadiusMils / dist;
                scaledX *= scale;
                scaledY *= scale;
            }

            return Tuple.Create(anchorXy.Item1 + scaledX, anchorXy.Item2 + scaledY, "schematic_mirror");
        }

        internal static double AutoSchematicScale(JArray supportComponents, double maxRadiusMils, double fallback = 0.12)
        {
            const double minScale = 0.06;
            const double maxScale = 0.32;
            var maxDist = 0.0;
            foreach (var item in supportComponents.OfType<JObject>())
            {
                var sch = item["schematic"] as JObject;
                var offsetX = TokenDouble(sch?["offsetXMils"]);
                var offsetY = TokenDouble(sch?["offsetYMils"]);
                if (!offsetX.HasValue || !offsetY.HasValue)
                    continue;
                maxDist = Math.Max(maxDist, Math.Sqrt(offsetX.Value * offsetX.Value + offsetY.Value * offsetY.Value));
            }

            if (maxDist <= 0)
                return fallback;
            var target = maxRadiusMils * 0.88;
            return Math.Max(minScale, Math.Min(maxScale, target / maxDist));
        }

        internal static JObject BuildPcbPinIndex(JObject pcbComponent)
        {
            var index = new JObject();
            if (pcbComponent == null)
                return index;

            foreach (var pin in PlacementConstants.Pins(pcbComponent))
            {
                var pinNumber = PlacementConstants.JsonStr(pin["name"]);
                if (string.IsNullOrEmpty(pinNumber))
                    pinNumber = PlacementConstants.JsonStr(pin["number"]);
                pinNumber = pinNumber.Trim();
                if (string.IsNullOrEmpty(pinNumber))
                    continue;
                if (pin["xMils"] == null || pin["yMils"] == null ||
                    pin["xMils"].Type == JTokenType.Null || pin["yMils"].Type == JTokenType.Null)
                {
                    continue;
                }

                index[pinNumber] = new JObject
                {
                    ["pin"] = pinNumber,
                    ["net"] = pin["net"],
                    ["xMils"] = pin["xMils"],
                    ["yMils"] = pin["yMils"],
                };
            }

            return index;
        }

        internal static Tuple<double, double> ResolveIcPinPcbXy(
            string pinNumber,
            JObject pcbPinIndex,
            JObject pinLayout,
            Tuple<double, double> anchorSchXy,
            Tuple<double, double> anchorPcbXy,
            double scale)
        {
            if (!string.IsNullOrWhiteSpace(pinNumber))
            {
                var key = pinNumber.Trim();
                if (pcbPinIndex[key] is JObject entry)
                    return Tuple.Create(entry["xMils"].Value<double>(), entry["yMils"].Value<double>());
            }

            return SchPinToPcbXy(pinNumber, pinLayout, anchorSchXy, anchorPcbXy, scale);
        }

        internal static double PinOutwardAngleDeg(Tuple<double, double> pinXy, Tuple<double, double> anchorXy)
        {
            var dx = pinXy.Item1 - anchorXy.Item1;
            var dy = pinXy.Item2 - anchorXy.Item2;
            if (Math.Sqrt(dx * dx + dy * dy) < 1e-6)
                return 0.0;
            return Math.Atan2(dy, dx) * 180.0 / Math.PI;
        }

        internal static double? ParseCapValueNf(string comment)
        {
            var text = (comment ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty);
            if (string.IsNullOrEmpty(text))
                return null;
            var match = PlacementConstants.CapValuePattern.Match(text);
            if (!match.Success)
                return null;

            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups[2].Value.ToUpperInvariant();
            if (unit == "UF" || unit == "U")
                return value * 1000.0;
            if (unit == "NF" || unit == "N")
                return value;
            if (unit == "PF" || unit == "P")
                return value / 1000.0;
            if (value < 1.0)
                return value * 1000.0;
            return value;
        }

        internal static int CapProximityRank(JObject item)
        {
            if (PlacementConstants.JsonStr(item["primary_role"]) != "decoupling")
                return 50;
            var nf = ParseCapValueNf(PlacementConstants.JsonStr(item["comment"]));
            if (!nf.HasValue)
                return 25;
            if (nf.Value <= 150.0)
                return 0;
            if (nf.Value <= 1500.0)
                return 1;
            if (nf.Value <= 12000.0)
                return 2;
            return 3;
        }

        /// <summary>
        /// Read the actual half-width and half-height from the PCB component's exported
        /// bounding box (bboxMils). Returns null if the bounding box wasn't exported.
        /// </summary>
        internal static Tuple<double, double> GetBboxHalfSize(
            JObject pcbComponent,
            double? targetRotationDeg = null)
        {
            var bbox = pcbComponent?["bboxMils"] as JObject;
            if (bbox == null) return null;
            var hw = TokenDouble(bbox["halfWidthMils"]);
            var hh = TokenDouble(bbox["halfHeightMils"]);
            if (!hw.HasValue || !hh.HasValue) return null;

            if (!targetRotationDeg.HasValue)
                return Tuple.Create(hw.Value, hh.Value);

            var currentRotation =
                TokenDouble(pcbComponent?["placement"]?["rotation"]) ?? 0.0;
            var delta = (targetRotationDeg.Value - currentRotation) * Math.PI / 180.0;
            var cos = Math.Abs(Math.Cos(delta));
            var sin = Math.Abs(Math.Sin(delta));
            return Tuple.Create(
                hw.Value * cos + hh.Value * sin,
                hw.Value * sin + hh.Value * cos);
        }

        internal static PlacementBox CreatePlacementBox(
            string designator,
            string layer,
            double x,
            double y,
            JObject pcbComponent,
            double? targetRotationDeg,
            double fallbackHalfSize = 40.0)
        {
            var half = GetBboxHalfSize(pcbComponent, targetRotationDeg)
                       ?? Tuple.Create(fallbackHalfSize, fallbackHalfSize);
            return new PlacementBox
            {
                Designator = designator ?? string.Empty,
                Layer = NormalizeLayerName(layer),
                X = x,
                Y = y,
                HalfWidth = Math.Max(half.Item1, 1.0),
                HalfHeight = Math.Max(half.Item2, 1.0),
            };
        }

        internal static string NormalizeLayerName(string layer)
        {
            return (layer ?? string.Empty).IndexOf(
                "bottom",
                StringComparison.OrdinalIgnoreCase) >= 0
                ? "bottom"
                : "top";
        }

        internal static bool BoxesOverlap(
            PlacementBox left,
            PlacementBox right,
            double gapMils)
        {
            if (left == null || right == null)
                return false;
            if (!string.Equals(
                    NormalizeLayerName(left.Layer),
                    NormalizeLayerName(right.Layer),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Math.Abs(left.X - right.X) <
                       left.HalfWidth + right.HalfWidth + gapMils &&
                   Math.Abs(left.Y - right.Y) <
                       left.HalfHeight + right.HalfHeight + gapMils;
        }

        internal static Tuple<double, double, bool> FindClearPlacement(
            double desiredX,
            double desiredY,
            Tuple<double, double> anchorXy,
            PlacementBox movingBox,
            List<PlacementBox> occupied,
            List<Tuple<double, double, double, double>> keepoutBoxes,
            double spacingMils,
            double maxRadiusMils)
        {
            var gap = Math.Max(spacingMils * 0.55, 28.0);

            bool Blocked(double testX, double testY)
            {
                movingBox.X = testX;
                movingBox.Y = testY;
                if ((occupied ?? new List<PlacementBox>())
                    .Any(box => BoxesOverlap(movingBox, box, gap)))
                {
                    return true;
                }

                if (keepoutBoxes != null)
                {
                    foreach (var box in keepoutBoxes)
                    {
                        if (testX + movingBox.HalfWidth >= box.Item1 &&
                            testX - movingBox.HalfWidth <= box.Item3 &&
                            testY + movingBox.HalfHeight >= box.Item2 &&
                            testY - movingBox.HalfHeight <= box.Item4)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            if (!Blocked(desiredX, desiredY))
                return Tuple.Create(desiredX, desiredY, false);

            var dx = desiredX - anchorXy.Item1;
            var dy = desiredY - anchorXy.Item2;
            var baseRadius = Math.Max(Math.Sqrt(dx * dx + dy * dy), spacingMils);
            var baseAngle = Math.Atan2(dy, dx);
            var radialStep = Math.Max(
                spacingMils * 0.55,
                Math.Max(movingBox.HalfWidth, movingBox.HalfHeight) + gap);
            var searchRadius = Math.Max(maxRadiusMils, baseRadius + radialStep * 16.0);

            for (var ring = 0; ring < 24; ring++)
            {
                var radius = baseRadius + ring * radialStep;
                if (radius > searchRadius)
                    break;

                for (var angleStep = 0; angleStep < 24; angleStep++)
                {
                    var signedStep = angleStep == 0
                        ? 0
                        : (angleStep % 2 == 1 ? 1 : -1) * ((angleStep + 1) / 2);
                    var angle = baseAngle + signedStep * 15.0 * Math.PI / 180.0;
                    var candidateX = anchorXy.Item1 + radius * Math.Cos(angle);
                    var candidateY = anchorXy.Item2 + radius * Math.Sin(angle);
                    if (!Blocked(candidateX, candidateY))
                        return Tuple.Create(candidateX, candidateY, true);
                }
            }

            // No overlap-free location was found in the normal search area. Continue
            // radially rather than returning a known overlap.
            for (var ring = 24; ring < 64; ring++)
            {
                var radius = baseRadius + ring * radialStep;
                for (var quadrant = 0; quadrant < 8; quadrant++)
                {
                    var angle = baseAngle + quadrant * Math.PI / 4.0;
                    var candidateX = anchorXy.Item1 + radius * Math.Cos(angle);
                    var candidateY = anchorXy.Item2 + radius * Math.Sin(angle);
                    if (!Blocked(candidateX, candidateY))
                        return Tuple.Create(candidateX, candidateY, true);
                }
            }

            return Tuple.Create(desiredX, desiredY, false);
        }

        internal static double PassiveBodyRadiusMils(JObject item, JObject pcbComponent)
        {
            var pattern = PlacementConstants.JsonStr(pcbComponent?["pattern"]).ToUpperInvariant();
            var comment = PlacementConstants.JsonStr(item?["comment"]).ToUpperInvariant();
            var designator = PlacementConstants.JsonStr(item?["designator"]).ToUpperInvariant();
            foreach (var pair in new[] { Tuple.Create("0402", 12.0), Tuple.Create("0603", 18.0), Tuple.Create("0805", 22.0), Tuple.Create("1206", 28.0) })
            {
                if (pattern.Contains(pair.Item1) || comment.Contains(pair.Item1))
                    return pair.Item2;
            }

            if (designator.StartsWith("L", StringComparison.Ordinal))
                return 20.0;
            return 16.0;
        }

        /// <summary>
        /// Courtyard half-size (body + clearance) for collision avoidance. Larger than
        /// the body radius so parts don't overlap. Matches the courtyard estimates in
        /// DesignExporter.EstimateCourtyardHalfSizeMils.
        /// </summary>
        internal static double CourtyardHalfSizeMils(JObject item, JObject pcbComponent)
        {
            var pattern = PlacementConstants.JsonStr(pcbComponent?["pattern"]).ToUpperInvariant();
            var comment = PlacementConstants.JsonStr(item?["comment"]).ToUpperInvariant();
            if (pattern.Contains("0402") || comment.Contains("0402"))
                return 24.0;
            if (pattern.Contains("0603") || comment.Contains("0603"))
                return 32.0;
            if (pattern.Contains("0805") || comment.Contains("0805"))
                return 40.0;
            if (pattern.Contains("1206") || comment.Contains("1206"))
                return 52.0;
            if (pattern.Contains("SOT") || pattern.Contains("SOIC") || comment.Contains("SOT") || comment.Contains("SOIC"))
                return 70.0;
            return 30.0;
        }

        internal static List<Tuple<double, double, double, double>> CollectKeepoutBoxes(JObject data)
        {
            var boxes = new List<Tuple<double, double, double, double>>();
            var regions = data?["pcb"]?["keepouts"]?["regions"] as JArray;
            if (regions == null)
                return boxes;

            foreach (var region in regions.OfType<JObject>())
            {
                // Component courtyards are exported in this collection for
                // diagnostics, but they are not fixed board keepouts. Their current
                // coordinates become stale as soon as the planner moves the part.
                // Actual component bounds are handled separately by PlacementBox.
                if (string.Equals(
                        PlacementConstants.JsonStr(region["kind"]),
                        "component_courtyard",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (region["bboxMils"] is JArray bbox && bbox.Count >= 4)
                {
                    boxes.Add(Tuple.Create(
                        bbox[0].Value<double>(),
                        bbox[1].Value<double>(),
                        bbox[2].Value<double>(),
                        bbox[3].Value<double>()));
                    continue;
                }

                var xVal = TokenDouble(region["xMils"]);
                var yVal = TokenDouble(region["yMils"]);
                if (!xVal.HasValue || !yVal.HasValue)
                    continue;
                var half = TokenDouble(region["radiusMils"]) ?? TokenDouble(region["halfSizeMils"]) ?? 80.0;
                boxes.Add(Tuple.Create(
                    xVal.Value - half,
                    yVal.Value - half,
                    xVal.Value + half,
                    yVal.Value + half));
            }

            return boxes;
        }

        internal static List<Tuple<double, double, double>> CollectPcbObstacles(
            Dictionary<string, JObject> pcbComponents,
            HashSet<string> skip)
        {
            var obstacles = new List<Tuple<double, double, double>>();
            foreach (var kvp in pcbComponents)
            {
                if (skip.Contains(kvp.Key))
                    continue;
                var xy = PlacementConstants.PlacementXy(kvp.Value["placement"]);
                if (xy == null)
                    continue;
                var half = GetBboxHalfSize(kvp.Value);
                var radius = half != null
                    ? Math.Max(half.Item1, half.Item2)
                    : 28.0;
                obstacles.Add(Tuple.Create(xy.Item1, xy.Item2, radius));
            }

            return obstacles;
        }

        internal static double? SuggestPassiveRotationDeg(
            JObject item,
            Tuple<double, double> passiveXy,
            Tuple<double, double> pinXy,
            JObject pcbComponent)
        {
            if (pinXy == null)
                return null;
            var dx = passiveXy.Item1 - pinXy.Item1;
            var dy = passiveXy.Item2 - pinXy.Item2;
            if (Math.Sqrt(dx * dx + dy * dy) < 1e-6)
                return null;

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            var snapped = Math.Round(angle / 90.0) * 90.0 % 360.0;
            var nets = new HashSet<string>(
                (item["nets"] as JArray ?? new JArray()).Select(n => PlacementConstants.JsonStr(n).Trim()),
                StringComparer.OrdinalIgnoreCase);
            // Orient so the non-plane pad faces the IC pin (short surface track); the
            // plane pad (GND/VCC) faces away for a short via to the mid-layer.
            if (nets.Count >= 2 && nets.Any(PlacementConstants.IsPlaneNet))
                snapped = (snapped + 180.0) % 360.0;

            var current = pcbComponent?["placement"] as JObject;
            var currentRot = TokenDouble(current?["rotation"]);
            if (currentRot.HasValue && Math.Abs(currentRot.Value - snapped) <= 45.0)
                return currentRot.Value;
            return snapped;
        }

        internal static bool ShouldUseRfPiTPlacement(
            JObject grouping,
            JObject item,
            Dictionary<string, JObject> chainLookup)
        {
            if (!PlacementConstants.IsRfMatchingAnchor(grouping))
                return false;
            var designator = PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant();
            if (!chainLookup.ContainsKey(designator))
                return false;
            if (PlacementConstants.JsonStr(item["primary_role"]) == "decoupling")
                return false;
            var primaryNet = PlacementConstants.JsonStr(item["primary_net"]).Trim();
            // Plane nets are via targets, not RF matching-chain members.
            if (PlacementConstants.IsPlaneNet(primaryNet) ||
                string.Equals(primaryNet, "XTA", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        internal static bool IsRfShuntToGnd(JObject item)
        {
            var nets = new HashSet<string>(
                (item["nets"] as JArray ?? new JArray()).Select(n => PlacementConstants.JsonStr(n).Trim()),
                StringComparer.OrdinalIgnoreCase);
            return nets.Any(PlacementConstants.IsGndNet) && nets.Count >= 2;
        }

        internal static PlacementTargetResult RfPiTTargetXy(
            JObject item,
            int chainIndex,
            Tuple<double, double> pinXy,
            Tuple<double, double> anchorXy,
            double spacingMils,
            double maxRadiusMils,
            List<Tuple<double, double>> placedPoints,
            JObject pcbComponent,
            List<Tuple<double, double, double, double>> keepoutBoxes,
            List<Tuple<double, double, double>> pcbObstacles,
            List<string> placedLayers = null,
            List<Tuple<double, double>> placedHalfSizes = null)
        {
            var angleDeg = PinOutwardAngleDeg(pinXy, anchorXy);
            var seriesRad = angleDeg * Math.PI / 180.0;
            var baseDist = Math.Max(spacingMils * 0.75, 50.0);
            var bodyRadius = PassiveBodyRadiusMils(item, pcbComponent);
            double x;
            double y;
            string method;
            double standoff;

            if (chainIndex == 0 || !IsRfShuntToGnd(item))
            {
                var along = baseDist + (chainIndex / 2) * Math.Max(spacingMils * 1.0, 65.0);
                x = pinXy.Item1 + along * Math.Cos(seriesRad);
                y = pinXy.Item2 + along * Math.Sin(seriesRad);
                method = "rf_pi_t_series";
                standoff = along;
            }
            else
            {
                var along = baseDist + ((chainIndex - 1) / 2) * Math.Max(spacingMils * 1.0, 65.0);
                var perpSign = chainIndex % 2 == 0 ? -1.0 : 1.0;
                var perp = Math.Max(spacingMils * 0.85, 55.0) * perpSign;
                var centerX = pinXy.Item1 + along * Math.Cos(seriesRad);
                var centerY = pinXy.Item2 + along * Math.Sin(seriesRad);
                var perpRad = seriesRad + Math.PI / 2.0;
                x = centerX + perp * Math.Cos(perpRad);
                y = centerY + perp * Math.Sin(perpRad);
                method = "rf_pi_t_shunt";
                standoff = Math.Sqrt(Math.Pow(x - pinXy.Item1, 2) + Math.Pow(y - pinXy.Item2, 2));
            }

            var courtyard = CourtyardHalfSizeMils(item, pcbComponent);
            var bboxHalf = GetBboxHalfSize(pcbComponent);
            var resolved = ResolveCollision(
                x,
                y,
                spacingMils,
                maxRadiusMils,
                anchorXy,
                placedPoints,
                courtyard,
                keepoutBoxes,
                pcbObstacles,
                "top",
                placedLayers,
                bboxHalf ?? Tuple.Create(courtyard, courtyard),
                placedHalfSizes);
            var rotation = SuggestPassiveRotationDeg(item, resolved, pinXy, pcbComponent);
            return new PlacementTargetResult
            {
                X = resolved.Item1,
                Y = resolved.Item2,
                Method = method,
                TargetPinAngle = angleDeg,
                StandoffMils = standoff,
                PinSlot = chainIndex,
                AngleOffsetDeg = 0.0,
                RotationDeg = rotation,
            };
        }

        internal static Tuple<double, double> SchPinToPcbXy(
            string pinNumber,
            JObject pinLayout,
            Tuple<double, double> anchorSchXy,
            Tuple<double, double> anchorPcbXy,
            double scale)
        {
            if (string.IsNullOrWhiteSpace(pinNumber) || anchorSchXy == null)
                return null;
            if (!(pinLayout[pinNumber.Trim()] is JObject layout))
                return null;
            if (layout["xMils"] == null || layout["yMils"] == null ||
                layout["xMils"].Type == JTokenType.Null || layout["yMils"].Type == JTokenType.Null)
            {
                return null;
            }

            var dx = layout["xMils"].Value<double>() - anchorSchXy.Item1;
            var dy = layout["yMils"].Value<double>() - anchorSchXy.Item2;
            return Tuple.Create(
                anchorPcbXy.Item1 + dx * scale,
                anchorPcbXy.Item2 + dy * scale);
        }

        internal static Tuple<double, double> ResolveCollision(
            double x,
            double y,
            double spacingMils,
            double maxRadiusMils,
            Tuple<double, double> anchorXy,
            List<Tuple<double, double>> placedPoints,
            double bodyRadiusMils = 16.0,
            List<Tuple<double, double, double, double>> keepoutBoxes = null,
            List<Tuple<double, double, double>> pcbObstacles = null,
            string newLayer = null,
            List<string> placedLayers = null,
            Tuple<double, double> newHalfSize = null,
            List<Tuple<double, double>> placedHalfSizes = null)
        {
            // Use the actual bounding box half-sizes for rectangle-rectangle collision
            // if available; fall back to the courtyard radius (circle) if not.
            double newHalfW = newHalfSize?.Item1 ?? bodyRadiusMils;
            double newHalfH = newHalfSize?.Item2 ?? bodyRadiusMils;
            // Middle-ground assembly/routing gap — previous 0.3× left same-layer overlaps.
            var gap = Math.Max(spacingMils * 0.55, 28.0);

            bool Blocked(double testX, double testY)
            {
                for (var i = 0; i < placedPoints.Count; i++)
                {
                    var p = placedPoints[i];
                    if (newLayer != null && placedLayers != null && i < placedLayers.Count)
                    {
                        var existingLayer = placedLayers[i] ?? "top";
                        if (!string.Equals(newLayer, existingLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    double existHalfW = (placedHalfSizes != null && i < placedHalfSizes.Count)
                        ? placedHalfSizes[i].Item1 : bodyRadiusMils;
                    double existHalfH = (placedHalfSizes != null && i < placedHalfSizes.Count)
                        ? placedHalfSizes[i].Item2 : bodyRadiusMils;
                    if (Math.Abs(testX - p.Item1) < (newHalfW + existHalfW + gap) &&
                        Math.Abs(testY - p.Item2) < (newHalfH + existHalfH + gap))
                        return true;
                }
                if (pcbObstacles != null)
                {
                    var obstacleMinSep = Math.Max(newHalfW, newHalfH) + Math.Max(spacingMils, 60.0);
                    foreach (var obstacle in pcbObstacles)
                    {
                        if (Math.Sqrt(Math.Pow(testX - obstacle.Item1, 2) + Math.Pow(testY - obstacle.Item2, 2)) < obstacleMinSep + obstacle.Item3)
                            return true;
                    }
                }

                if (keepoutBoxes != null)
                {
                    foreach (var box in keepoutBoxes)
                    {
                        if (testX >= box.Item1 && testX <= box.Item3 && testY >= box.Item2 && testY <= box.Item4)
                            return true;
                    }
                }

                return false;
            }

            if (Blocked(x, y))
            {
                var dx0 = x - anchorXy.Item1;
                var dy0 = y - anchorXy.Item2;
                var baseRadius = Math.Max(Math.Sqrt(dx0 * dx0 + dy0 * dy0), spacingMils);
                var baseAngle = Math.Atan2(dy0, dx0);
                var radialStep = Math.Max(gap + Math.Max(newHalfW, newHalfH) * 0.35, spacingMils * 0.6);
                var searchLimit = Math.Max(
                    maxRadiusMils > 0 ? maxRadiusMils : baseRadius + radialStep * 20,
                    baseRadius + radialStep * 20);

                var found = false;
                for (var ring = 0; ring < 28 && !found; ring++)
                {
                    var radius = baseRadius + ring * radialStep;
                    if (radius > searchLimit)
                        break;
                    for (var angleStep = 0; angleStep < 28; angleStep++)
                    {
                        var signedStep = angleStep == 0
                            ? 0
                            : ((angleStep % 2 == 1) ? 1 : -1) * ((angleStep + 1) / 2);
                        var angle = baseAngle + signedStep * (Math.PI / 14.0);
                        var tx = anchorXy.Item1 + radius * Math.Cos(angle);
                        var ty = anchorXy.Item2 + radius * Math.Sin(angle);
                        if (!Blocked(tx, ty))
                        {
                            x = tx;
                            y = ty;
                            found = true;
                            break;
                        }
                    }
                }
            }

            placedPoints.Add(Tuple.Create(x, y));
            placedLayers?.Add(newLayer ?? "top");
            placedHalfSizes?.Add(Tuple.Create(newHalfW, newHalfH));
            return Tuple.Create(x, y);
        }

        internal static PlacementTargetResult PinAccurateTargetXy(
            JObject item,
            Tuple<double, double> anchorPcbXy,
            Tuple<double, double> anchorSchXy,
            JObject pinLayout,
            JObject pcbPinIndex,
            double scale,
            double spacingMils,
            double maxRadiusMils,
            Dictionary<Tuple<string, string>, int> pinSlotCounts,
            List<Tuple<double, double>> placedPoints,
            JObject pcbComponent,
            List<Tuple<double, double, double, double>> keepoutBoxes,
            List<Tuple<double, double, double>> pcbObstacles,
            List<string> placedLayers = null,
            List<Tuple<double, double>> placedHalfSizes = null)
        {
            var sch = item["schematic"] as JObject ?? new JObject();
            var role = PlacementConstants.JsonStr(item["primary_role"]);
            if (string.IsNullOrEmpty(role))
                role = "support";
            var pinNumber = PlacementConstants.JsonStr(item["primary_ic_pin"]);
            var pinNet = PlacementConstants.JsonStr(item["primary_net"]);
            if (string.IsNullOrEmpty(pinNet))
                pinNet = "unknown";
            var pinKey = Tuple.Create(string.IsNullOrEmpty(pinNumber) ? "unknown" : pinNumber, pinNet);
            var slot = pinSlotCounts.TryGetValue(pinKey, out var existingSlot) ? existingSlot : 0;
            pinSlotCounts[pinKey] = slot + 1;

            double? targetPinAngle = TokenDouble(sch["pinAngleDeg"]) ?? TokenDouble(sch["angleDeg"]);
            var offsetX = TokenDouble(sch["offsetXMils"]);
            var offsetY = TokenDouble(sch["offsetYMils"]);
            var hasMirror = anchorSchXy != null && offsetX.HasValue && offsetY.HasValue;
            double x;
            double y;
            string method;

            if (hasMirror)
            {
                var scaledX = offsetX.Value * scale;
                var scaledY = offsetY.Value * scale;
                var dist = Math.Sqrt(scaledX * scaledX + scaledY * scaledY);
                if (dist > maxRadiusMils && maxRadiusMils > 0)
                {
                    var clamp = maxRadiusMils / dist;
                    scaledX *= clamp;
                    scaledY *= clamp;
                }

                x = anchorPcbXy.Item1 + scaledX;
                y = anchorPcbXy.Item2 + scaledY;
                method = "pin_accurate_mirror";
            }
            else
            {
                x = anchorPcbXy.Item1;
                y = anchorPcbXy.Item2;
                method = "pin_accurate_fallback";
            }

            var pinXy = ResolveIcPinPcbXy(pinNumber, pcbPinIndex, pinLayout, anchorSchXy, anchorPcbXy, scale);
            var usedPcbPad = !string.IsNullOrWhiteSpace(pinNumber) && pcbPinIndex[pinNumber.Trim()] != null;
            var capRank = CapProximityRank(item);
            var isDecoupling = role == "decoupling";

            // Multilayer (4–6L) boards with internal GND/PWR planes: keep decoupling on TOP
            // beside the IC pin (short surface fanout + via to plane). Bottom-side flip was
            // for 2-layer boards that needed a through-via under the pin.
            var bodyRadius = PassiveBodyRadiusMils(item, pcbComponent);
            double standoff;
            if (isDecoupling)
            {
                // Close to the pin on the top side; slot stacks multiple caps on same pin.
                standoff = bodyRadius + Math.Max(spacingMils * 0.35, 18.0)
                           + slot * spacingMils * 0.55
                           - capRank * spacingMils * 0.12;
                standoff = Math.Max(standoff, bodyRadius + 12.0);
            }
            else
            {
                standoff = PinEdgeStandoffMils(role, spacingMils) + slot * spacingMils * 0.45 - capRank * spacingMils * 0.22;
                standoff = Math.Max(standoff, spacingMils * 0.35);
            }

            if (pinXy != null)
            {
                if (!targetPinAngle.HasValue)
                    targetPinAngle = PinOutwardAngleDeg(pinXy, anchorPcbXy);
                var angleDeg = targetPinAngle.Value + slot * 11.0;
                var angle = angleDeg * Math.PI / 180.0;
                var pinX = pinXy.Item1 + standoff * Math.Cos(angle);
                var pinY = pinXy.Item2 + standoff * Math.Sin(angle);

                if (hasMirror && !usedPcbPad)
                {
                    var blend = role == "decoupling" ? 0.72 : role == "signal" ? 0.58 : 0.5;
                    x = (1.0 - blend) * x + blend * pinX;
                    y = (1.0 - blend) * y + blend * pinY;
                    method = "pin_accurate_blend";
                }
                else
                {
                    var pcbBlend = usedPcbPad ? 0.88 : 0.72;
                    if (hasMirror)
                    {
                        x = (1.0 - pcbBlend) * x + pcbBlend * pinX;
                        y = (1.0 - pcbBlend) * y + pcbBlend * pinY;
                        method = "pin_accurate_pcb_pad";
                    }
                    else
                    {
                        x = pinX;
                        y = pinY;
                        method = usedPcbPad ? "pin_accurate_pcb_pad" : "pin_accurate_pin";
                    }
                }

                var distToAnchor = Math.Sqrt(Math.Pow(x - anchorPcbXy.Item1, 2) + Math.Pow(y - anchorPcbXy.Item2, 2));
                if (distToAnchor > maxRadiusMils && maxRadiusMils > 0)
                {
                    var clamp = maxRadiusMils / distToAnchor;
                    x = anchorPcbXy.Item1 + (x - anchorPcbXy.Item1) * clamp;
                    y = anchorPcbXy.Item2 + (y - anchorPcbXy.Item2) * clamp;
                }
            }
            else if (!targetPinAngle.HasValue)
            {
                targetPinAngle = (slot * 137.508) % 360.0;
            }

            var angleOffsetDeg = slot * 11.0;
            var collisionRadius = CourtyardHalfSizeMils(item, pcbComponent);
            var bboxHalf = GetBboxHalfSize(pcbComponent);
            const string partLayer = "top";
            var resolved = ResolveCollision(
                x, y, spacingMils, maxRadiusMils, anchorPcbXy, placedPoints, collisionRadius, keepoutBoxes, pcbObstacles,
                partLayer, placedLayers, bboxHalf ?? Tuple.Create(collisionRadius, collisionRadius), placedHalfSizes);
            x = resolved.Item1;
            y = resolved.Item2;
            var standoffMils = pinXy != null
                ? Math.Sqrt(Math.Pow(x - pinXy.Item1, 2) + Math.Pow(y - pinXy.Item2, 2))
                : Math.Sqrt(Math.Pow(x - anchorPcbXy.Item1, 2) + Math.Pow(y - anchorPcbXy.Item2, 2));
            var rotation = SuggestPassiveRotationDeg(item, Tuple.Create(x, y), pinXy, pcbComponent);

            return new PlacementTargetResult
            {
                X = x,
                Y = y,
                Method = isDecoupling ? method + "_top_decap" : method,
                TargetPinAngle = targetPinAngle,
                StandoffMils = standoffMils,
                PinSlot = slot,
                AngleOffsetDeg = angleOffsetDeg,
                RotationDeg = rotation,
                Layer = "top",
                Mirror = false,
            };
        }

        internal static string NormalizeLayoutMode(string layoutMode)
        {
            var mode = (layoutMode ?? "pin_accurate").ToLowerInvariant();
            if (mode == "pin" || mode == "pin_proximity" || mode == "pin-proximity" || mode == "pinproximity")
                return "pin_near";
            if (mode == "accurate" || mode == "schematic_pin" || mode == "pin-accurate")
                return "pin_accurate";
            return mode;
        }

        private static double? TokenDouble(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<double>();
            if (double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }
    }
}
