using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    internal static class PlacementConstants
    {
        public const double PlanSchemaVersion = 5.1;

        public static readonly HashSet<string> PassivePrefixes = new HashSet<string>(
            new[] { "R", "C", "L", "FB", "CB", "RN", "RV", "D" },
            StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> GlobalRailNames = new HashSet<string>(
            new[]
            {
                "3v3", "3.3v", "3v3_a", "vcc", "vcc3v3", "5v", "gnd", "agnd", "dgnd", "pgnd", "vss",
            },
            StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<double> SupportedConnectivitySchemaVersions = new HashSet<double>
        {
            4.0, 5.0, 5.1,
        };

        public static readonly Regex IcDesignatorPattern = new Regex(
            @"^(IC|U)\d+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex PowerPinNamePattern = new Regex(
            @"(^VDD|^VCC|^VDDA|^VDDD|^VDD3|^VBAT|^VIN|^3V3|3\.3|SUPPLY|POWER)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex PassivePrefixPattern = new Regex(
            @"^([A-Z]+)",
            RegexOptions.Compiled);

        public static readonly Regex CapValuePattern = new Regex(
            @"([\d.]+)\s*(UF|NF|PF|U|N|P)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex AnchorSortPattern = new Regex(
            @"^(IC|U)(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly string[] RfTransceiverCommentTokens =
        {
            "SX126", "SX127", "SX128", "LLCC68", "RF4463", "SI4463", "NRF24", "NRF91",
            "AT86RF", "CC1101", "CC1125", "SUB-GHZ", "SUBGHZ", "LORA TRANSCEIVER",
        };

        public static bool IsRfMatchingAnchor(JObject grouping)
        {
            if (grouping == null)
                return false;

            var comment = JsonStr(grouping["anchor_comment"]).ToUpperInvariant();
            foreach (var token in RfTransceiverCommentTokens)
            {
                if (comment.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public static string DefaultPlanPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "placement_plan.json");

        public static bool IsPassiveDesignator(string designator)
        {
            var text = (designator ?? string.Empty).Trim().ToUpperInvariant();
            var match = PassivePrefixPattern.Match(text);
            return match.Success && PassivePrefixes.Contains(match.Groups[1].Value);
        }

        public static Tuple<double, double> PlacementXy(JToken placement)
        {
            if (placement == null || placement.Type == JTokenType.Null)
                return null;

            var obj = placement as JObject;
            if (obj == null)
                return null;

            foreach (var pair in new[] { new[] { "xMils", "yMils" }, new[] { "xMm", "yMm" } })
            {
                var xToken = obj[pair[0]];
                var yToken = obj[pair[1]];
                if (xToken != null && yToken != null && xToken.Type != JTokenType.Null && yToken.Type != JTokenType.Null)
                    return Tuple.Create(xToken.Value<double>(), yToken.Value<double>());
            }

            return null;
        }

        public static string SheetName(string sheet)
        {
            if (string.IsNullOrWhiteSpace(sheet))
                return string.Empty;
            return Path.GetFileName(sheet).ToLowerInvariant();
        }

        public static bool IsGlobalRail(string netName)
        {
            var text = (netName ?? string.Empty).Trim().ToLowerInvariant();
            if (GlobalRailNames.Contains(text))
                return true;
            return text.Contains("gnd") && text.Length <= 6;
        }

        /// <summary>GND / return plane nets — place a via nearby; do not use as a placement magnet.</summary>
        public static bool IsGndNet(string netName)
        {
            var text = (netName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text))
                return false;
            if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "gnd", "agnd", "dgnd", "pgnd", "vss" }.Contains(text))
            {
                return true;
            }

            return IsGlobalRail(text) && text.Contains("gnd");
        }

        /// <summary>Power rails that typically live on an internal plane (via to mid-layer).</summary>
        public static bool IsPowerPlaneNet(string netName)
        {
            var text = (netName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text) || IsGndNet(text))
                return false;
            if (GlobalRailNames.Contains(text))
                return true;
            return LooksLikePowerNet(text);
        }

        /// <summary>GND or power plane — discard as a placement pull; via to mid-layer instead.</summary>
        public static bool IsPlaneNet(string netName) =>
            IsGndNet(netName) || IsPowerPlaneNet(netName);

        public static bool IsLocalNetName(string netName)
        {
            var text = (netName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text) || IsGlobalRail(text))
                return false;
            var upper = text.ToUpperInvariant();
            return upper.StartsWith("NETIC", StringComparison.Ordinal) ||
                   upper.StartsWith("NETC", StringComparison.Ordinal) ||
                   upper.StartsWith("NETR", StringComparison.Ordinal) ||
                   upper.StartsWith("NETU", StringComparison.Ordinal);
        }

        /// <summary>
        /// Decoupling = capacitor between a power rail and GND (not AC-coupling / filter caps
        /// between two signal nets). Target net must be one of the two sides.
        /// </summary>
        public static bool IsDecouplingCap(JObject component, string netName)
        {
            var designator = JsonStr(component?["designator"]).Trim().ToUpperInvariant();
            if (!designator.StartsWith("C", StringComparison.Ordinal))
                return false;

            var pins = component?["pins"] as JArray;
            if (pins == null || pins.Count == 0)
                return false;

            var pinNets = pins
                .Select(p => JsonStr(p?["net"]).Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            if (pinNets.Count == 0)
                return false;

            var target = (netName ?? string.Empty).Trim();
            var hasTarget = pinNets.Any(net =>
                string.Equals(net, target, StringComparison.OrdinalIgnoreCase));
            var hasGnd = pinNets.Any(IsGndNet);
            var hasPower = pinNets.Any(IsPowerPlaneNet);
            return hasTarget && hasGnd && hasPower;
        }

        public static bool LooksLikePowerNet(string netName)
        {
            var text = (netName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text) || IsGndNet(text))
                return false;
            if (GlobalRailNames.Contains(text))
                return true;
            return new[] { "vcc", "vdd", "vbat", "vin", "vout", "vbus", "3v3", "1v8", "2v5", "5v", "12v" }
                .Any(text.Contains);
        }

        public static IEnumerable<JObject> NetMembers(JObject net)
        {
            if (net?["connections"] is JArray connections)
            {
                foreach (var conn in connections.OfType<JObject>())
                    yield return conn;
            }
        }

        public static int NetIcCount(JObject net, string anchor)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var conn in NetMembers(net))
            {
                var designator = JsonStr(conn["designator"]).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(designator) || designator == anchor)
                    continue;
                if (IcDesignatorPattern.IsMatch(designator))
                    seen.Add(designator);
            }

            return seen.Count;
        }

        public static bool ExclusiveToAnchor(JObject net, string anchor)
        {
            var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var conn in NetMembers(net))
            {
                var designator = JsonStr(conn["designator"]).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(designator) || designator == anchor)
                    continue;
                if (!IsPassiveDesignator(designator))
                    others.Add(designator);
            }

            return others.Count == 0;
        }

        public static HashSet<string> CollectIcNets(JObject data, string icDesignator)
        {
            var target = icDesignator.Trim().ToUpperInvariant();
            var nets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "no net", "nonet", "unconnected" };

            foreach (var component in Components(data))
            {
                if (JsonStr(component["designator"]).Trim().ToUpperInvariant() != target)
                    continue;

                foreach (var pin in Pins(component))
                {
                    var net = JsonStr(pin["net"]).Trim();
                    if (!string.IsNullOrEmpty(net) && !invalid.Contains(net.ToLowerInvariant()))
                        nets.Add(net);
                }
            }

            return nets;
        }

        public static int NetSpecificity(string netName)
        {
            var text = (netName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
                return 0;
            // Plane nets are never placement magnets — signal / local nets win.
            if (IsGndNet(text))
                return 0;
            if (IsPowerPlaneNet(text))
                return 15;
            if (IsLocalNetName(text))
                return 100;
            return 80;
        }

        public static HashSet<string> PassivePinNets(JObject component)
        {
            var nets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "no net", "nonet", "unconnected" };
            foreach (var pin in Pins(component))
            {
                var net = JsonStr(pin["net"]).Trim();
                if (!string.IsNullOrEmpty(net) && !invalid.Contains(net.ToLowerInvariant()))
                    nets.Add(net);
            }

            return nets;
        }

        public static JObject BuildIcPinIndex(JObject anchor)
        {
            var index = new JObject();
            foreach (var pin in Pins(anchor))
            {
                var pinNumber = JsonStr(pin["number"]).Trim();
                if (string.IsNullOrEmpty(pinNumber))
                    continue;
                index[pinNumber] = new JObject
                {
                    ["pin"] = pinNumber,
                    ["pin_name"] = JsonStr(pin["name"]).Trim(),
                    ["net"] = JsonStr(pin["net"]).Trim(),
                };
            }

            return index;
        }

        /// <summary>
        /// Build pin layout from export JSON only (no SchDoc resolver).
        /// Uses pin coordinates from export when present; otherwise xMils/yMils are null.
        /// </summary>
        public static JObject LoadIcPinLayout(JObject anchor)
        {
            var pinIndex = BuildIcPinIndex(anchor);
            var layout = new JObject();

            if (anchor?["pinLayout"] is JObject exportedLayout)
            {
                foreach (var prop in exportedLayout.Properties())
                {
                    if (prop.Value is JObject entry)
                        layout[prop.Name] = (JObject)entry.DeepClone();
                }
            }

            foreach (var prop in pinIndex.Properties())
            {
                var pinNumber = prop.Name;
                var exportPin = prop.Value as JObject;
                if (layout[pinNumber] is JObject existing)
                {
                    if (string.IsNullOrEmpty(JsonStr(existing["pin_name"])))
                        existing["pin_name"] = exportPin?["pin_name"];
                    if (string.IsNullOrEmpty(JsonStr(existing["net"])))
                        existing["net"] = exportPin?["net"];
                    continue;
                }

                JToken xMils = null;
                JToken yMils = null;
                foreach (var pin in Pins(anchor))
                {
                    if (JsonStr(pin["number"]).Trim() != pinNumber)
                        continue;
                    if (pin["xMils"] != null && pin["yMils"] != null)
                    {
                        xMils = pin["xMils"];
                        yMils = pin["yMils"];
                    }

                    break;
                }

                layout[pinNumber] = new JObject
                {
                    ["pin"] = pinNumber,
                    ["pin_name"] = exportPin?["pin_name"],
                    ["net"] = exportPin?["net"],
                    ["xMils"] = xMils ?? JValue.CreateNull(),
                    ["yMils"] = yMils ?? JValue.CreateNull(),
                };
            }

            return layout;
        }

        public static double? PinAngleFromAnchor(Tuple<double, double> anchorXy, JObject pinLayout)
        {
            if (anchorXy == null || pinLayout == null)
                return null;
            var px = pinLayout["xMils"];
            var py = pinLayout["yMils"];
            if (px == null || py == null || px.Type == JTokenType.Null || py.Type == JTokenType.Null)
                return null;
            return Math.Round(
                Math.Atan2(py.Value<double>() - anchorXy.Item2, px.Value<double>() - anchorXy.Item1) * 180.0 / Math.PI,
                2);
        }

        public static Tuple<int, int, string> AnchorSortKey(string designator)
        {
            var match = AnchorSortPattern.Match((designator ?? string.Empty).Trim().ToUpperInvariant());
            if (!match.Success)
                return Tuple.Create(9, 9999, designator ?? string.Empty);
            var prefixRank = match.Groups[1].Value == "IC" ? 0 : 1;
            return Tuple.Create(prefixRank, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), designator.ToUpperInvariant());
        }

        public static double NormalizeAngleDeg(double angle) => angle % 360.0;

        public static double AngleDeltaDeg(double a, double b)
        {
            var delta = Math.Abs(NormalizeAngleDeg(a) - NormalizeAngleDeg(b));
            return Math.Min(delta, 360.0 - delta);
        }

        public static double? NormalizeSchemaVersion(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
                return null;
            if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                return value.Value<double>();
            if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        public static string JsonStr(JToken token) =>
            token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString();

        public static IEnumerable<JObject> Components(JObject data)
        {
            if (data?["components"] is JArray components)
            {
                foreach (var item in components.OfType<JObject>())
                    yield return item;
            }
        }

        public static IEnumerable<JObject> ProjectNets(JObject data)
        {
            JArray nets = data?["projectNets"] as JArray ?? data?["nets"] as JArray;
            if (nets == null)
                yield break;
            foreach (var item in nets.OfType<JObject>())
                yield return item;
        }

        public static Dictionary<string, JObject> ProjectNetIndex(JObject data)
        {
            var index = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var net in ProjectNets(data))
            {
                var name = JsonStr(net["name"]).Trim();
                if (!string.IsNullOrEmpty(name))
                    index[name] = net;
            }

            return index;
        }

        public static Dictionary<string, JObject> ComponentIndex(JObject data)
        {
            var index = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in Components(data))
            {
                var designator = JsonStr(component["designator"]).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(designator))
                    index[designator] = component;
            }

            return index;
        }

        public static IEnumerable<JObject> Pins(JObject component)
        {
            if (component?["pins"] is JArray pins)
            {
                foreach (var pin in pins.OfType<JObject>())
                    yield return pin;
            }
        }

        public static Dictionary<string, JObject> PcbComponentIndex(JObject data)
        {
            var index = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var pcbComponents = data?["pcb"]?["components"] as JArray;
            if (pcbComponents == null)
                return index;

            foreach (var component in pcbComponents.OfType<JObject>())
            {
                var designator = JsonStr(component["designator"]).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(designator))
                    index[designator] = component;
            }

            return index;
        }
    }
}
