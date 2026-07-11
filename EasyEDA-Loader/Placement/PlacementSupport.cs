using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    internal static class PlacementSupport
    {
        internal static JObject ResolvePrimaryIcLink(
            JObject component,
            JObject icPinIndex,
            JObject pinLayout,
            Tuple<double, double> anchorXy)
        {
            var passiveNets = PlacementConstants.PassivePinNets(component);
            var compXy = PlacementConstants.PlacementXy(component?["placement"]);

            var directLinks = new List<JObject>();
            foreach (var prop in icPinIndex.Properties())
            {
                var pinNumber = prop.Name;
                var pinInfo = prop.Value as JObject;
                var net = PlacementConstants.JsonStr(pinInfo?["net"]).Trim();
                if (string.IsNullOrEmpty(net) || !passiveNets.Contains(net))
                    continue;
                if (PlacementConstants.IsGlobalRail(net) && net.ToLowerInvariant().Contains("gnd") && passiveNets.Count > 1)
                    continue;

                var layout = pinLayout[pinNumber] as JObject ?? new JObject();
                directLinks.Add(new JObject
                {
                    ["pin"] = pinNumber,
                    ["pin_name"] = pinInfo?["pin_name"],
                    ["net"] = net,
                    ["link_type"] = "direct_net",
                    ["pin_angle_deg"] = PlacementConstants.PinAngleFromAnchor(anchorXy, layout) is double angle
                        ? (JToken)angle
                        : JValue.CreateNull(),
                    ["net_specificity"] = PlacementConstants.NetSpecificity(net),
                });
            }

            if (directLinks.Count > 0)
            {
                directLinks.Sort((a, b) =>
                {
                    var cmp = -a["net_specificity"].Value<int>().CompareTo(b["net_specificity"].Value<int>());
                    if (cmp != 0) return cmp;
                    var aNull = a["pin_angle_deg"].Type == JTokenType.Null;
                    var bNull = b["pin_angle_deg"].Type == JTokenType.Null;
                    cmp = aNull.CompareTo(bNull);
                    if (cmp != 0) return cmp;
                    return string.Compare(PlacementConstants.JsonStr(a["pin"]), PlacementConstants.JsonStr(b["pin"]), StringComparison.Ordinal);
                });

                var primary = directLinks[0];
                return new JObject
                {
                    ["primary_net"] = primary["net"],
                    ["primary_ic_pin"] = primary["pin"],
                    ["primary_ic_pin_name"] = primary["pin_name"],
                    ["pin_angle_deg"] = primary["pin_angle_deg"],
                    ["linked_ic_pins"] = new JArray(directLinks),
                };
            }

            var passiveLower = new HashSet<string>(passiveNets.Select(n => n.ToLowerInvariant()));
            if (PlacementConstants.JsonStr(component?["designator"]).ToUpperInvariant().StartsWith("C", StringComparison.Ordinal) &&
                passiveLower.Contains("3v3"))
            {
                var powerCandidates = new List<JObject>();
                foreach (var prop in icPinIndex.Properties())
                {
                    var pinNumber = prop.Name;
                    var pinInfo = prop.Value as JObject;
                    var net = PlacementConstants.JsonStr(pinInfo?["net"]).Trim();
                    var pinName = PlacementConstants.JsonStr(pinInfo?["pin_name"]).Trim();
                    var netLower = net.ToLowerInvariant();
                    if (!new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3v3", "3.3v", "vcc", "vcc3v3" }.Contains(netLower) &&
                        !PlacementConstants.PowerPinNamePattern.IsMatch(pinName))
                    {
                        continue;
                    }

                    var layout = pinLayout[pinNumber] as JObject ?? new JObject();
                    double? distance = null;
                    if (compXy != null &&
                        layout["xMils"] != null && layout["yMils"] != null &&
                        layout["xMils"].Type != JTokenType.Null && layout["yMils"].Type != JTokenType.Null)
                    {
                        distance = Math.Sqrt(
                            Math.Pow(layout["xMils"].Value<double>() - compXy.Item1, 2) +
                            Math.Pow(layout["yMils"].Value<double>() - compXy.Item2, 2));
                    }

                    powerCandidates.Add(new JObject
                    {
                        ["pin"] = pinNumber,
                        ["pin_name"] = pinName,
                        ["net"] = string.IsNullOrEmpty(net) ? "3v3" : net,
                        ["link_type"] = "power_decoupling",
                        ["pin_angle_deg"] = PlacementConstants.PinAngleFromAnchor(anchorXy, layout) is double angle
                            ? (JToken)angle
                            : JValue.CreateNull(),
                        ["distance_mils"] = distance.HasValue ? (JToken)distance.Value : JValue.CreateNull(),
                    });
                }

                if (powerCandidates.Count > 0)
                {
                    powerCandidates.Sort((a, b) =>
                    {
                        var aNull = a["distance_mils"].Type == JTokenType.Null;
                        var bNull = b["distance_mils"].Type == JTokenType.Null;
                        var cmp = aNull.CompareTo(bNull);
                        if (cmp != 0) return cmp;
                        cmp = (a["distance_mils"].Type == JTokenType.Null ? 0.0 : a["distance_mils"].Value<double>())
                            .CompareTo(b["distance_mils"].Type == JTokenType.Null ? 0.0 : b["distance_mils"].Value<double>());
                        if (cmp != 0) return cmp;
                        return string.Compare(PlacementConstants.JsonStr(a["pin"]), PlacementConstants.JsonStr(b["pin"]), StringComparison.Ordinal);
                    });

                    var primary = powerCandidates[0];
                    return new JObject
                    {
                        ["primary_net"] = "3v3",
                        ["primary_ic_pin"] = primary["pin"],
                        ["primary_ic_pin_name"] = primary["pin_name"],
                        ["pin_angle_deg"] = primary["pin_angle_deg"],
                        ["linked_ic_pins"] = new JArray(powerCandidates.Take(4)),
                    };
                }
            }

            string primaryNet = null;
            if (passiveNets.Count > 0)
            {
                primaryNet = passiveNets
                    .OrderByDescending(n => PlacementConstants.NetSpecificity(n))
                    .ThenBy(n => n, StringComparer.Ordinal)
                    .First();
            }

            return new JObject
            {
                ["primary_net"] = primaryNet != null ? (JToken)primaryNet : JValue.CreateNull(),
                ["primary_ic_pin"] = JValue.CreateNull(),
                ["primary_ic_pin_name"] = JValue.CreateNull(),
                ["pin_angle_deg"] = JValue.CreateNull(),
                ["linked_ic_pins"] = new JArray(),
            };
        }

        internal static Tuple<bool, string> ShouldIncludePassive(
            string netName,
            JObject net,
            JObject component,
            string anchor,
            string anchorSheet,
            Tuple<double, double> anchorXy,
            bool sameSheetOnly,
            double maxSchematicDistanceMils,
            bool excludeGlobalNets)
        {
            var designator = PlacementConstants.JsonStr(component["designator"]).Trim().ToUpperInvariant();
            var compXy = PlacementConstants.PlacementXy(component["placement"]);
            double? schDistance = null;
            if (anchorXy != null && compXy != null)
                schDistance = Math.Sqrt(Math.Pow(compXy.Item1 - anchorXy.Item1, 2) + Math.Pow(compXy.Item2 - anchorXy.Item2, 2));

            if (sameSheetOnly &&
                PlacementConstants.SheetName(PlacementConstants.JsonStr(component["sheet"])) !=
                PlacementConstants.SheetName(anchorSheet))
            {
                return Tuple.Create(false, "other_sheet");
            }

            if (PlacementConstants.ExclusiveToAnchor(net, anchor))
                return Tuple.Create(true, "exclusive_net");

            if (PlacementConstants.IsLocalNetName(netName))
                return Tuple.Create(true, "local_net");

            if (excludeGlobalNets && PlacementConstants.IsGlobalRail(netName))
            {
                if (!designator.StartsWith("C", StringComparison.Ordinal))
                    return Tuple.Create(false, "global_rail_non_cap");
                if (!schDistance.HasValue || schDistance.Value > maxSchematicDistanceMils)
                    return Tuple.Create(false, "global_rail_far");
                return Tuple.Create(true, "nearby_decoupling");
            }

            if (schDistance.HasValue && schDistance.Value <= maxSchematicDistanceMils)
            {
                if (PlacementConstants.NetIcCount(net, anchor) == 0)
                    return Tuple.Create(true, "nearby_signal");
                return Tuple.Create(false, "nearby_but_shared_ic");
            }

            return Tuple.Create(false, "too_far_or_shared");
        }

        public static JObject GetIcSupportComponents(
            JObject data,
            string icDesignator,
            bool sameSheetOnly = true,
            double maxSchematicDistanceMils = 2500.0,
            bool excludeGlobalNets = true)
        {
            var target = icDesignator.Trim().ToUpperInvariant();
            JObject anchor = null;
            foreach (var component in PlacementConstants.Components(data))
            {
                if (PlacementConstants.JsonStr(component["designator"]).Trim().ToUpperInvariant() == target)
                {
                    anchor = component;
                    break;
                }
            }

            if (anchor == null)
            {
                return new JObject
                {
                    ["found"] = false,
                    ["anchor"] = target,
                    ["error"] = $"Component '{icDesignator}' not found in export.",
                };
            }

            var icNets = PlacementConstants.CollectIcNets(data, target);
            var projectNets = PlacementConstants.ProjectNetIndex(data);
            var componentIndex = PlacementConstants.ComponentIndex(data);
            var anchorXy = PlacementConstants.PlacementXy(anchor["placement"]);
            var anchorSheet = PlacementConstants.JsonStr(anchor["sheet"]);
            var icPinIndex = PlacementConstants.BuildIcPinIndex(anchor);
            var pinLayout = PlacementConstants.LoadIcPinLayout(anchor);

            var grouped = new Dictionary<string, GroupEntry>(StringComparer.OrdinalIgnoreCase);
            var rejectedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var netName in icNets.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!projectNets.TryGetValue(netName, out var net))
                    continue;

                foreach (var connection in PlacementConstants.NetMembers(net))
                {
                    var designator = PlacementConstants.JsonStr(connection["designator"]).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(designator) || designator == target ||
                        !PlacementConstants.IsPassiveDesignator(designator))
                    {
                        continue;
                    }

                    if (!componentIndex.TryGetValue(designator, out var component))
                        continue;

                    var includeResult = ShouldIncludePassive(
                        netName,
                        net,
                        component,
                        target,
                        anchorSheet,
                        anchorXy,
                        sameSheetOnly,
                        maxSchematicDistanceMils,
                        excludeGlobalNets);
                    if (!includeResult.Item1)
                    {
                        rejectedCounts[includeResult.Item2] = rejectedCounts.TryGetValue(includeResult.Item2, out var count)
                            ? count + 1
                            : 1;
                        continue;
                    }

                    if (!grouped.TryGetValue(designator, out var entry))
                    {
                        entry = new GroupEntry
                        {
                            Designator = designator,
                            Comment = component["comment"],
                            Jlcpcb = component["jlcpcb"],
                            Sheet = component["sheet"],
                        };
                        grouped[designator] = entry;
                    }

                    if (!entry.Nets.Contains(netName))
                        entry.Nets.Add(netName);
                    entry.IncludeReasons.Add(includeResult.Item2);
                    if (PlacementConstants.IsDecouplingCap(component, netName))
                        entry.Roles.Add("decoupling");
                    else if (PlacementConstants.LooksLikePowerNet(netName) || PlacementConstants.IsGlobalRail(netName))
                        entry.Roles.Add("power");
                    else
                        entry.Roles.Add("signal");
                }
            }

            var supportList = new List<JObject>();
            var roleRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["decoupling"] = 0,
                ["power"] = 1,
                ["signal"] = 2,
                ["support"] = 3,
            };

            foreach (var entry in grouped.Values)
            {
                var component = componentIndex[entry.Designator];
                var compXy = PlacementConstants.PlacementXy(component["placement"]);
                double? offsetX = null;
                double? offsetY = null;
                double? distance = null;
                double? angleDeg = null;
                if (anchorXy != null && compXy != null)
                {
                    offsetX = Math.Round(compXy.Item1 - anchorXy.Item1, 3);
                    offsetY = Math.Round(compXy.Item2 - anchorXy.Item2, 3);
                    distance = Math.Round(Math.Sqrt(offsetX.Value * offsetX.Value + offsetY.Value * offsetY.Value), 3);
                    angleDeg = Math.Round(Math.Atan2(offsetY.Value, offsetX.Value) * 180.0 / Math.PI, 2);
                }

                var roles = entry.Roles.OrderBy(r => r, StringComparer.Ordinal).ToList();
                if (roles.Count == 0)
                    roles.Add("support");
                var primaryRole = roles.Contains("decoupling") ? "decoupling" : roles[0];
                var netLink = ResolvePrimaryIcLink(component, icPinIndex, pinLayout, anchorXy);
                var pinAngle = netLink["pin_angle_deg"];
                if (pinAngle == null || pinAngle.Type == JTokenType.Null)
                    pinAngle = angleDeg.HasValue ? (JToken)angleDeg.Value : JValue.CreateNull();

                supportList.Add(new JObject
                {
                    ["designator"] = entry.Designator,
                    ["comment"] = entry.Comment,
                    ["jlcpcb"] = entry.Jlcpcb,
                    ["sheet"] = entry.Sheet,
                    ["nets"] = new JArray(entry.Nets),
                    ["roles"] = new JArray(roles),
                    ["primary_role"] = primaryRole,
                    ["primary_net"] = netLink["primary_net"],
                    ["primary_ic_pin"] = netLink["primary_ic_pin"],
                    ["primary_ic_pin_name"] = netLink["primary_ic_pin_name"],
                    ["linked_ic_pins"] = netLink["linked_ic_pins"] ?? new JArray(),
                    ["include_reasons"] = new JArray(entry.IncludeReasons.OrderBy(r => r, StringComparer.Ordinal)),
                    ["schematic"] = new JObject
                    {
                        ["offsetXMils"] = offsetX.HasValue ? (JToken)offsetX.Value : JValue.CreateNull(),
                        ["offsetYMils"] = offsetY.HasValue ? (JToken)offsetY.Value : JValue.CreateNull(),
                        ["distanceMils"] = distance.HasValue ? (JToken)distance.Value : JValue.CreateNull(),
                        ["angleDeg"] = angleDeg.HasValue ? (JToken)angleDeg.Value : JValue.CreateNull(),
                        ["pinAngleDeg"] = pinAngle,
                        ["placement"] = component["placement"]?.DeepClone(),
                    },
                });
            }

            supportList.Sort((a, b) =>
            {
                var aRole = PlacementConstants.JsonStr(a["primary_role"]);
                var bRole = PlacementConstants.JsonStr(b["primary_role"]);
                var cmp = (roleRank.TryGetValue(aRole, out var ar) ? ar : 9).CompareTo(roleRank.TryGetValue(bRole, out var br) ? br : 9);
                if (cmp != 0) return cmp;
                cmp = string.Compare(PlacementConstants.JsonStr(a["primary_net"]), PlacementConstants.JsonStr(b["primary_net"]), StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                cmp = PlacementLayout.CapProximityRank(a).CompareTo(PlacementLayout.CapProximityRank(b));
                if (cmp != 0) return cmp;
                var aPin = a["schematic"]?["pinAngleDeg"];
                var bPin = b["schematic"]?["pinAngleDeg"];
                cmp = (aPin == null || aPin.Type == JTokenType.Null).CompareTo(bPin == null || bPin.Type == JTokenType.Null);
                if (cmp != 0) return cmp;
                var aAngle = aPin != null && aPin.Type != JTokenType.Null ? aPin.Value<double>() : 0.0;
                var bAngle = bPin != null && bPin.Type != JTokenType.Null ? bPin.Value<double>() : 0.0;
                cmp = aAngle.CompareTo(bAngle);
                if (cmp != 0) return cmp;
                return string.Compare(PlacementConstants.JsonStr(a["designator"]), PlacementConstants.JsonStr(b["designator"]), StringComparison.Ordinal);
            });

            return new JObject
            {
                ["found"] = true,
                ["anchor"] = target,
                ["anchor_comment"] = anchor["comment"],
                ["anchor_sheet"] = anchor["sheet"],
                ["ic_net_count"] = icNets.Count,
                ["support_count"] = supportList.Count,
                ["support_components"] = new JArray(supportList),
                ["has_schematic_coords"] = anchorXy != null,
                ["has_pin_layout"] = pinLayout.Properties().Any(),
                ["anchor_placement"] = anchor["placement"]?.DeepClone(),
                ["pin_layout"] = pinLayout,
                ["filters"] = new JObject
                {
                    ["same_sheet_only"] = sameSheetOnly,
                    ["max_schematic_distance_mils"] = maxSchematicDistanceMils,
                    ["exclude_global_nets"] = excludeGlobalNets,
                },
                ["rejected_counts"] = JObject.FromObject(rejectedCounts),
            };
        }

        private sealed class GroupEntry
        {
            public string Designator { get; set; }
            public JToken Comment { get; set; }
            public JToken Jlcpcb { get; set; }
            public JToken Sheet { get; set; }
            public List<string> Nets { get; } = new List<string>();
            public HashSet<string> Roles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> IncludeReasons { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static JObject VerifyClusterPlacement(
            string anchor,
            Tuple<double, double> anchorXy,
            JArray supportComponents,
            JArray moves,
            double spacingMils,
            double maxRadiusMils)
        {
            var supportIndex = supportComponents
                .OfType<JObject>()
                .ToDictionary(
                    item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant(),
                    item => item,
                    StringComparer.OrdinalIgnoreCase);

            var minSep = Math.Max(spacingMils * 0.75, 60.0);
            var minStandoff = Math.Max(spacingMils * 0.8, 70.0);
            var items = new JArray();
            var okCount = 0;
            var moveList = moves.OfType<JObject>().ToList();

            foreach (var move in moveList)
            {
                var designator = PlacementConstants.JsonStr(move["designator"]).Trim().ToUpperInvariant();
                supportIndex.TryGetValue(designator, out var support);
                var issues = new List<string>();
                var x = move.Value<double>("xMils");
                var y = move.Value<double>("yMils");
                var standoff = Math.Sqrt(Math.Pow(x - anchorXy.Item1, 2) + Math.Pow(y - anchorXy.Item2, 2));
                var placementAngle = Math.Atan2(y - anchorXy.Item2, x - anchorXy.Item1) * 180.0 / Math.PI;

                var primaryPin = PlacementConstants.JsonStr(move["primary_ic_pin"]);
                var primaryNet = PlacementConstants.JsonStr(move["primary_net"]);
                if (string.IsNullOrEmpty(primaryPin))
                    issues.Add("missing_primary_ic_pin");
                if (string.IsNullOrEmpty(primaryNet))
                    issues.Add("missing_primary_net");

                var moveNets = new HashSet<string>(
                    (move["nets"] as JArray ?? new JArray())
                        .Select(n => PlacementConstants.JsonStr(n).Trim())
                        .Where(n => !string.IsNullOrEmpty(n)),
                    StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(primaryNet) && moveNets.Count > 0 && !moveNets.Contains(primaryNet))
                {
                    if ((move["linked_ic_pins"] as JArray ?? new JArray()).Count > 0)
                        issues.Add("primary_net_not_on_part");
                }

                var linkedPins = move["linked_ic_pins"] as JArray ?? new JArray();
                if (!string.IsNullOrEmpty(primaryPin) && linkedPins.Count > 0)
                {
                    var linkedNumbers = new HashSet<string>(
                        linkedPins.OfType<JObject>().Select(entry => PlacementConstants.JsonStr(entry["pin"]).Trim()),
                        StringComparer.OrdinalIgnoreCase);
                    if (!linkedNumbers.Contains(primaryPin.Trim()))
                        issues.Add("primary_pin_not_in_linked_ic_pins");
                }

                if (standoff > maxRadiusMils + 1.0)
                    issues.Add("outside_max_radius");
                if (standoff < minStandoff)
                    issues.Add("too_close_to_anchor");

                var targetPinAngleToken = move["targetPinAngleDeg"];
                if (targetPinAngleToken != null && targetPinAngleToken.Type != JTokenType.Null)
                {
                    var targetPinAngle = targetPinAngleToken.Value<double>();
                    var angleDelta = PlacementConstants.AngleDeltaDeg(placementAngle, targetPinAngle);
                    var pinSlot = move.Value<int?>("pinSlot") ?? 0;
                    var angleOffset = move.Value<double?>("angleOffsetDeg") ?? pinSlot * 14.0;
                    var allowedDelta = Math.Max(28.0, angleOffset + 18.0);
                    if (angleDelta > allowedDelta)
                        issues.Add("not_aligned_to_pin_ray");
                }

                foreach (var other in moveList)
                {
                    var otherDes = PlacementConstants.JsonStr(other["designator"]).Trim().ToUpperInvariant();
                    if (otherDes == designator)
                        continue;
                    var otherDist = Math.Sqrt(
                        Math.Pow(x - other.Value<double>("xMils"), 2) +
                        Math.Pow(y - other.Value<double>("yMils"), 2));
                    if (otherDist < minSep)
                    {
                        issues.Add("too_close_to_other_part");
                        break;
                    }
                }

                var status = issues.Count == 0 ? "ok" : "warn";
                if (status == "ok")
                    okCount++;

                items.Add(new JObject
                {
                    ["designator"] = designator,
                    ["status"] = status,
                    ["issues"] = new JArray(issues),
                    ["anchor"] = anchor,
                    ["primary_ic_pin"] = string.IsNullOrEmpty(primaryPin) ? JValue.CreateNull() : primaryPin,
                    ["primary_ic_pin_name"] = move["primary_ic_pin_name"]?.DeepClone() ?? JValue.CreateNull(),
                    ["primary_net"] = string.IsNullOrEmpty(primaryNet) ? JValue.CreateNull() : primaryNet,
                    ["method"] = move["method"],
                    ["standoffMils"] = Math.Round(standoff, 3),
                    ["placementAngleDeg"] = Math.Round(placementAngle, 2),
                    ["targetPinAngleDeg"] = move["targetPinAngleDeg"]?.DeepClone() ?? JValue.CreateNull(),
                    ["roles"] = move["roles"] ?? support?["roles"] ?? new JArray(),
                });
            }

            return new JObject
            {
                ["anchor"] = anchor,
                ["verified_count"] = items.Count,
                ["ok_count"] = okCount,
                ["warn_count"] = items.Count - okCount,
                ["all_ok"] = okCount == items.Count && items.Count > 0,
                ["items"] = items,
            };
        }

        internal static Dictionary<string, string> ComputePassiveAnchorOwnership(
            JObject data,
            IList<string> anchorDesignators,
            Dictionary<string, JObject> pcbComponents,
            bool sameSheetOnly,
            double maxSchematicDistanceMils,
            bool excludeGlobalNets,
            double schematicScale)
        {
            var scores = new Dictionary<string, List<Tuple<double, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var anchor in anchorDesignators)
            {
                var grouping = GetIcSupportComponents(
                    data,
                    anchor,
                    sameSheetOnly,
                    maxSchematicDistanceMils,
                    excludeGlobalNets);
                if (grouping.Value<bool?>("found") != true)
                    continue;

                if (!pcbComponents.TryGetValue(anchor, out var anchorPcb))
                    continue;
                var anchorXy = PlacementConstants.PlacementXy(anchorPcb["placement"]);
                if (anchorXy == null)
                    continue;

                var pinLayout = grouping["pin_layout"] as JObject ?? new JObject();
                var pinIndex = PlacementLayout.BuildPcbPinIndex(anchorPcb);
                var anchorSch = PlacementConstants.PlacementXy(grouping["anchor_placement"]);

                foreach (var item in (grouping["support_components"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var designator = PlacementConstants.JsonStr(item["designator"]);
                    var pinNum = PlacementConstants.JsonStr(item["primary_ic_pin"]);
                    var pinXy = PlacementLayout.ResolveIcPinPcbXy(
                        pinNum,
                        pinIndex,
                        pinLayout,
                        anchorSch,
                        anchorXy,
                        schematicScale);

                    pcbComponents.TryGetValue(designator, out var passivePcb);
                    var passiveXy = PlacementConstants.PlacementXy(passivePcb?["placement"]);
                    double score;
                    if (pinXy != null && passiveXy != null)
                        score = Math.Sqrt(Math.Pow(passiveXy.Item1 - pinXy.Item1, 2) + Math.Pow(passiveXy.Item2 - pinXy.Item2, 2));
                    else if (pinXy != null)
                        score = 120.0;
                    else
                        score = 5000.0;

                    if (PlacementConstants.JsonStr(item["primary_role"]) == "decoupling")
                        score *= 0.82;
                    score += PlacementLayout.CapProximityRank(item) * 8.0;

                    if (!scores.TryGetValue(designator, out var options))
                    {
                        options = new List<Tuple<double, string>>();
                        scores[designator] = options;
                    }

                    options.Add(Tuple.Create(score, anchor));
                }
            }

            var ownership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in scores)
            {
                var best = kvp.Value
                    .OrderBy(entry => entry.Item1)
                    .ThenBy(entry => PlacementConstants.AnchorSortKey(entry.Item2).Item1)
                    .ThenBy(entry => PlacementConstants.AnchorSortKey(entry.Item2).Item2)
                    .ThenBy(entry => PlacementConstants.AnchorSortKey(entry.Item2).Item3, StringComparer.Ordinal)
                    .First();
                ownership[kvp.Key] = best.Item2;
            }

            return ownership;
        }

        public static List<string> ListClusterAnchorDesignators(
            JObject data,
            bool sameSheetOnly = true,
            double maxSchematicDistanceMils = 2500.0,
            bool excludeGlobalNets = true,
            int minSupportCount = 1)
        {
            var pcbComponents = PlacementConstants.PcbComponentIndex(data);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<Tuple<string, int>>();

            foreach (var component in PlacementConstants.Components(data))
            {
                var designator = PlacementConstants.JsonStr(component["designator"]).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(designator) ||
                    !PlacementConstants.IcDesignatorPattern.IsMatch(designator) ||
                    seen.Contains(designator))
                {
                    continue;
                }

                seen.Add(designator);
                if (!pcbComponents.ContainsKey(designator))
                    continue;

                var grouping = GetIcSupportComponents(
                    data,
                    designator,
                    sameSheetOnly,
                    maxSchematicDistanceMils,
                    excludeGlobalNets);
                var supportCount = grouping.Value<int?>("support_count") ?? 0;
                if (grouping.Value<bool?>("found") == true && supportCount >= minSupportCount)
                    candidates.Add(Tuple.Create(designator, supportCount));
            }

            candidates.Sort((a, b) =>
            {
                var keyA = PlacementConstants.AnchorSortKey(a.Item1);
                var keyB = PlacementConstants.AnchorSortKey(b.Item1);
                var cmp = keyA.Item1.CompareTo(keyB.Item1);
                if (cmp != 0) return cmp;
                cmp = keyA.Item2.CompareTo(keyB.Item2);
                if (cmp != 0) return cmp;
                cmp = string.Compare(keyA.Item3, keyB.Item3, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                return b.Item2.CompareTo(a.Item2);
            });

            return candidates.Select(c => c.Item1).ToList();
        }
    }
}
