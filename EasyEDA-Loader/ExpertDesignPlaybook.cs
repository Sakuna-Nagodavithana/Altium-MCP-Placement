using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader
{
    /// <summary>
    /// How experienced PCB engineers actually work (schematic → fab), distilled so the
    /// plugin can coach the same process instead of only offering disconnected tools.
    /// Sources: Altium RF layout guides, JLCPCB layout process, Analog Devices mixed-signal,
    /// Sierra/Cadence high-speed routing practices.
    /// </summary>
    internal static class ExpertDesignPlaybook
    {
        public sealed class Phase
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string WhatExpertsDo { get; set; }
            public string WhyItMatters { get; set; }
            public string HowPluginHelps { get; set; }
            public string YouStillDoInAltium { get; set; }
        }

        /// <summary>Routing order experts use once placement + planes exist.</summary>
        public static readonly string[] RouteClassOrder = { "RF", "HighSpeed", "PWR", "Logic" };

        public static IReadOnlyList<Phase> Phases { get; } = new List<Phase>
        {
            new Phase
            {
                Id = "schematic",
                Title = "A. Schematic like the board already exists",
                WhatExpertsDo =
                    "Draw left→right signal flow. Group RF / analog / digital / power on the sheet the way they will sit on the board. " +
                    "Name power nets clearly (GND, 3v3, +5). Put matching networks and antennas as short chains. " +
                    "Add design rules notes (impedance, differential pairs) in the schematic before layout.",
                WhyItMatters =
                    "Layout mirrors the schematic. A messy sheet forces long RF jumps and crossed domains on the PCB.",
                HowPluginHelps =
                    "EasyEDA/JLCPCB part import + BOM so footprints match fab. MCP export lets AI check connectivity/ERC markers.",
                YouStillDoInAltium =
                    "Compile project, run Schematic ERC, fix floating pins / missing power flags, annotate.",
            },
            new Phase
            {
                Id = "stackup_rules",
                Title = "B. Stackup + rules BEFORE placement (non-negotiable)",
                WhatExpertsDo =
                    "Pick fab stackup first (JLCPCB 4L JLC04161H-7628 for ESP/LoRa). Decide planes: Mid1=GND, Mid2=power. " +
                    "Set net classes and widths: RF≈50Ω, power thick, logic default. Import fab min clearance/via rules.",
                WhyItMatters =
                    "Width/impedance and return paths depend on dielectric. Changing stackup after routing = redo copper.",
                HowPluginHelps =
                    "Stackup Advisor (copy .stackupx + .RUL). Setup Net Classes & Rules (RF/PWR/HighSpeed/Logic).",
                YouStillDoInAltium =
                    "Layer Stack Manager → Load Stackup From File. Confirm Mid layers. Optional: load .RUL fab rules.",
            },
            new Phase
            {
                Id = "floorplan",
                Title = "C. Floorplan blocks, then place parts",
                WhatExpertsDo =
                    "Partition into rooms (MCU, RF, power, connectors). Place connectors on edges, RF in a quiet corner, " +
                    "power entry near the connector. Then place ICs and pull passives in: decoupling closest to pins, " +
                    "then matching networks, then other support. Create Altium Rooms so each block stays together.",
                WhyItMatters =
                    "Experts spend more time on placement than routing. Bad placement cannot be fixed by clever traces. Rooms enforce the floorplan.",
                HowPluginHelps =
                    "Floorplan Preview: compare layouts, set board size (auto / mm / DXF), Apply moves ICs+connectors. " +
                    "Then Auto-Place (pin-accurate decaps) → Rooms + Unions. Fanout Decap Vias. Optimize Board.",
                YouStillDoInAltium =
                    "Mounting holes, antenna keepout. Nudge RF chain + connectors by hand. Interactive Fanout for BGAs.",
            },
            new Phase
            {
                Id = "planes",
                Title = "D. Pour planes early (not last)",
                WhatExpertsDo =
                    "Pour solid GND under RF before routing RF. Pour power plane. Keep GND continuous — no slots under RF/clocks. " +
                    "Stitch GND with vias near RF and around board edges later.",
                WhyItMatters =
                    "RF and high-speed need a continuous return plane. Routing first then 'adding ground later' creates antennas.",
                HowPluginHelps =
                    "Workflow checklist reminds planes before route. Via Stitch after RF copper exists.",
                YouStillDoInAltium =
                    "Polygon Pour Manager: Mid1 GND, Mid2 3v3, Top/Bottom GND pours where useful. Antenna keepout.",
            },
            new Phase
            {
                Id = "route",
                Title = "E. Route by priority (never random)",
                WhatExpertsDo =
                    "1) RF / antenna matching (controlled impedance, coplanar GND, short). " +
                    "2) Clocks, USB, high-speed diffs. " +
                    "3) Power feeders from regulator to loads (wide). " +
                    "4) Everything else (logic). " +
                    "Use Interactive Routing with net classes on. Fan out dense packages first. Avoid acute angles / neckdowns below fab min.",
                WhyItMatters =
                    "Critical nets own the real estate. Leftover space is for GPIO — not the other way around.",
                HowPluginHelps =
                    "Route Priority list (from net classes). Full DRC catches neckdown / pad↔track / power clearance.",
                YouStillDoInAltium =
                    "Interactive Routing, differential pairs, length tune if needed. No auto-route for RF.",
            },
            new Phase
            {
                Id = "verify",
                Title = "F. Verify like fab will fail you",
                WhatExpertsDo =
                    "Run DRC until clean. Visually inspect power under pads, USB, RF return. Stitch vias. " +
                    "Generate Gerbers/BOM only after DRC pass. Re-check stackup template matches the order form.",
                WhyItMatters =
                    "Most fab shorts are clearance / pour / EPAD mistakes that rushed DRC misses.",
                HowPluginHelps =
                    "Full PCB DRC (Altium + MCP extras) with Jump/Re-Run. Export for MCP review. Via stitch.",
                YouStillDoInAltium =
                    "Fabrication Outputs (Gerber/drill), Assembly (PnP), final 3D check.",
            },
        };

        public static string FormatFullPlaybook()
        {
            var sb = new StringBuilder();
            sb.AppendLine("HOW EXPERTS DESIGN A BOARD (plugin-aligned)");
            sb.AppendLine("==========================================");
            sb.AppendLine("Humans do NOT: dump parts → auto-route everything → hope DRC is green.");
            sb.AppendLine("Humans DO: schematic for layout → stackup/rules → floorplan → planes → priority route → verify.");
            sb.AppendLine();
            foreach (var p in Phases)
            {
                sb.AppendLine(p.Title);
                sb.AppendLine("  Experts:  " + p.WhatExpertsDo);
                sb.AppendLine("  Why:      " + p.WhyItMatters);
                sb.AppendLine("  Plugin:   " + p.HowPluginHelps);
                sb.AppendLine("  You:      " + p.YouStillDoInAltium);
                sb.AppendLine();
            }
            sb.AppendLine("ROUTING ORDER: " + string.Join(" → ", RouteClassOrder) + " → leftover.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Builds the expert routing checklist from current net-class classification.
    /// </summary>
    internal static class RoutingPriorityGuide
    {
        public sealed class Entry
        {
            public int Order { get; set; }
            public string NetClass { get; set; }
            public string Net { get; set; }
            public string Tip { get; set; }
        }

        public static List<Entry> Build()
        {
            var tips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RF"] = "Short, 50Ω, over solid GND, coplanar GND + stitch vias after.",
                ["HighSpeed"] = "Clocks/USB/diffs first; avoid stubs; reference continuous GND.",
                ["PWR"] = "Wide feeders from regulator; no thin necks under pads; pour Mid2.",
                ["Logic"] = "Fill last; don't cut RF/power return paths.",
            };

            Dictionary<string, List<string>> assignments;
            try
            {
                assignments = PcbDesignRulesSetup.Classify(useConnectivityHints: true).NetClassAssignments
                              ?? new Dictionary<string, List<string>>();
            }
            catch
            {
                assignments = new Dictionary<string, List<string>>();
            }

            var list = new List<Entry>();
            var order = 1;
            foreach (var cls in ExpertDesignPlaybook.RouteClassOrder)
            {
                if (!assignments.TryGetValue(cls, out var nets) || nets == null || nets.Count == 0)
                    continue;
                foreach (var net in nets.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(new Entry
                    {
                        Order = order++,
                        NetClass = cls,
                        Net = net,
                        Tip = tips.TryGetValue(cls, out var t) ? t : string.Empty,
                    });
                }
            }

            return list;
        }

        public static string FormatText(IEnumerable<Entry> entries = null)
        {
            var list = (entries ?? Build()).ToList();
            if (list.Count == 0)
            {
                return "No classified nets yet. Run Setup Net Classes & Rules first, then reopen Route Priority.\n" +
                       "Expert order: RF → HighSpeed → PWR → Logic.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Route these nets in order ({list.Count} nets). Experts finish each class before the next.");
            sb.AppendLine();
            string lastClass = null;
            foreach (var e in list)
            {
                if (!string.Equals(lastClass, e.NetClass, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"—— {e.NetClass} ——  {e.Tip}");
                    lastClass = e.NetClass;
                }
                sb.AppendLine($"  {e.Order,3}. {e.Net}");
            }
            sb.AppendLine();
            sb.AppendLine("In Altium: Interactive Routing with the matching net class selected. No auto-route for RF.");
            return sb.ToString();
        }
    }
}
