using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    internal sealed class PcbRulesProfile
    {
        public const string DefaultProfileFileName = "pcb-rules-profile.json";
        private const int CurrentProfileVersion = 6;

        public static string ProfilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                DefaultProfileFileName);

        public int ProfileVersion { get; set; } = CurrentProfileVersion;
        public List<NetClassDefinition> NetClasses { get; set; } = new List<NetClassDefinition>();
        public List<WidthRuleDefinition> WidthRules { get; set; } = new List<WidthRuleDefinition>();
        public List<ClearanceRuleDefinition> ClearanceRules { get; set; } = new List<ClearanceRuleDefinition>();
        public List<ViaStyleRuleDefinition> ViaStyleRules { get; set; } = new List<ViaStyleRuleDefinition>();
        public PlaneRoutingPolicy PlaneRouting { get; set; } = new PlaneRoutingPolicy();

        public static PcbRulesProfile LoadOrCreateDefault()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath) ?? string.Empty);
            if (File.Exists(ProfilePath))
            {
                try
                {
                    var json = File.ReadAllText(ProfilePath);
                    var profile = JsonConvert.DeserializeObject<PcbRulesProfile>(json);
                    if (profile != null && profile.NetClasses.Count > 0)
                    {
                        if (Migrate(profile))
                        {
                            try { File.WriteAllText(ProfilePath, JsonConvert.SerializeObject(profile, Formatting.Indented)); }
                            catch { /* non-fatal: keep in-memory migration */ }
                        }
                        return profile;
                    }
                }
                catch
                {
                    // fall through to rewrite defaults
                }
            }

            var defaults = CreateDefault();
            File.WriteAllText(ProfilePath, JsonConvert.SerializeObject(defaults, Formatting.Indented));
            return defaults;
        }

        /// <summary>
        /// Merges in classes/rules added in newer profile versions without dropping user edits.
        /// Returns true if the profile was modified and should be re-saved.
        /// </summary>
        private static bool Migrate(PcbRulesProfile profile)
        {
            var changed = false;
            var defaults = CreateDefault();

            foreach (var defClass in defaults.NetClasses)
            {
                var existing = profile.NetClasses.FirstOrDefault(c =>
                    string.Equals(c.Name, defClass.Name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    profile.NetClasses.Add(defClass);
                    changed = true;
                }
                else
                {
                    // Backfill pin-name tokens on classes that should have them.
                    if ((existing.PinNameTokens == null || existing.PinNameTokens.Count == 0) &&
                        defClass.PinNameTokens != null && defClass.PinNameTokens.Count > 0)
                    {
                        existing.PinNameTokens = new List<string>(defClass.PinNameTokens);
                        changed = true;
                    }
                    if ((existing.NetNameTokens == null || existing.NetNameTokens.Count == 0) &&
                        defClass.NetNameTokens != null && defClass.NetNameTokens.Count > 0)
                    {
                        existing.NetNameTokens = new List<string>(defClass.NetNameTokens);
                        changed = true;
                    }
                    else if (defClass.NetNameTokens != null && defClass.NetNameTokens.Count > 0)
                    {
                        // Always merge missing critical tokens (e.g. +5 on an already-v5 profile).
                        existing.NetNameTokens ??= new List<string>();
                        var have = new HashSet<string>(existing.NetNameTokens, StringComparer.OrdinalIgnoreCase);
                        foreach (var token in defClass.NetNameTokens)
                        {
                            if (!have.Contains(token))
                            {
                                existing.NetNameTokens.Add(token);
                                have.Add(token);
                                changed = true;
                            }
                        }
                    }
                    if ((existing.ComponentCommentTokens == null || existing.ComponentCommentTokens.Count == 0) &&
                        defClass.ComponentCommentTokens != null && defClass.ComponentCommentTokens.Count > 0)
                    {
                        existing.ComponentCommentTokens = new List<string>(defClass.ComponentCommentTokens);
                        changed = true;
                    }
                }
            }

            foreach (var defRule in defaults.WidthRules)
            {
                var exists = profile.WidthRules.Any(r =>
                    string.Equals(r.Name, defRule.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.NetClass, defRule.NetClass, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    profile.WidthRules.Add(defRule);
                    changed = true;
                }
            }

            foreach (var defRule in defaults.ClearanceRules)
            {
                var exists = profile.ClearanceRules.Any(r =>
                    string.Equals(r.Name, defRule.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Scope1NetClass, defRule.Scope1NetClass, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    profile.ClearanceRules.Add(defRule);
                    changed = true;
                }
            }

            profile.ViaStyleRules ??= new List<ViaStyleRuleDefinition>();
            foreach (var defRule in defaults.ViaStyleRules)
            {
                var exists = profile.ViaStyleRules.Any(r =>
                    string.Equals(r.Name, defRule.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.NetClass, defRule.NetClass, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    profile.ViaStyleRules.Add(defRule);
                    changed = true;
                }
            }

            if (profile.ProfileVersion < CurrentProfileVersion)
            {
                // v3 migration: remove ambiguous RF pin/net tokens (PA/TX/RX) that substring-matched
                // MCU GPIO (PA3) and UART (UART2_RX) pins on combo MCU+RF modules like the RAK family.
                if (profile.ProfileVersion < 3)
                {
                    var rf = profile.NetClasses.FirstOrDefault(c =>
                        string.Equals(c.Name, "RF", StringComparison.OrdinalIgnoreCase));
                    if (rf != null)
                    {
                        var dropPin = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PA", "TX", "RX" };
                        var dropNet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TX", "RX" };
                        if (rf.PinNameTokens != null)
                            rf.PinNameTokens = rf.PinNameTokens.Where(t => !dropPin.Contains(t)).ToList();
                        if (rf.NetNameTokens != null)
                            rf.NetNameTokens = rf.NetNameTokens.Where(t => !dropNet.Contains(t)).ToList();
                        changed = true;
                    }
                }
                // v4 migration: remove "ETH" from HighSpeed net-name tokens - it substring-matched
                // auto-named header nets like NetH1_1 (Net + H1) and falsely seeded them HighSpeed.
                // Ethernet is still detected via RGMII/RMII/MDC/MDIO/REFCLK tokens.
                if (profile.ProfileVersion < 4)
                {
                    var hs = profile.NetClasses.FirstOrDefault(c =>
                        string.Equals(c.Name, "HighSpeed", StringComparison.OrdinalIgnoreCase));
                    if (hs?.NetNameTokens != null)
                    {
                        hs.NetNameTokens = hs.NetNameTokens
                            .Where(t => !string.Equals(t, "ETH", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        changed = true;
                    }
                }
                // v5 migration: add bare-voltage PWR tokens (+5, +3, +12, 1V8, 2V5, B+, BAT+, ...)
                // so nets like "+5" and "B+" are classified as PWR instead of Logic.
                if (profile.ProfileVersion < 5)
                {
                    var pwr = profile.NetClasses.FirstOrDefault(c =>
                        string.Equals(c.Name, "PWR", StringComparison.OrdinalIgnoreCase));
                    if (pwr?.NetNameTokens != null)
                    {
                        var addTokens = new[] { "+5", "+3", "+12", "+1V8", "+2V5", "1V8", "2V5", "VBCKP", "V_BCKP", "B+", "BAT+", "BAT-", "BATT" };
                        var existing = new HashSet<string>(pwr.NetNameTokens, StringComparer.OrdinalIgnoreCase);
                        foreach (var t in addTokens)
                        {
                            if (!existing.Contains(t))
                            {
                                pwr.NetNameTokens.Add(t);
                                changed = true;
                            }
                        }
                    }
                }
                // v6: routing via styles + neckdown-friendly PWR width range (min << preferred).
                if (profile.ProfileVersion < 6)
                {
                    profile.ViaStyleRules ??= new List<ViaStyleRuleDefinition>();
                    changed = true;
                }
                profile.ProfileVersion = CurrentProfileVersion;
                changed = true;
            }

            return changed;
        }

        public static PcbRulesProfile CreateDefault()
        {
            return new PcbRulesProfile
            {
                NetClasses = new List<NetClassDefinition>
                {
                    new NetClassDefinition
                    {
                        Name = "RF",
                        Description = "RF / antenna / matching nets",
                        NetNameTokens = new List<string>
                        {
                            "RF", "ANT", "LORA", "SUBG", "SUB-G", "MATCH", "IFA", "BALUN",
                            "FEED", "WLAN", "WIFI", "BLE", "NFC", "GPS_RF", "RFIO", "RFI", "RFO",
                        },
                        ComponentCommentTokens = new List<string>
                        {
                            "SX126", "SX127", "SX128", "LLCC68", "NRF24", "CC1101", "CC1125",
                            "LORA", "TRANSCEIVER", "ANTENNA", "RAK",
                        },
                        // Unambiguous RF pin names only. PA/TX/RX are deliberately excluded:
                        // they substring-match MCU GPIO (PA3) and UART (UART2_RX) on combo MCU+RF modules.
                        PinNameTokens = new List<string>
                        {
                            "RF", "RFIO", "RFI", "RFO", "ANT", "LNA", "FEED", "BALUN", "MATCH",
                        },
                    },
                    new NetClassDefinition
                    {
                        Name = "PWR",
                        Description = "Power and ground distribution",
                        NetNameTokens = new List<string>
                        {
                            "GND", "AGND", "DGND", "PGND", "VSS", "VCC", "VDD", "3V3", "3.3V",
                            "5V", "12V", "VBAT", "VBUS", "VIN", "VOUT", "VDDA", "VDDD", "+3V3", "+5V",
                            "+5", "+3", "+12", "+1V8", "+2V5", "1V8", "2V5", "VREF", "VBCKP", "V_BCKP",
                            "B+", "BAT+", "BAT-", "BATT",
                        },
                    },
                    new NetClassDefinition
                    {
                        Name = "HighSpeed",
                        Description = "High-speed digital / analog nets needing length match or controlled routing",
                        NetNameTokens = new List<string>
                        {
                            "USB", "D+", "D-", "DP", "DM", "RGMII", "RMII", "MDC", "MDIO",
                            "SDIO", "SD_CLK", "SDCMD", "QSPI", "MIPI", "CSI", "DSI", "HDMI",
                            "DDR", "DQS", "REFCLK", "XTAL", "OSC", "MCLK", "BCLK", "LRCLK",
                            "CANH", "CANL", "CAN_P", "CAN_N", "RS485", "LVDS", "USB_P", "USB_N",
                        },
                        ComponentCommentTokens = new List<string>
                        {
                            "LAN872", "DP83848", "KSZ808", "KSZ903", "RTL8211",
                            "USB330", "USB332", "USB251", "CH340", "CP210", "FT232", "FT223",
                            "DDR3", "DDR4", "LPDDR", "MT48", "IS42", "AS4C",
                        },
                        PinNameTokens = new List<string>
                        {
                            "D+", "D-", "DM", "DP", "USB_DM", "USB_DP",
                            "MCLK", "BCLK", "LRCLK", "SDCLK", "DQS",
                            "RGMII", "RMII", "REFCLK", "XTAL", "OSC",
                        },
                    },
                    new NetClassDefinition
                    {
                        Name = "Logic",
                        Description = "Default digital / signal nets",
                        CatchAll = true,
                    },
                },
                WidthRules = new List<WidthRuleDefinition>
                {
                    new WidthRuleDefinition
                    {
                        Name = "MCP - RF Width 50 Ohm",
                        NetClass = "RF",
                        MinMils = 11.0,
                        PreferredMils = 11.0,
                        MaxMils = 11.0,
                        ImpedanceOhms = 50.0,
                    },
                    new WidthRuleDefinition
                    {
                        Name = "MCP - HighSpeed Width",
                        NetClass = "HighSpeed",
                        MinMils = 6.0,
                        PreferredMils = 8.0,
                        MaxMils = 12.0,
                    },
                    new WidthRuleDefinition
                    {
                        Name = "MCP - Logic Width",
                        NetClass = "Logic",
                        MinMils = 6.0,
                        PreferredMils = 8.0,
                        MaxMils = 12.0,
                    },
                    new WidthRuleDefinition
                    {
                        Name = "MCP - PWR Width",
                        NetClass = "PWR",
                        // Min allows neckdown into IC pads; preferred is plane/feeder width.
                        MinMils = 8.0,
                        PreferredMils = 20.0,
                        MaxMils = 60.0,
                    },
                },
                ClearanceRules = new List<ClearanceRuleDefinition>
                {
                    new ClearanceRuleDefinition
                    {
                        Name = "MCP - RF Clearance",
                        Scope1NetClass = "RF",
                        Scope2Expression = "All",
                        GapMils = 8.0,
                    },
                    new ClearanceRuleDefinition
                    {
                        Name = "MCP - HighSpeed Clearance",
                        Scope1NetClass = "HighSpeed",
                        Scope2Expression = "All",
                        GapMils = 7.0,
                    },
                    new ClearanceRuleDefinition
                    {
                        Name = "MCP - Logic Clearance",
                        Scope1NetClass = "Logic",
                        Scope2Expression = "All",
                        GapMils = 6.0,
                    },
                },
                ViaStyleRules = new List<ViaStyleRuleDefinition>
                {
                    // Through vias for multilayer plane stitching / fanout (sizes in mils).
                    new ViaStyleRuleDefinition
                    {
                        Name = "MCP - Logic Via",
                        NetClass = "Logic",
                        PreferHoleMils = 8.0,
                        MinHoleMils = 6.0,
                        MaxHoleMils = 12.0,
                        PreferDiameterMils = 18.0,
                        MinDiameterMils = 16.0,
                        MaxDiameterMils = 28.0,
                    },
                    new ViaStyleRuleDefinition
                    {
                        Name = "MCP - HighSpeed Via",
                        NetClass = "HighSpeed",
                        PreferHoleMils = 8.0,
                        MinHoleMils = 6.0,
                        MaxHoleMils = 10.0,
                        PreferDiameterMils = 18.0,
                        MinDiameterMils = 16.0,
                        MaxDiameterMils = 24.0,
                    },
                    new ViaStyleRuleDefinition
                    {
                        Name = "MCP - RF Via",
                        NetClass = "RF",
                        PreferHoleMils = 8.0,
                        MinHoleMils = 6.0,
                        MaxHoleMils = 10.0,
                        PreferDiameterMils = 18.0,
                        MinDiameterMils = 16.0,
                        MaxDiameterMils = 24.0,
                    },
                    new ViaStyleRuleDefinition
                    {
                        Name = "MCP - PWR Via",
                        NetClass = "PWR",
                        PreferHoleMils = 12.0,
                        MinHoleMils = 10.0,
                        MaxHoleMils = 20.0,
                        PreferDiameterMils = 24.0,
                        MinDiameterMils = 20.0,
                        MaxDiameterMils = 40.0,
                    },
                },
                PlaneRouting = new PlaneRoutingPolicy
                {
                    Enabled = true,
                    RuleName = "MCP - Signal Routing Layers Only",
                    ScopeExpression = "InNetClass('Logic') Or InNetClass('RF') Or InNetClass('HighSpeed')",
                    AllowedSignalLayerNameTokens = new List<string> { "Top", "Bottom" },
                    BlockInternalPlaneLayers = true,
                    PlaneClearanceRuleName = "MCP - Solid Plane Clearance",
                    PlaneClearanceMils = 12.0,
                },
            };
        }
    }

    internal sealed class NetClassDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> NetNameTokens { get; set; } = new List<string>();
        public List<string> ComponentCommentTokens { get; set; } = new List<string>();
        public List<string> PinNameTokens { get; set; } = new List<string>();
        public bool CatchAll { get; set; }
    }

    internal sealed class WidthRuleDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string NetClass { get; set; } = string.Empty;
        public double MinMils { get; set; }
        public double PreferredMils { get; set; }
        public double MaxMils { get; set; }
        public double? ImpedanceOhms { get; set; }
    }

    internal sealed class ClearanceRuleDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Scope1NetClass { get; set; } = string.Empty;
        public string Scope2Expression { get; set; } = "All";
        public double GapMils { get; set; }
    }

    internal sealed class ViaStyleRuleDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string NetClass { get; set; } = string.Empty;
        public double PreferHoleMils { get; set; }
        public double MinHoleMils { get; set; }
        public double MaxHoleMils { get; set; }
        public double PreferDiameterMils { get; set; }
        public double MinDiameterMils { get; set; }
        public double MaxDiameterMils { get; set; }
    }

    internal sealed class PlaneRoutingPolicy
    {
        public bool Enabled { get; set; } = true;
        public string RuleName { get; set; } = "MCP - Signal Routing Layers Only";
        public string ScopeExpression { get; set; } = "InNetClass('Logic') Or InNetClass('RF') Or InNetClass('HighSpeed')";
        public List<string> AllowedSignalLayerNameTokens { get; set; } = new List<string> { "Top", "Bottom" };
        public bool BlockInternalPlaneLayers { get; set; } = true;
        public string PlaneClearanceRuleName { get; set; } = "MCP - Solid Plane Clearance";
        public double PlaneClearanceMils { get; set; } = 12.0;
    }
}
