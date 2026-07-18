using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    internal static class PlacementChains
    {
        internal static List<Tuple<string, string>> PassiveInternalPinPairs(JObject component)
        {
            var pinNumbers = PlacementConstants.Pins(component)
                .Select(p => PlacementConstants.JsonStr(p["number"]).Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            if (pinNumbers.Count < 2 || pinNumbers.Count > 3)
                return new List<Tuple<string, string>>();

            var pairs = new List<Tuple<string, string>>();
            foreach (var left in pinNumbers)
            {
                foreach (var right in pinNumbers)
                {
                    if (left != right)
                        pairs.Add(Tuple.Create(left, right));
                }
            }

            return pairs;
        }

        internal static Tuple<Dictionary<Tuple<string, string>, List<Tuple<string, string>>>, List<Tuple<string, string>>> BuildSupportConnectivityGraph(
            JObject data,
            string anchor,
            HashSet<string> allowedPassives)
        {
            anchor = anchor.Trim().ToUpperInvariant();
            var allowed = new HashSet<string>(allowedPassives, StringComparer.OrdinalIgnoreCase);
            var adjacency = new Dictionary<Tuple<string, string>, List<Tuple<string, string>>>();
            var componentIndex = PlacementConstants.ComponentIndex(data);

            void LinkNodes(Tuple<string, string> left, Tuple<string, string> right)
            {
                if (left.Equals(right))
                    return;
                if (!adjacency.TryGetValue(left, out var leftList))
                {
                    leftList = new List<Tuple<string, string>>();
                    adjacency[left] = leftList;
                }

                if (!leftList.Contains(right))
                    leftList.Add(right);

                if (!adjacency.TryGetValue(right, out var rightList))
                {
                    rightList = new List<Tuple<string, string>>();
                    adjacency[right] = rightList;
                }

                if (!rightList.Contains(left))
                    rightList.Add(left);
            }

            var seenNetLinks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var net in PlacementConstants.ProjectNets(data))
            {
                var pins = new List<Tuple<string, string>>();
                foreach (var connection in PlacementConstants.NetMembers(net))
                {
                    var designator = PlacementConstants.JsonStr(connection["designator"]).Trim().ToUpperInvariant();
                    var pinNumber = PlacementConstants.JsonStr(connection["pin"]).Trim();
                    if (string.IsNullOrEmpty(designator) || string.IsNullOrEmpty(pinNumber))
                        continue;
                    if (designator != anchor && !allowed.Contains(designator))
                        continue;
                    pins.Add(Tuple.Create(designator, pinNumber));
                }

                for (var i = 0; i < pins.Count; i++)
                {
                    for (var j = 0; j < pins.Count; j++)
                    {
                        if (i == j)
                            continue;
                        var left = pins[i];
                        var right = pins[j];
                        var key = string.Compare(left.Item1 + ":" + left.Item2, right.Item1 + ":" + right.Item2, StringComparison.Ordinal) <= 0
                            ? left.Item1 + ":" + left.Item2 + "|" + right.Item1 + ":" + right.Item2
                            : right.Item1 + ":" + right.Item2 + "|" + left.Item1 + ":" + left.Item2;
                        if (seenNetLinks.Contains(key))
                            continue;
                        seenNetLinks.Add(key);
                        LinkNodes(left, right);
                    }
                }
            }

            foreach (var designator in allowed)
            {
                if (!componentIndex.TryGetValue(designator, out var component))
                    continue;
                foreach (var pair in PassiveInternalPinPairs(component))
                    LinkNodes(Tuple.Create(designator, pair.Item1), Tuple.Create(designator, pair.Item2));
            }

            var icStarts = new List<Tuple<string, string>>();
            if (componentIndex.TryGetValue(anchor, out var anchorComponent))
            {
                foreach (var pinInfo in PlacementConstants.Pins(anchorComponent))
                {
                    var pinNumber = PlacementConstants.JsonStr(pinInfo["number"]).Trim();
                    if (!string.IsNullOrEmpty(pinNumber))
                        icStarts.Add(Tuple.Create(anchor, pinNumber));
                }
            }

            return Tuple.Create(adjacency, icStarts);
        }

        public static JArray DetectSupportChains(JObject data, string anchor, JArray supportComponents)
        {
            var allowed = new HashSet<string>(
                supportComponents
                    .OfType<JObject>()
                    .Select(item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant())
                    .Where(d => !string.IsNullOrEmpty(d)),
                StringComparer.OrdinalIgnoreCase);
            if (allowed.Count < 2)
                return new JArray();

            var supportByDes = supportComponents
                .OfType<JObject>()
                .ToDictionary(
                    item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant(),
                    item => item,
                    StringComparer.OrdinalIgnoreCase);

            var graph = BuildSupportConnectivityGraph(data, anchor, allowed);
            var adjacency = graph.Item1;
            var icStarts = graph.Item2;
            var maxChainPassives = Math.Max(8, Math.Min(12, allowed.Count));
            var activeIcStarts = icStarts.Where(start => adjacency.ContainsKey(start) && adjacency[start].Count > 0).ToList();
            var pathCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            List<string> PassivePathTo(string targetDes)
            {
                targetDes = targetDes.Trim().ToUpperInvariant();
                if (pathCache.TryGetValue(targetDes, out var cached))
                    return cached;

                var best = new List<string>();
                foreach (var start in activeIcStarts)
                {
                    var queue = new Queue<Tuple<Tuple<string, string>, List<string>>>();
                    queue.Enqueue(Tuple.Create(start, new List<string>()));
                    var visited = new HashSet<Tuple<string, string>> { start };

                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        var node = current.Item1;
                        var passives = current.Item2;
                        var designator = node.Item1;
                        if (allowed.Contains(designator))
                        {
                            if (passives.Count == 0 || passives[passives.Count - 1] != designator)
                                passives = new List<string>(passives) { designator };
                        }

                        if (designator == targetDes)
                        {
                            if (passives.Count > best.Count)
                                best = new List<string>(passives);
                            continue;
                        }

                        if (passives.Count >= maxChainPassives)
                            continue;

                        if (!adjacency.TryGetValue(node, out var neighbors))
                            continue;

                        foreach (var neighbor in neighbors)
                        {
                            if (visited.Contains(neighbor))
                                continue;
                            var neighborDes = neighbor.Item1;
                            if (neighborDes != anchor && !allowed.Contains(neighborDes))
                                continue;
                            visited.Add(neighbor);
                            queue.Enqueue(Tuple.Create(neighbor, new List<string>(passives)));
                        }
                    }
                }

                pathCache[targetDes] = best;
                return best;
            }

            var remaining = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            var chains = new JArray();
            var chainNumber = 0;
            while (remaining.Count > 0)
            {
                var bestPath = new List<string>();
                foreach (var designator in remaining.ToList())
                {
                    var path = PassivePathTo(designator);
                    if (path.Count > bestPath.Count)
                        bestPath = path;
                }

                if (bestPath.Count < 2)
                    break;

                chainNumber++;
                supportByDes.TryGetValue(bestPath[0], out var firstItem);
                chains.Add(new JObject
                {
                    ["chain_id"] = $"chain_{chainNumber}",
                    ["members"] = new JArray(bestPath),
                    ["length"] = bestPath.Count,
                    ["primary_ic_pin"] = firstItem?["primary_ic_pin"]?.DeepClone() ?? JValue.CreateNull(),
                    ["primary_net"] = firstItem?["primary_net"]?.DeepClone() ?? JValue.CreateNull(),
                });

                foreach (var member in bestPath)
                    remaining.Remove(member);
            }

            return chains;
        }

        public static JArray ExpandSeriesChainSupport(
            JObject data,
            string anchor,
            JArray supportComponents,
            double maxSchematicDistanceMils = 2500.0)
        {
            anchor = anchor.Trim().ToUpperInvariant();
            if (supportComponents == null || supportComponents.Count == 0)
                return supportComponents ?? new JArray();

            var componentIndex = PlacementConstants.ComponentIndex(data);
            componentIndex.TryGetValue(anchor, out var anchorComponent);
            var anchorSheet = PlacementConstants.SheetName(PlacementConstants.JsonStr(anchorComponent?["sheet"]));
            var anchorXy = PlacementConstants.PlacementXy(anchorComponent?["placement"]);

            var supportByDes = supportComponents
                .OfType<JObject>()
                .ToDictionary(
                    item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant(),
                    item => (JObject)item.DeepClone(),
                    StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(supportByDes.Keys, StringComparer.OrdinalIgnoreCase);

            bool PassiveNearEnough(JObject component)
            {
                if (anchorXy == null)
                    return true;
                var passiveXy = PlacementConstants.PlacementXy(component?["placement"]);
                if (passiveXy == null)
                    return false;
                return Math.Sqrt(
                    Math.Pow(passiveXy.Item1 - anchorXy.Item1, 2) +
                    Math.Pow(passiveXy.Item2 - anchorXy.Item2, 2)) <= maxSchematicDistanceMils;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var net in PlacementConstants.ProjectNets(data))
                {
                    var netName = PlacementConstants.JsonStr(net["name"]).Trim();
                    if (string.IsNullOrEmpty(netName) || PlacementConstants.IsPlaneNet(netName))
                        continue;

                    var members = new List<string>();
                    var touchesAllowed = false;
                    foreach (var connection in PlacementConstants.NetMembers(net))
                    {
                        var designator = PlacementConstants.JsonStr(connection["designator"]).Trim().ToUpperInvariant();
                        if (string.IsNullOrEmpty(designator))
                            continue;
                        if (designator == anchor || allowed.Contains(designator))
                            touchesAllowed = true;
                        if (PlacementConstants.IsPassiveDesignator(designator))
                            members.Add(designator);
                    }

                    if (!touchesAllowed)
                        continue;

                    foreach (var designator in members)
                    {
                        if (allowed.Contains(designator))
                            continue;
                        if (!componentIndex.TryGetValue(designator, out var component))
                            continue;
                        if (!string.IsNullOrEmpty(anchorSheet) &&
                            PlacementConstants.SheetName(PlacementConstants.JsonStr(component["sheet"])) != anchorSheet)
                        {
                            continue;
                        }

                        if (!PassiveNearEnough(component))
                            continue;

                        allowed.Add(designator);
                        changed = true;
                        supportByDes[designator] = new JObject
                        {
                            ["designator"] = designator,
                            ["comment"] = component["comment"],
                            ["roles"] = new JArray("signal"),
                            ["nets"] = new JArray(PlacementConstants.PassivePinNets(component).OrderBy(n => n, StringComparer.Ordinal)),
                            ["schematic"] = new JObject(),
                        };
                    }
                }
            }

            var originalDesignators = new HashSet<string>(
                supportComponents.OfType<JObject>().Select(item => PlacementConstants.JsonStr(item["designator"])),
                StringComparer.OrdinalIgnoreCase);
            var addedDesignators = allowed.Where(des => !originalDesignators.Contains(des)).ToList();
            if (addedDesignators.Count > 0)
            {
                var groupingSeed = PlacementSupport.GetIcSupportComponents(data, anchor);
                var enrichedByDes = (groupingSeed["support_components"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .ToDictionary(
                        item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant(),
                        item => item,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var designator in addedDesignators)
                {
                    if (enrichedByDes.TryGetValue(designator, out var enriched))
                        supportByDes[designator] = (JObject)enriched.DeepClone();
                }
            }

            var ordered = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in supportComponents.OfType<JObject>())
            {
                var des = PlacementConstants.JsonStr(item["designator"]);
                if (seen.Contains(des))
                    continue;
                ordered.Add(supportByDes.TryGetValue(des, out var existing) ? existing : item);
                seen.Add(des);
            }

            foreach (var kvp in supportByDes)
            {
                if (seen.Contains(kvp.Key))
                    continue;
                ordered.Add(kvp.Value);
                seen.Add(kvp.Key);
            }

            return ordered;
        }
    }
}
