using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EasyEDA_Loader.Placement;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Floorplan
{
    internal enum FloorplanRole
    {
        Connector,
        Rf,
        Power,
        Mcu,
        OtherIc,
        Crystal,
        Passive,
        Skip,
    }

    internal sealed class FloorplanPart
    {
        public string Designator { get; set; }
        public FloorplanRole Role { get; set; }
        public string Comment { get; set; }
        public string Sheet { get; set; }
        public double WidthMils { get; set; }
        public double HeightMils { get; set; }
        public double CurrentXMils { get; set; }
        public double CurrentYMils { get; set; }
        public double Rotation { get; set; }
        public string Layer { get; set; }

        // Assigned by a layout variant
        public double TargetXMils { get; set; }
        public double TargetYMils { get; set; }
        public double TargetRotation { get; set; }
        public string ZoneName { get; set; }
    }

    internal sealed class FloorplanZone
    {
        public string Name { get; set; }
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }
        public string BrushKey { get; set; } // for preview coloring
    }

    internal sealed class FloorplanVariant
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public BoardOutlineSpec Board { get; set; }
        public List<FloorplanZone> Zones { get; set; } = new List<FloorplanZone>();
        public List<FloorplanPart> Parts { get; set; } = new List<FloorplanPart>();

        public JObject ToPlacementPlan()
        {
            var moves = new JArray();
            var anchors = new JArray();
            foreach (var p in Parts.Where(x => x.Role != FloorplanRole.Passive && x.Role != FloorplanRole.Skip))
            {
                moves.Add(new JObject
                {
                    ["designator"] = p.Designator,
                    ["anchor"] = "BOARD",
                    ["xMils"] = Math.Round(p.TargetXMils, 2),
                    ["yMils"] = Math.Round(p.TargetYMils, 2),
                    ["rotation"] = Math.Round(p.TargetRotation, 1),
                    ["layer"] = string.IsNullOrWhiteSpace(p.Layer) ? "top" : p.Layer,
                    ["role"] = p.Role.ToString(),
                    ["zone"] = p.ZoneName ?? "",
                });
                if (p.Role == FloorplanRole.Mcu || p.Role == FloorplanRole.Rf ||
                    p.Role == FloorplanRole.Power || p.Role == FloorplanRole.OtherIc)
                {
                    anchors.Add(p.Designator);
                }
            }

            return new JObject
            {
                ["schemaVersion"] = PlacementConstants.PlanSchemaVersion,
                ["mode"] = "floorplan",
                ["anchor"] = "BOARD",
                ["variant"] = Id,
                ["title"] = Title,
                ["board"] = new JObject
                {
                    ["source"] = Board.Source.ToString(),
                    ["widthMils"] = Board.WidthMils,
                    ["heightMils"] = Board.HeightMils,
                    ["label"] = Board.Label,
                },
                ["anchors"] = anchors,
                ["cluster_count"] = anchors.Count,
                ["moves"] = moves,
                ["generatedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
        }
    }

    /// <summary>
    /// Rule-based board floorplanner: classifies ICs/connectors and builds several layout variants.
    /// Passives stay for Auto-Place after the user picks a layout.
    /// </summary>
    internal static class FloorplanGenerator
    {
        private static readonly Regex ConnectorPattern = new Regex(
            @"^(J|P|H|CN|CON|USB|ANT|XH|SW|BTN)\d*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CrystalPattern = new Regex(
            @"^(Y|X|XTAL)\d*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] PowerCommentTokens =
        {
            "LDO", "BUCK", "BOOST", "REGULATOR", "DCDC", "DC-DC", "AMS1117", "LM1117",
            "MP1584", "TPS", "AP2112", "XC620", "ME621", "POWER",
        };

        private static readonly string[] McuCommentTokens =
        {
            "ESP32", "ESP8266", "STM32", "ATMEGA", "ATTINY", "RP2040", "NRF52", "NRF91",
            "PIC18", "PIC32", "SAMD", "GD32", "CH32", "MCU", "MICROCONTROLLER",
        };

        public static BoardOutlineSpec BuildAutoBoard(IReadOnlyList<FloorplanPart> parts, double aspect = 1.4)
        {
            // Courtyard area of placeable parts + padding for routing/keepout.
            double area = 0;
            foreach (var p in parts.Where(IsFloorplanTarget))
                area += Math.Max(80, p.WidthMils) * Math.Max(80, p.HeightMils);

            area = Math.Max(area * 3.2, 800 * 600); // room for passives + routing
            var height = Math.Sqrt(area / Math.Max(0.6, aspect));
            var width = height * aspect;
            // Snap to nice mils
            width = Math.Ceiling(width / 50.0) * 50.0;
            height = Math.Ceiling(height / 50.0) * 50.0;
            width = Math.Max(1000, Math.Min(6000, width));
            height = Math.Max(800, Math.Min(5000, height));
            return BoardOutlineSpec.FromRectangle(width, height, BoardOutlineSource.Auto, "Auto size");
        }

        public static List<FloorplanPart> LoadPartsFromConnectivity(string connectivityPath = null)
        {
            connectivityPath ??= DesignExporter.DefaultExportPath;
            if (!File.Exists(connectivityPath))
            {
                DesignExporter.ExportForPlacementPlanning(connectivityPath);
            }

            var root = JObject.Parse(File.ReadAllText(connectivityPath));
            var map = new Dictionary<string, FloorplanPart>(StringComparer.OrdinalIgnoreCase);

            // Prefer PCB geometry; fall back to schematic component list.
            foreach (var token in root.SelectTokens("..components[*]").OfType<JObject>())
            {
                var des = (token.Value<string>("designator") ?? token.Value<string>("Designator") ?? "").Trim();
                if (string.IsNullOrEmpty(des))
                    continue;

                FloorplanPart part;
                if (!map.TryGetValue(des, out part))
                {
                    part = new FloorplanPart { Designator = des };
                    map[des] = part;
                }

                var comment = token.Value<string>("comment") ?? token.Value<string>("Comment") ?? part.Comment;
                part.Comment = comment ?? "";
                var sheet = token.Value<string>("sheet");
                if (!string.IsNullOrWhiteSpace(sheet))
                    part.Sheet = sheet;

                var placement = token["placement"] as JObject;
                var xy = PlacementConstants.PlacementXy(placement);
                if (xy != null)
                {
                    part.CurrentXMils = xy.Item1;
                    part.CurrentYMils = xy.Item2;
                }

                if (placement != null)
                {
                    if (placement["rotation"] != null)
                        part.Rotation = placement.Value<double?>("rotation") ?? part.Rotation;
                    var layer = placement.Value<string>("layer");
                    if (!string.IsNullOrWhiteSpace(layer))
                        part.Layer = layer;
                }

                var bbox = token["bboxMils"] as JArray;
                if (bbox != null && bbox.Count >= 4)
                {
                    var x1 = bbox[0].Value<double>();
                    var y1 = bbox[1].Value<double>();
                    var x2 = bbox[2].Value<double>();
                    var y2 = bbox[3].Value<double>();
                    part.WidthMils = Math.Max(40, Math.Abs(x2 - x1));
                    part.HeightMils = Math.Max(40, Math.Abs(y2 - y1));
                }
                else if (part.WidthMils <= 0)
                {
                    part.WidthMils = EstimateSize(des);
                    part.HeightMils = EstimateSize(des);
                }
            }

            // Also scan pcb.components explicitly if present
            var pcbComps = root.SelectToken("pcb.components") as JArray;
            if (pcbComps != null)
            {
                foreach (var token in pcbComps.OfType<JObject>())
                {
                    var des = (token.Value<string>("designator") ?? "").Trim();
                    if (string.IsNullOrEmpty(des))
                        continue;
                    FloorplanPart part;
                    if (!map.TryGetValue(des, out part))
                    {
                        part = new FloorplanPart { Designator = des };
                        map[des] = part;
                    }

                    var placement = token["placement"] as JObject;
                    var xy = PlacementConstants.PlacementXy(placement);
                    if (xy != null)
                    {
                        part.CurrentXMils = xy.Item1;
                        part.CurrentYMils = xy.Item2;
                    }

                    var bbox = token["bboxMils"] as JArray;
                    if (bbox != null && bbox.Count >= 4)
                    {
                        part.WidthMils = Math.Max(40, Math.Abs(bbox[2].Value<double>() - bbox[0].Value<double>()));
                        part.HeightMils = Math.Max(40, Math.Abs(bbox[3].Value<double>() - bbox[1].Value<double>()));
                    }

                    if (string.IsNullOrWhiteSpace(part.Comment))
                        part.Comment = token.Value<string>("comment") ?? "";
                }
            }

            foreach (var part in map.Values)
                part.Role = Classify(part);

            return map.Values
                .OrderBy(p => p.Designator, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<FloorplanVariant> GenerateVariants(BoardOutlineSpec board, IReadOnlyList<FloorplanPart> parts)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));
            if (parts == null || parts.Count == 0)
                throw new InvalidOperationException("No components found. Export connectivity / open the PCB first.");

            var targets = parts.Where(IsFloorplanTarget).Select(ClonePart).ToList();
            if (targets.Count == 0)
                throw new InvalidOperationException("No ICs or connectors found to floorplan.");

            var variants = new List<FloorplanVariant>
            {
                BuildVariant("rf_corner",
                    "RF quiet corner (top-right)",
                    "Connectors left, power near entry, MCU center, RF top-right quiet corner.",
                    board, targets,
                    connectorEdge: "left", rfCorner: "top-right", powerNear: "left"),

                BuildVariant("rf_corner_tl",
                    "RF quiet corner (top-left)",
                    "Connectors right, MCU center, RF top-left — mirror for antenna on the left.",
                    board, targets,
                    connectorEdge: "right", rfCorner: "top-left", powerNear: "right"),

                BuildVariant("signal_flow",
                    "Left → right signal flow",
                    "Connectors left, power mid-left, MCU center, RF on the right (antenna end).",
                    board, targets,
                    connectorEdge: "left", rfCorner: "right", powerNear: "left"),

                BuildVariant("signal_flow_rtl",
                    "Right → left signal flow",
                    "Connectors right, MCU center, RF on the left.",
                    board, targets,
                    connectorEdge: "right", rfCorner: "left", powerNear: "right"),

                BuildVariant("connector_bottom",
                    "Connectors on bottom",
                    "I/O along bottom edge, power above, MCU center, RF top-left.",
                    board, targets,
                    connectorEdge: "bottom", rfCorner: "top-left", powerNear: "bottom"),

                BuildVariant("connector_top",
                    "Connectors on top",
                    "I/O along top edge (panel / edge connector), MCU center, RF bottom-right.",
                    board, targets,
                    connectorEdge: "top", rfCorner: "bottom-right", powerNear: "top"),

                BuildVariant("power_edge",
                    "Power entry left, RF far right",
                    "Maximize separation: power+connectors left, MCU mid, RF far quiet right.",
                    board, targets,
                    connectorEdge: "left", rfCorner: "right", powerNear: "left"),

                BuildVariant("dual_io",
                    "Dual I/O edges",
                    "Connectors split toward bottom; RF top-right; MCU/power center-left.",
                    board, targets,
                    connectorEdge: "bottom", rfCorner: "top-right", powerNear: "left"),

                BuildVariant("compact",
                    "Compact centered",
                    "Tighter packing toward center; connectors still on nearest edge.",
                    board, targets,
                    connectorEdge: "left", rfCorner: "top-right", powerNear: "left", compact: true),

                BuildVariant("compact_rf",
                    "Compact + RF priority",
                    "Smaller board packing but RF zone kept large in top-right.",
                    board, targets,
                    connectorEdge: "bottom", rfCorner: "top-right", powerNear: "left", compact: true),
            };

            return variants;
        }

        private static FloorplanVariant BuildVariant(
            string id,
            string title,
            string description,
            BoardOutlineSpec board,
            List<FloorplanPart> templateParts,
            string connectorEdge,
            string rfCorner,
            string powerNear,
            bool compact = false)
        {
            var margin = compact ? 60.0 : 100.0;
            var w = board.WidthMils;
            var h = board.HeightMils;
            var parts = templateParts.Select(ClonePart).ToList();

            // Zone rectangles (normalized 0..1 inside usable area).
            ZoneRect connZ, pwrZ, mcuZ, rfZ, otherZ;
            DefineZones(w, h, margin, connectorEdge, rfCorner, powerNear, compact,
                out connZ, out pwrZ, out mcuZ, out rfZ, out otherZ);

            var zones = new List<FloorplanZone>
            {
                MakeZone("Connectors", connZ, "conn"),
                MakeZone("Power", pwrZ, "pwr"),
                MakeZone("MCU / Logic", mcuZ, "mcu"),
                MakeZone("RF", rfZ, "rf"),
                MakeZone("Other", otherZ, "other"),
            };

            PlaceGroup(parts.Where(p => p.Role == FloorplanRole.Connector).ToList(), connZ, connectorEdge, "Connectors");
            PlaceGroup(parts.Where(p => p.Role == FloorplanRole.Power).ToList(), pwrZ, "grid", "Power");
            PlaceGroup(parts.Where(p => p.Role == FloorplanRole.Mcu).ToList(), mcuZ, "grid", "MCU / Logic");
            PlaceGroup(parts.Where(p => p.Role == FloorplanRole.Rf).ToList(), rfZ, "grid", "RF");

            var otherParts = parts.Where(p => p.Role == FloorplanRole.OtherIc || p.Role == FloorplanRole.Crystal).ToList();
            if (!parts.Any(p => p.Role == FloorplanRole.Mcu))
                PlaceGroup(otherParts, mcuZ, "grid", "MCU / Logic");
            else
                PlaceGroup(otherParts, otherZ, "grid", "Other");

            return new FloorplanVariant
            {
                Id = id,
                Title = title,
                Description = description,
                Board = board,
                Zones = zones,
                Parts = parts,
            };
        }

        private struct ZoneRect
        {
            public double L, B, R, T;
            public ZoneRect(double l, double b, double r, double t) { L = l; B = b; R = r; T = t; }
            public double Cx => (L + R) * 0.5;
            public double Cy => (B + T) * 0.5;
            public double W => Math.Max(1, R - L);
            public double H => Math.Max(1, T - B);
        }

        private static void DefineZones(
            double w, double h, double margin,
            string connectorEdge, string rfCorner, string powerNear, bool compact,
            out ZoneRect conn, out ZoneRect pwr, out ZoneRect mcu, out ZoneRect rf, out ZoneRect other)
        {
            var usableL = margin;
            var usableB = margin;
            var usableR = w - margin;
            var usableT = h - margin;
            var uw = usableR - usableL;
            var uh = usableT - usableB;
            var strip = compact ? 0.16 : 0.20;

            if (string.Equals(connectorEdge, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                conn = new ZoneRect(usableL, usableB, usableR, usableB + uh * (compact ? 0.18 : 0.22));
            }
            else if (string.Equals(connectorEdge, "top", StringComparison.OrdinalIgnoreCase))
            {
                conn = new ZoneRect(usableL, usableT - uh * (compact ? 0.18 : 0.22), usableR, usableT);
            }
            else if (string.Equals(connectorEdge, "right", StringComparison.OrdinalIgnoreCase))
            {
                conn = new ZoneRect(usableR - uw * strip, usableB, usableR, usableT);
            }
            else // left
            {
                conn = new ZoneRect(usableL, usableB, usableL + uw * strip, usableT);
            }

            // Power near the named edge
            if (string.Equals(powerNear, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                pwr = new ZoneRect(usableL, Math.Max(usableB, conn.T), usableL + uw * 0.45,
                    Math.Max(usableB, conn.T) + uh * 0.22);
            }
            else if (string.Equals(powerNear, "top", StringComparison.OrdinalIgnoreCase))
            {
                pwr = new ZoneRect(usableL, Math.Min(usableT, conn.B) - uh * 0.22, usableL + uw * 0.45,
                    Math.Min(usableT, conn.B));
            }
            else if (string.Equals(powerNear, "right", StringComparison.OrdinalIgnoreCase))
            {
                pwr = new ZoneRect(conn.L - uw * 0.22, usableB + uh * 0.15, conn.L, usableB + uh * 0.55);
                if (pwr.L < usableL)
                    pwr = new ZoneRect(usableL, usableB + uh * 0.15, usableL + uw * 0.22, usableB + uh * 0.55);
            }
            else // left
            {
                pwr = new ZoneRect(conn.R, usableB + uh * 0.15, conn.R + uw * 0.22, usableB + uh * 0.55);
                if (pwr.R > usableR)
                    pwr = new ZoneRect(usableL + uw * 0.2, usableB + uh * 0.15, usableL + uw * 0.42, usableB + uh * 0.55);
            }

            var rfW = uw * (compact ? 0.28 : 0.32);
            var rfH = uh * (compact ? 0.28 : 0.34);
            var corner = (rfCorner ?? "top-right").ToLowerInvariant();
            if (corner == "right")
                rf = new ZoneRect(usableR - rfW, usableB + uh * 0.2, usableR, usableT - uh * 0.1);
            else if (corner == "left")
                rf = new ZoneRect(usableL, usableB + uh * 0.2, usableL + rfW, usableT - uh * 0.1);
            else if (corner == "top-left")
                rf = new ZoneRect(usableL + uw * 0.12, usableT - rfH, usableL + uw * 0.12 + rfW, usableT);
            else if (corner == "bottom-right")
                rf = new ZoneRect(usableR - rfW, usableB, usableR, usableB + rfH);
            else if (corner == "bottom-left")
                rf = new ZoneRect(usableL + uw * 0.12, usableB, usableL + uw * 0.12 + rfW, usableB + rfH);
            else // top-right
                rf = new ZoneRect(usableR - rfW, usableT - rfH, usableR, usableT);

            mcu = new ZoneRect(
                usableL + uw * (compact ? 0.28 : 0.30),
                usableB + uh * (compact ? 0.28 : 0.30),
                usableL + uw * (compact ? 0.68 : 0.65),
                usableB + uh * (compact ? 0.68 : 0.70));

            // Nudge MCU if it overlaps RF heavily
            if (mcu.R > rf.L && mcu.T > rf.B && mcu.L < rf.R && mcu.B < rf.T)
            {
                mcu = new ZoneRect(
                    usableL + uw * 0.22,
                    usableB + uh * 0.25,
                    Math.Min(rf.L - 20, usableL + uw * 0.58),
                    usableB + uh * 0.65);
            }

            other = new ZoneRect(
                usableL + uw * 0.30,
                usableB + uh * 0.08,
                Math.Min(usableR - 40, rf.L - 10),
                Math.Max(mcu.B - 20, usableB + uh * 0.2));

            if (other.T <= other.B + 40 || other.R <= other.L + 40)
                other = new ZoneRect(mcu.L, usableB + margin, mcu.R, Math.Max(mcu.B - 10, usableB + 80));
        }

        private static FloorplanZone MakeZone(string name, ZoneRect r, string brush) =>
            new FloorplanZone
            {
                Name = name,
                Left = r.L,
                Bottom = r.B,
                Right = r.R,
                Top = r.T,
                BrushKey = brush,
            };

        private static void PlaceGroup(List<FloorplanPart> group, ZoneRect zone, string style, string zoneName)
        {
            if (group == null || group.Count == 0)
                return;

            group = group.OrderBy(p => p.Designator, StringComparer.OrdinalIgnoreCase).ToList();
            var gap = 80.0;

            if (string.Equals(style, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                var x = zone.L + 40;
                var y = zone.B + zone.H * 0.45;
                foreach (var p in group)
                {
                    p.TargetXMils = x + p.WidthMils * 0.5;
                    p.TargetYMils = y;
                    p.TargetRotation = 0;
                    p.ZoneName = zoneName;
                    x += p.WidthMils + gap;
                    if (x + p.WidthMils > zone.R)
                    {
                        x = zone.L + 40;
                        y += Math.Max(p.HeightMils, 120) + gap * 0.5;
                    }
                }

                return;
            }

            if (string.Equals(style, "top", StringComparison.OrdinalIgnoreCase))
            {
                var x = zone.L + 40;
                var y = zone.T - zone.H * 0.45;
                foreach (var p in group)
                {
                    p.TargetXMils = x + p.WidthMils * 0.5;
                    p.TargetYMils = y;
                    p.TargetRotation = 0;
                    p.ZoneName = zoneName;
                    x += p.WidthMils + gap;
                    if (x + p.WidthMils > zone.R)
                    {
                        x = zone.L + 40;
                        y -= Math.Max(p.HeightMils, 120) + gap * 0.5;
                    }
                }

                return;
            }

            if (string.Equals(style, "right", StringComparison.OrdinalIgnoreCase))
            {
                var x = zone.R - zone.W * 0.45;
                var y = zone.T - 40;
                foreach (var p in group)
                {
                    p.TargetXMils = x;
                    p.TargetYMils = y - p.HeightMils * 0.5;
                    p.TargetRotation = 0;
                    p.ZoneName = zoneName;
                    y -= p.HeightMils + gap;
                    if (y - p.HeightMils < zone.B)
                    {
                        y = zone.T - 40;
                        x -= Math.Max(p.WidthMils, 120) + gap * 0.5;
                    }
                }

                return;
            }

            if (string.Equals(style, "left", StringComparison.OrdinalIgnoreCase))
            {
                var x = zone.L + zone.W * 0.45;
                var y = zone.T - 40;
                foreach (var p in group)
                {
                    p.TargetXMils = x;
                    p.TargetYMils = y - p.HeightMils * 0.5;
                    p.TargetRotation = 0;
                    p.ZoneName = zoneName;
                    y -= p.HeightMils + gap;
                    if (y - p.HeightMils < zone.B)
                    {
                        y = zone.T - 40;
                        x += Math.Max(p.WidthMils, 120) + gap * 0.5;
                    }
                }

                return;
            }

            // Grid pack inside zone
            var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(group.Count)));
            var cellW = zone.W / cols;
            var rows = (int)Math.Ceiling(group.Count / (double)cols);
            var cellH = zone.H / Math.Max(1, rows);
            for (var i = 0; i < group.Count; i++)
            {
                var col = i % cols;
                var row = i / cols;
                var p = group[i];
                p.TargetXMils = zone.L + cellW * (col + 0.5);
                p.TargetYMils = zone.T - cellH * (row + 0.5);
                p.TargetRotation = p.Rotation;
                p.ZoneName = zoneName;
            }
        }

        public static FloorplanRole Classify(FloorplanPart part)
        {
            var des = part.Designator ?? "";
            var comment = (part.Comment ?? "").ToUpperInvariant();

            if (PlacementConstants.IsPassiveDesignator(des) &&
                !CrystalPattern.IsMatch(des))
                return FloorplanRole.Passive;

            if (ConnectorPattern.IsMatch(des) ||
                comment.Contains("CONNECTOR") || comment.Contains("HEADER") ||
                comment.Contains("USB") || comment.Contains("ANTENNA"))
                return FloorplanRole.Connector;

            if (CrystalPattern.IsMatch(des) || comment.Contains("CRYSTAL") || comment.Contains("OSCILLATOR"))
                return FloorplanRole.Crystal;

            var isIc = PlacementConstants.IcDesignatorPattern.IsMatch(des) ||
                       des.StartsWith("Q", StringComparison.OrdinalIgnoreCase);

            if (!isIc)
            {
                if (des.StartsWith("D", StringComparison.OrdinalIgnoreCase) ||
                    des.StartsWith("LED", StringComparison.OrdinalIgnoreCase) ||
                    des.StartsWith("TP", StringComparison.OrdinalIgnoreCase) ||
                    des.StartsWith("MH", StringComparison.OrdinalIgnoreCase) ||
                    des.StartsWith("FID", StringComparison.OrdinalIgnoreCase))
                    return FloorplanRole.Skip;
                return FloorplanRole.Skip;
            }

            // RF transceiver
            foreach (var token in PlacementConstants.RfTransceiverCommentTokens)
            {
                if (comment.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return FloorplanRole.Rf;
            }

            if (comment.Contains("LORA") || comment.Contains("TRANSCEIVER") || comment.Contains("RF "))
                return FloorplanRole.Rf;

            foreach (var token in PowerCommentTokens)
            {
                if (comment.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return FloorplanRole.Power;
            }

            foreach (var token in McuCommentTokens)
            {
                if (comment.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return FloorplanRole.Mcu;
            }

            // Heuristic: ESP modules often named U1 with ESP in comment already caught;
            // first U often MCU if nothing else.
            return FloorplanRole.OtherIc;
        }

        private static bool IsFloorplanTarget(FloorplanPart p) =>
            p != null &&
            p.Role != FloorplanRole.Passive &&
            p.Role != FloorplanRole.Skip;

        private static FloorplanPart ClonePart(FloorplanPart p) =>
            new FloorplanPart
            {
                Designator = p.Designator,
                Role = p.Role,
                Comment = p.Comment,
                Sheet = p.Sheet,
                WidthMils = p.WidthMils,
                HeightMils = p.HeightMils,
                CurrentXMils = p.CurrentXMils,
                CurrentYMils = p.CurrentYMils,
                Rotation = p.Rotation,
                Layer = p.Layer,
                TargetXMils = p.TargetXMils,
                TargetYMils = p.TargetYMils,
                TargetRotation = p.TargetRotation,
                ZoneName = p.ZoneName,
            };

        private static double EstimateSize(string des)
        {
            if (string.IsNullOrEmpty(des))
                return 200;
            if (ConnectorPattern.IsMatch(des))
                return 280;
            if (PlacementConstants.IcDesignatorPattern.IsMatch(des))
                return 350;
            return 120;
        }
    }
}
