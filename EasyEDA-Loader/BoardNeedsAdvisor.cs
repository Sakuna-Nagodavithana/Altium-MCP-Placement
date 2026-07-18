using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EasyEDA_Loader.Floorplan;
using EasyEDA_Loader.Placement;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Rule-based board “needs” from MCU / RF / USB / power parts:
    /// stackup &amp; layers, impedance, via/fanout sizes, thermal hotspots.
    /// Pros use datasheets + fab calculators; this encodes the same decision tree.
    /// </summary>
    internal sealed class BoardNeedsReport
    {
        public bool HasRf { get; set; }
        public bool HasWifiBle { get; set; }
        public bool HasUsb { get; set; }
        public bool HasMcu { get; set; }
        public bool HasSwitchingRegulator { get; set; }
        public bool HasLdo { get; set; }
        public bool HasBgaOrFinePitch { get; set; }
        public bool HasHighSpeed { get; set; }
        public int IcCount { get; set; }
        public int ConnectorCount { get; set; }
        public int PowerIcCount { get; set; }
        public int EstimatedNetCount { get; set; }
        public List<string> DetectedHighlights { get; set; } = new List<string>();
        public List<ThermalHotspot> ThermalHotspots { get; set; } = new List<ThermalHotspot>();
        public StackupRecommendation RecommendedStackup { get; set; }
        public string LayerPlanSummary { get; set; }
        public List<ImpedanceHint> ImpedanceHints { get; set; } = new List<ImpedanceHint>();
        public List<ViaFanoutHint> ViaHints { get; set; } = new List<ViaFanoutHint>();
        public List<string> ActionChecklist { get; set; } = new List<string>();
        public string SummaryLine { get; set; }

        public string FormatFullText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(SummaryLine ?? "Board needs analysis");
            sb.AppendLine();
            sb.AppendLine("=== Detected ===");
            foreach (var h in DetectedHighlights)
                sb.AppendLine("• " + h);

            sb.AppendLine();
            sb.AppendLine("=== Stackup / layers ===");
            if (RecommendedStackup != null)
            {
                sb.AppendLine(RecommendedStackup.Title);
                sb.AppendLine("Layers: " + RecommendedStackup.LayerCount);
                sb.AppendLine("Template: " + RecommendedStackup.Template);
                sb.AppendLine(RecommendedStackup.LayerPlan);
                sb.AppendLine(LayerPlanSummary);
            }

            sb.AppendLine();
            sb.AppendLine("=== Impedance (verify on JLCPCB calculator) ===");
            foreach (var z in ImpedanceHints)
                sb.AppendLine($"• {z.Name}: {z.TargetOhms:0}Ω → ~{z.SuggestedWidthMils:0.#} mil wide on Top ({z.Notes})");

            sb.AppendLine();
            sb.AppendLine("=== Fanout / via sizes ===");
            foreach (var v in ViaHints)
                sb.AppendLine($"• {v.NetClass}: via {v.DiameterMils:0}/{v.HoleMils:0} mil — {v.Why}");

            sb.AppendLine();
            sb.AppendLine("=== Heat (where it gets trapped + what to do) ===");
            if (ThermalHotspots.Count == 0)
                sb.AppendLine("• No strong heat sources tagged from BOM comments. Still pour GND under MCUs.");
            foreach (var t in ThermalHotspots)
            {
                sb.AppendLine($"• {t.Designator} ({t.Kind}) — {t.WhyHot}");
                sb.AppendLine($"    Do: {t.WhatToDo}");
            }

            sb.AppendLine();
            sb.AppendLine("=== Checklist ===");
            foreach (var a in ActionChecklist)
                sb.AppendLine("☐ " + a);

            sb.AppendLine();
            sb.AppendLine("Pros find heat from: datasheet Pd / θJA, EPAD size, IR camera after prototype,");
            sb.AppendLine("and layout review (copper under package + thermal vias to plane). No AI required.");
            return sb.ToString();
        }
    }

    internal sealed class ThermalHotspot
    {
        public string Designator { get; set; }
        public string Kind { get; set; }
        public string WhyHot { get; set; }
        public string WhatToDo { get; set; }
        public double Severity { get; set; } // 1–5
    }

    internal sealed class ImpedanceHint
    {
        public string Name { get; set; }
        public double TargetOhms { get; set; }
        public double SuggestedWidthMils { get; set; }
        public string Notes { get; set; }
    }

    internal sealed class ViaFanoutHint
    {
        public string NetClass { get; set; }
        public double DiameterMils { get; set; }
        public double HoleMils { get; set; }
        public string Why { get; set; }
    }

    internal static class BoardNeedsAdvisor
    {
        private static readonly Regex UsbPattern = new Regex(
            @"USB|DP\b|DM\b|D\+|D-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WifiBlePattern = new Regex(
            @"ESP32|ESP8266|WIFI|WI-FI|BLE|BLUETOOTH|NRF52|NRF91",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BuckBoostPattern = new Regex(
            @"BUCK|BOOST|DCDC|DC-DC|MP15|TPS5|TPS6|XL600|MT360|SY8|AOZ",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LdoPattern = new Regex(
            @"LDO|AMS1117|LM1117|AP2112|XC620|ME621|HT73|REGULATOR",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FinePitchPattern = new Regex(
            @"BGA|QFN|DFN|WLCSP|0\.4\s*MM|0\.5\s*MM",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HsPattern = new Regex(
            @"DDR|HDMI|PCIE|USB3|MIPI|ETHERNET|RMII|RGMII|SDIO|SPI.?FLASH",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static BoardNeedsReport Analyze(string connectivityPath = null)
        {
            connectivityPath ??= DesignExporter.DefaultExportPath;
            if (!File.Exists(connectivityPath))
            {
                try { DesignExporter.ExportForPlacementPlanning(connectivityPath); }
                catch { /* use empty if export fails */ }
            }

            List<FloorplanPart> parts;
            try
            {
                parts = File.Exists(connectivityPath)
                    ? FloorplanGenerator.LoadPartsFromConnectivity(connectivityPath)
                    : new List<FloorplanPart>();
            }
            catch
            {
                parts = new List<FloorplanPart>();
            }

            var report = new BoardNeedsReport();
            var root = File.Exists(connectivityPath)
                ? JObject.Parse(File.ReadAllText(connectivityPath))
                : new JObject();

            report.EstimatedNetCount =
                (root["projectNets"] as JArray)?.Count
                ?? (root["nets"] as JArray)?.Count
                ?? 0;

            foreach (var p in parts)
            {
                var comment = p.Comment ?? "";
                var des = p.Designator ?? "";
                var blob = des + " " + comment;

                if (p.Role == FloorplanRole.Rf ||
                    PlacementConstants.RfTransceiverCommentTokens.Any(t =>
                        comment.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    comment.IndexOf("LORA", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    report.HasRf = true;
                    report.DetectedHighlights.Add($"{des}: RF / LoRa transceiver");
                }

                if (WifiBlePattern.IsMatch(blob))
                {
                    report.HasWifiBle = true;
                    report.HasMcu = true;
                    report.DetectedHighlights.Add($"{des}: WiFi/BLE / ESP-class module (RF + digital heat)");
                }

                if (p.Role == FloorplanRole.Mcu)
                {
                    report.HasMcu = true;
                    report.DetectedHighlights.Add($"{des}: MCU");
                }

                if (UsbPattern.IsMatch(blob) || des.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                {
                    report.HasUsb = true;
                    report.HasHighSpeed = true;
                    report.DetectedHighlights.Add($"{des}: USB");
                }

                if (BuckBoostPattern.IsMatch(blob) || p.Role == FloorplanRole.Power && BuckBoostPattern.IsMatch(comment))
                {
                    report.HasSwitchingRegulator = true;
                    report.PowerIcCount++;
                    report.DetectedHighlights.Add($"{des}: switching regulator (heat + noise)");
                    report.ThermalHotspots.Add(new ThermalHotspot
                    {
                        Designator = des,
                        Kind = "Switching regulator",
                        Severity = 5,
                        WhyHot = "Inductor switching losses + FET Rdson heat concentrate under the IC / inductor.",
                        WhatToDo =
                            "Place near power connector; large copper pours on VIN/VOUT/GND; thermal vias under EPAD to Mid GND; " +
                            "keep switching loop tiny; keep RF/analog away from the inductor.",
                    });
                }
                else if (LdoPattern.IsMatch(blob) || p.Role == FloorplanRole.Power)
                {
                    report.HasLdo = true;
                    report.PowerIcCount++;
                    report.DetectedHighlights.Add($"{des}: LDO / power");
                    report.ThermalHotspots.Add(new ThermalHotspot
                    {
                        Designator = des,
                        Kind = "LDO / power",
                        Severity = 4,
                        WhyHot = "Dropout × current = heat trapped in the package if copper is thin.",
                        WhatToDo =
                            "Copper pour on VIN/VOUT/GND; thermal vias (array) under EPAD to plane; " +
                            "if Pd is high, move to board edge or add heatsink / thicker copper.",
                    });
                }

                if (FinePitchPattern.IsMatch(blob) || FinePitchPattern.IsMatch(p.Comment ?? ""))
                {
                    report.HasBgaOrFinePitch = true;
                    report.DetectedHighlights.Add($"{des}: fine-pitch / BGA-like");
                }

                if (HsPattern.IsMatch(blob))
                {
                    report.HasHighSpeed = true;
                    report.DetectedHighlights.Add($"{des}: high-speed interface");
                }

                if (p.Role == FloorplanRole.Mcu || p.Role == FloorplanRole.OtherIc || p.Role == FloorplanRole.Rf)
                    report.IcCount++;
                if (p.Role == FloorplanRole.Connector)
                    report.ConnectorCount++;

                // ESP modules always get a thermal note
                if (WifiBlePattern.IsMatch(blob) &&
                    !report.ThermalHotspots.Any(t => t.Designator == des))
                {
                    report.ThermalHotspots.Add(new ThermalHotspot
                    {
                        Designator = des,
                        Kind = "WiFi/MCU module",
                        Severity = 3,
                        WhyHot = "Radio TX bursts + CPU heat; modules trap heat if GND pad is isolated.",
                        WhatToDo =
                            "Stitch module GND/EPAD with many vias to solid Mid GND; avoid cutting GND under antenna keepout; " +
                            "leave copper under the module body (not antenna).",
                    });
                }

                if (report.HasRf && p.Role == FloorplanRole.Rf &&
                    !report.ThermalHotspots.Any(t => t.Designator == des))
                {
                    report.ThermalHotspots.Add(new ThermalHotspot
                    {
                        Designator = des,
                        Kind = "RF transceiver",
                        Severity = 3,
                        WhyHot = "PA efficiency losses heat the die; bad thermal path also detunes RF.",
                        WhatToDo =
                            "EPAD full of thermal vias to GND plane; continuous GND under RF section; " +
                            "do not star-cut GND under the matching network.",
                    });
                }
            }

            // Deduplicate highlights
            report.DetectedHighlights = report.DetectedHighlights
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();

            report.RecommendedStackup = PickStackup(report);
            report.LayerPlanSummary = BuildLayerAdvice(report);
            report.ImpedanceHints = BuildImpedanceHints(report);
            report.ViaHints = BuildViaHints(report);
            report.ActionChecklist = BuildChecklist(report);
            report.SummaryLine = BuildSummaryLine(report);

            // Persist for other tools
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "AltiumEE");
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "board_needs_report.txt"),
                    report.FormatFullText());
            }
            catch { }

            return report;
        }

        public static void ApplyRecommendedViaSizesToProfile(BoardNeedsReport report)
        {
            if (report?.ViaHints == null || report.ViaHints.Count == 0)
                return;

            var profile = PcbRulesProfile.LoadOrCreateDefault();
            profile.ViaStyleRules ??= new List<ViaStyleRuleDefinition>();
            foreach (var hint in report.ViaHints)
            {
                var existing = profile.ViaStyleRules.FirstOrDefault(v =>
                    string.Equals(v.NetClass, hint.NetClass, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.PreferDiameterMils = hint.DiameterMils;
                    existing.PreferHoleMils = hint.HoleMils;
                    existing.MinDiameterMils = Math.Min(
                        existing.MinDiameterMils > 0 ? existing.MinDiameterMils : hint.DiameterMils,
                        hint.DiameterMils);
                    existing.MaxDiameterMils = Math.Max(existing.MaxDiameterMils, hint.DiameterMils + 6);
                    existing.MinHoleMils = Math.Min(
                        existing.MinHoleMils > 0 ? existing.MinHoleMils : hint.HoleMils,
                        hint.HoleMils);
                    existing.MaxHoleMils = Math.Max(existing.MaxHoleMils, hint.HoleMils + 4);
                }
            }

            File.WriteAllText(
                PcbRulesProfile.ProfilePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(profile, Newtonsoft.Json.Formatting.Indented));
        }

        private static StackupRecommendation PickStackup(BoardNeedsReport r)
        {
            // Dense / many rails / BGA → 6L
            if (r.HasBgaOrFinePitch && (r.IcCount >= 4 || r.PowerIcCount >= 2 || r.EstimatedNetCount > 120))
                return PcbStackupAdvisor.Catalog.FirstOrDefault(c => c.Id == "6l-jlc7628")
                       ?? PcbStackupAdvisor.GetDefaultRecommendation();

            if (r.IcCount >= 8 || r.PowerIcCount >= 3 || r.EstimatedNetCount > 200)
                return PcbStackupAdvisor.Catalog.FirstOrDefault(c => c.Id == "6l-jlc7628")
                       ?? PcbStackupAdvisor.GetDefaultRecommendation();

            // RF / WiFi / USB → 4L impedance template
            if (r.HasRf || r.HasWifiBle || r.HasUsb || r.HasHighSpeed || r.HasMcu)
                return PcbStackupAdvisor.Catalog.FirstOrDefault(c => c.Id == "4l-jlc7628")
                       ?? PcbStackupAdvisor.GetDefaultRecommendation();

            // Simple digital only
            if (!r.HasRf && !r.HasWifiBle && !r.HasUsb && r.IcCount <= 2)
                return PcbStackupAdvisor.Catalog.FirstOrDefault(c => c.Id == "2l-simple")
                       ?? PcbStackupAdvisor.GetDefaultRecommendation();

            return PcbStackupAdvisor.GetDefaultRecommendation();
        }

        private static string BuildLayerAdvice(BoardNeedsReport r)
        {
            var layers = r.RecommendedStackup?.LayerCount ?? 4;
            if (layers <= 2)
                return "2L: Top signals+parts, Bottom solid GND pour. Keep returns short. No controlled RF.";
            if (layers == 4)
                return "4L plan: L1 signals/RF · L2 solid GND · L3 power (3v3) · L4 GND/signals. " +
                       "RF references L2. Fanout power vias to L3, GND vias to L2.";
            return "6L plan: keep a solid GND adjacent to every fast/RF signal layer; " +
                   "dedicate ≥1 GND + ≥1 power plane; use inner layers for dense routes.";
        }

        private static List<ImpedanceHint> BuildImpedanceHints(BoardNeedsReport r)
        {
            var list = new List<ImpedanceHint>();
            var layers = r.RecommendedStackup?.LayerCount ?? 4;

            // Ballpark for JLC 4L 7628 / similar 1oz outer — MUST verify on fab calculator.
            if (r.HasRf || r.HasWifiBle)
            {
                list.Add(new ImpedanceHint
                {
                    Name = "RF antenna / matching (microstrip)",
                    TargetOhms = 50,
                    SuggestedWidthMils = layers >= 4 ? 12.0 : 30.0,
                    Notes = layers >= 4
                        ? "JLC04161H-7628 ballpark; confirm on jlcpcb.com/impedance"
                        : "2L RF is poor — prefer 4L",
                });
            }

            if (r.HasUsb)
            {
                list.Add(new ImpedanceHint
                {
                    Name = "USB 2.0 DP/DM differential",
                    TargetOhms = 90,
                    SuggestedWidthMils = layers >= 4 ? 7.5 : 10.0,
                    Notes = "Diff pair ~7–8 mil / ~8 mil gap typical on 4L — verify; length-match DP/DM",
                });
            }

            if (r.HasHighSpeed && !r.HasUsb)
            {
                list.Add(new ImpedanceHint
                {
                    Name = "High-speed single-ended",
                    TargetOhms = 50,
                    SuggestedWidthMils = layers >= 4 ? 12.0 : 20.0,
                    Notes = "Reference continuous GND; avoid plane splits under the route",
                });
            }

            if (list.Count == 0)
            {
                list.Add(new ImpedanceHint
                {
                    Name = "Logic (no controlled Z required)",
                    TargetOhms = 0,
                    SuggestedWidthMils = 6,
                    Notes = "Use default width rules; still keep solid GND under MCU",
                });
            }

            return list;
        }

        private static List<ViaFanoutHint> BuildViaHints(BoardNeedsReport r)
        {
            var layers = r.RecommendedStackup?.LayerCount ?? 4;
            // JLCPCB common cheap via: 0.3mm hole / 0.5–0.6mm pad ≈ 12/20 mil; we use profile-friendly sizes.
            var list = new List<ViaFanoutHint>
            {
                new ViaFanoutHint
                {
                    NetClass = "Logic",
                    DiameterMils = 18,
                    HoleMils = 8,
                    Why = "Standard signal fanout / stitch (cheap JLC via)",
                },
                new ViaFanoutHint
                {
                    NetClass = "RF",
                    DiameterMils = 18,
                    HoleMils = 8,
                    Why = "RF GND stitch — small, dense around matching & EPAD (not huge vias)",
                },
                new ViaFanoutHint
                {
                    NetClass = "HighSpeed",
                    DiameterMils = 18,
                    HoleMils = 8,
                    Why = "Escape vias; keep stub short on " + layers + "L",
                },
                new ViaFanoutHint
                {
                    NetClass = "PWR",
                    DiameterMils = r.HasSwitchingRegulator ? 24 : 20,
                    HoleMils = r.HasSwitchingRegulator ? 12 : 10,
                    Why = r.HasSwitchingRegulator
                        ? "Power + thermal: larger vias under EPAD / high-current paths"
                        : "Decap dogbone into power/GND planes",
                },
            };
            return list;
        }

        private static List<string> BuildChecklist(BoardNeedsReport r)
        {
            var list = new List<string>
            {
                $"Load stackup: {r.RecommendedStackup?.Template ?? "4L"} ({r.RecommendedStackup?.LayerCount ?? 4} layers)",
                "Setup Net Classes & Rules (RF / PWR / HighSpeed / Logic)",
                "Floorplan Preview → pick layout → Apply",
                "Auto-Place passives → Fanout Decap Vias → Create Rooms",
            };

            if (r.HasRf || r.HasWifiBle)
            {
                list.Add("Pour solid GND under RF before routing RF");
                list.Add("Set RF width for ~50Ω; keep matching network short & coplanar GND");
                list.Add("Antenna keepout: no copper / no GND under antenna element");
            }

            if (r.HasUsb)
                list.Add("Route USB DP/DM as 90Ω diff pair; ESD at connector");

            if (r.ThermalHotspots.Count > 0)
            {
                list.Add("Thermal vias under every power EPAD → Mid GND");
                list.Add("Copper pours on regulator VIN/VOUT/GND (not skinny traces)");
            }

            list.Add("Via stitch RF/clocks after routing");
            list.Add("Full DRC before fab; confirm stackup on JLCPCB order form");
            return list;
        }

        private static string BuildSummaryLine(BoardNeedsReport r)
        {
            var tags = new List<string>();
            if (r.HasMcu) tags.Add("MCU");
            if (r.HasRf) tags.Add("RF/LoRa");
            if (r.HasWifiBle) tags.Add("WiFi/BLE");
            if (r.HasUsb) tags.Add("USB");
            if (r.HasSwitchingRegulator) tags.Add("Buck");
            if (r.HasLdo) tags.Add("LDO");
            if (r.HasBgaOrFinePitch) tags.Add("fine-pitch");
            if (tags.Count == 0) tags.Add("general");

            return string.Format(
                CultureInfo.InvariantCulture,
                "Needs: {0} → recommend {1} ({2}L). Heat sources: {3}.",
                string.Join(" + ", tags),
                r.RecommendedStackup?.Template ?? "?",
                r.RecommendedStackup?.LayerCount ?? 0,
                r.ThermalHotspots.Count);
        }
    }
}
