using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;

namespace EasyEDA_Loader
{
    internal sealed class PcbDesignRulesSetupResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; } = string.Empty;
        public Dictionary<string, List<string>> NetClassAssignments { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedRules { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal static class PcbDesignRulesSetup
    {
        private const string ManagedRulePrefix = "MCP - ";
        // Priority order matters: a net is checked against PWR first, then RF, then HighSpeed, then Logic (catch-all).
        public static readonly string[] ManagedNetClassOrder = { "PWR", "RF", "HighSpeed", "Logic" };
        private static readonly HashSet<string> ManagedNetClassNames = new HashSet<string>(
            ManagedNetClassOrder,
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Classifies nets from the current PCB board using the saved profile + connectivity hints.
        /// Does NOT mutate the board. Safe to call for a preview before Apply.
        /// </summary>
        public static PcbDesignRulesSetupResult Classify(bool useConnectivityHints = true)
        {
            var profile = PcbRulesProfile.LoadOrCreateDefault();
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            var connectivity = useConnectivityHints ? TryLoadConnectivity() : null;
            var netNames = EnumerateBoardNetNames(board);
            var assignments = ClassifyNets(netNames, profile, connectivity);

            return new PcbDesignRulesSetupResult
            {
                NetClassAssignments = assignments,
            };
        }

        /// <summary>
        /// Applies the given net-class assignments (and the saved profile's width/clearance/routing rules)
        /// to the current PCB board in a single undo batch.
        /// </summary>
        public static PcbDesignRulesSetupResult Apply(Dictionary<string, List<string>> assignments)
        {
            var profile = PcbRulesProfile.LoadOrCreateDefault();
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            var pcbServer = AltiumApi.GlobalVars.PCBServer;

            // Normalize: ensure every managed class key exists, drop unknown classes.
            var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ManagedNetClassOrder)
                normalized[name] = new List<string>();
            foreach (var pair in assignments)
            {
                if (ManagedNetClassNames.Contains(pair.Key))
                    normalized[pair.Key] = pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            var result = new PcbDesignRulesSetupResult
            {
                NetClassAssignments = normalized,
            };

            board.NewUndo();
            pcbServer.PreProcess();
            try
            {
                RemoveManagedRulesAndClasses(board);
                CreateNetClasses(board, normalized);
                CreateWidthRules(board, profile);
                CreateClearanceRules(board, profile);
                if (profile.PlaneRouting?.Enabled == true)
                {
                    CreateRoutingLayerRule(board, profile.PlaneRouting);
                    CreatePlaneClearanceRule(board, profile.PlaneRouting);
                }

                result.CreatedRules.AddRange(
                    profile.WidthRules.Select(r => r.Name)
                        .Concat(profile.ClearanceRules.Select(r => r.Name)));
                if (profile.PlaneRouting?.Enabled == true)
                {
                    result.CreatedRules.Add(profile.PlaneRouting.RuleName);
                    result.CreatedRules.Add(profile.PlaneRouting.PlaneClearanceRuleName);
                }
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }

            PcbDocumentHelper.RefreshBoardView(board);
            result.Success = true;
            result.Summary = BuildSummary(result, profile);
            return result;
        }

        /// <summary>Back-compat: classify then apply in one shot (no preview). Prefer Classify + Apply.</summary>
        public static PcbDesignRulesSetupResult ApplyFromProfile(bool useConnectivityHints = true)
        {
            var classified = Classify(useConnectivityHints);
            return Apply(classified.NetClassAssignments);
        }

        private static JObject TryLoadConnectivity()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "AltiumEE",
                    "connectivity.json");
                if (!File.Exists(path))
                    return null;
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static List<string> EnumerateBoardNetNames(IPCB_Board board)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IterateBoardObjects(board, PcbTObjectId.eNetObject, obj =>
            {
                if (obj is IPCB_Net net)
                {
                    var name = SafeText(net.GetState_Name());
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            });
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Deterministic classifier with series-chain propagation.
        /// Priority: PWR (by net name only) -> RF (seed + propagate through series passives)
        /// -> HighSpeed (seed + propagate) -> Logic (catch-all).
        /// PWR never propagates through passives (so a resistor divider midpoint is not PWR).
        /// RF wins over HighSpeed when a net is claimed by both.
        /// </summary>
        private static Dictionary<string, List<string>> ClassifyNets(
            IEnumerable<string> netNames,
            PcbRulesProfile profile,
            JObject connectivity)
        {
            var buckets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ManagedNetClassOrder)
                buckets[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var pwrDef = GetClass(profile, "PWR");
            var rfDef = GetClass(profile, "RF");
            var hsDef = GetClass(profile, "HighSpeed");

            // Build connectivity indices so we can walk the netlist.
            BuildConnectivityIndices(
                connectivity,
                out var netToPins,
                out var componentToPins,
                out var componentComment,
                out var componentPinCount);

            // Step 1 - seed classification by net name and pin name.
            // assigned tracks the class of each net so propagation can respect priority.
            var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var propagationQueue = new Queue<string>();

            foreach (var net in netNames)
            {
                if (string.IsNullOrWhiteSpace(net))
                    continue;

                // PWR first - power nets are never signal, and PWR does not propagate.
                if (MatchesTokens(net, pwrDef?.NetNameTokens))
                {
                    assigned[net] = "PWR";
                    continue;
                }

                string seed = null;
                if (MatchesTokens(net, rfDef?.NetNameTokens))
                    seed = "RF";
                else if (MatchesTokens(net, hsDef?.NetNameTokens))
                    seed = "HighSpeed";
                else if (netToPins.TryGetValue(net, out var pinsOnNet))
                {
                    // Pin-name seeding: any pin on this net with an RF/HS pin name.
                    foreach (var pin in pinsOnNet)
                    {
                        if (MatchesTokens(pin.PinName, rfDef?.PinNameTokens))
                        {
                            seed = "RF";
                            break;
                        }
                        if (MatchesTokens(pin.PinName, hsDef?.PinNameTokens))
                        {
                            seed = "HighSpeed";
                            break;
                        }
                    }
                }

                if (seed != null)
                {
                    assigned[net] = seed;
                    propagationQueue.Enqueue(net);
                }
            }

            // Step 2 - propagate RF and HighSpeed through 2-pin series passives (BFS).
            // A series passive is a 2-pin C/R/L/D/FB/BEAD (not an IC, connector, or transistor).
            // We never propagate into a PWR-named net or a net already classified PWR.
            // RF wins over HighSpeed when both reach the same net.
            while (propagationQueue.Count > 0)
            {
                var currentNet = propagationQueue.Dequeue();
                if (!assigned.TryGetValue(currentNet, out var currentClass))
                    continue;
                if (currentClass != "RF" && currentClass != "HighSpeed")
                    continue;

                if (!netToPins.TryGetValue(currentNet, out var pinsOnCurrent))
                    continue;

                foreach (var pin in pinsOnCurrent)
                {
                    if (!IsSeriesPassive(pin.Designator, componentPinCount))
                        continue;

                    var otherPins = componentToPins.TryGetValue(pin.Designator, out var cp)
                        ? cp : null;
                    if (otherPins == null)
                        continue;

                    foreach (var other in otherPins)
                    {
                        if (string.Equals(other.Net, currentNet, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var otherNet = SafeText(other.Net);
                        if (string.IsNullOrWhiteSpace(otherNet))
                            continue;

                        // PWR wins: never overwrite a PWR net, and never propagate into a PWR-named net.
                        if (assigned.TryGetValue(otherNet, out var existing) && existing == "PWR")
                            continue;
                        if (MatchesTokens(otherNet, pwrDef?.NetNameTokens))
                        {
                            assigned[otherNet] = "PWR";
                            continue;
                        }

                        // RF beats HighSpeed.
                        if (assigned.TryGetValue(otherNet, out existing))
                        {
                            if (existing == "RF")
                                continue; // already RF, nothing to do
                            if (existing == "HighSpeed" && currentClass == "RF")
                            {
                                assigned[otherNet] = "RF";
                                propagationQueue.Enqueue(otherNet);
                            }
                            continue;
                        }

                        assigned[otherNet] = currentClass;
                        propagationQueue.Enqueue(otherNet);
                    }
                }
            }

            // Step 3 - collect into buckets; anything unassigned is Logic catch-all.
            foreach (var net in netNames)
            {
                if (string.IsNullOrWhiteSpace(net))
                    continue;
                if (assigned.TryGetValue(net, out var cls) && buckets.ContainsKey(cls))
                    buckets[cls].Add(net);
                else
                    buckets["Logic"].Add(net);
            }

            return ManagedNetClassOrder.ToDictionary(
                name => name,
                name => buckets[name].OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void BuildConnectivityIndices(
            JObject connectivity,
            out Dictionary<string, List<NetPin>> netToPins,
            out Dictionary<string, List<NetPin>> componentToPins,
            out Dictionary<string, string> componentComment,
            out Dictionary<string, int> componentPinCount)
        {
            netToPins = new Dictionary<string, List<NetPin>>(StringComparer.OrdinalIgnoreCase);
            componentToPins = new Dictionary<string, List<NetPin>>(StringComparer.OrdinalIgnoreCase);
            componentComment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            componentPinCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (connectivity == null)
                return;

            var components = connectivity["components"] as JArray ?? new JArray();
            foreach (var component in components.OfType<JObject>())
            {
                var designator = SafeText(component["designator"]);
                if (string.IsNullOrWhiteSpace(designator))
                    continue;
                var comment = SafeText(component["comment"]);
                componentComment[designator] = comment;

                var pins = component["pins"] as JArray ?? new JArray();
                componentPinCount[designator] = pins.Count;

                var compPins = new List<NetPin>();
                foreach (var pin in pins.OfType<JObject>())
                {
                    var pinNumber = SafeText(pin["number"]);
                    var pinName = SafeText(pin["name"]);
                    var net = SafeText(pin["net"]);
                    var entry = new NetPin
                    {
                        Designator = designator,
                        PinNumber = pinNumber,
                        PinName = pinName,
                        Net = net,
                    };
                    compPins.Add(entry);
                    if (!string.IsNullOrWhiteSpace(net))
                    {
                        if (!netToPins.TryGetValue(net, out var list))
                        {
                            list = new List<NetPin>();
                            netToPins[net] = list;
                        }
                        list.Add(entry);
                    }
                }
                componentToPins[designator] = compPins;
            }
        }

        /// <summary>
        /// A 2-pin passive that can sit in a signal series path: cap, resistor, inductor,
        /// ferrite bead, diode. ICs, connectors, transistors, and anything with != 2 pins are excluded.
        /// </summary>
        private static bool IsSeriesPassive(string designator, Dictionary<string, int> pinCount)
        {
            if (string.IsNullOrWhiteSpace(designator))
                return false;
            if (!pinCount.TryGetValue(designator, out var count) || count != 2)
                return false;

            var prefix = new string(designator.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            switch (prefix)
            {
                case "C":
                case "R":
                case "L":
                case "D":
                case "FB":
                case "BEAD":
                case "FERRITE":
                    return true;
                default:
                    return false;
            }
        }

        private sealed class NetPin
        {
            public string Designator { get; set; }
            public string PinNumber { get; set; }
            public string PinName { get; set; }
            public string Net { get; set; }
        }

        private static NetClassDefinition GetClass(PcbRulesProfile profile, string name) =>
            profile.NetClasses.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        private static bool MatchesTokens(string value, List<string> tokens)
        {
            if (tokens == null || tokens.Count == 0 || string.IsNullOrEmpty(value))
                return false;

            var upper = value.ToUpperInvariant();
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                if (upper.IndexOf(token.Trim().ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        private static void RemoveManagedRulesAndClasses(IPCB_Board board)
        {
            var toRemove = new List<IPCB_Primitive>();
            IterateBoardObjects(board, PcbTObjectId.eRuleObject, obj =>
            {
                if (obj is IPCB_Rule rule &&
                    SafeText(rule.GetState_Name()).StartsWith(ManagedRulePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    toRemove.Add(rule);
                }
            });
            IterateBoardObjects(board, PcbTObjectId.eClassObject, obj =>
            {
                if (obj is IPCB_ObjectClass netClass &&
                    ManagedNetClassNames.Contains(SafeText(netClass.GetState_Name())))
                {
                    toRemove.Add(netClass);
                }
            });

            foreach (var primitive in toRemove)
                board.RemovePCBObject(primitive);
        }

        private static void CreateNetClasses(
            IPCB_Board board,
            Dictionary<string, List<string>> assignments)
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            foreach (var pair in assignments.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value.Count == 0)
                    continue;

                var netClass = pcbServer.Internal_PCBClassFactoryByClassMember((int)TClassMemberKind.eClassMemberKind_Net)
                    as IPCB_ObjectClass;
                if (netClass == null)
                    throw new InvalidOperationException($"Failed to create net class '{pair.Key}'.");

                netClass.SetState_Name(pair.Key);
                netClass.SetState_SuperClass(false);
                foreach (var net in pair.Value)
                    netClass.AddMemberByName(net);

                board.AddPCBObject(netClass);
            }
        }

        private static void CreateWidthRules(IPCB_Board board, PcbRulesProfile profile)
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            foreach (var definition in profile.WidthRules)
            {
                var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_MaxMinWidth) as IPCB_MaxMinWidthConstraint;
                if (rule == null)
                    throw new InvalidOperationException($"Failed to create width rule '{definition.Name}'.");

                rule.SetState_Name(definition.Name);
                rule.SetState_Comment($"Auto-generated by Altium MCP Placement ({definition.NetClass})");
                rule.SetState_Scope1Expression(BuildNetClassScope(definition.NetClass));
                rule.SetState_DRCEnabled(true);

                foreach (var layer in EnumerateSignalLayers(board))
                {
                    rule.SetState_MinWidth(layer, AltiumApi.MilsToCoord(definition.MinMils));
                    rule.SetState_FavoredWidth(layer, AltiumApi.MilsToCoord(definition.PreferredMils));
                    rule.SetState_MaxWidth(layer, AltiumApi.MilsToCoord(definition.MaxMils));
                }

                if (definition.ImpedanceOhms.HasValue && definition.ImpedanceOhms.Value > 0)
                {
                    rule.SetState_ImpedanceDriven(true);
                    var ohms = definition.ImpedanceOhms.Value;
                    rule.SetState_MinImpedance(ohms);
                    rule.SetState_FavoredImpedance(ohms);
                    rule.SetState_MaxImpedance(ohms);
                }

                board.AddPCBObject(rule);
            }
        }

        private static void CreateClearanceRules(IPCB_Board board, PcbRulesProfile profile)
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            foreach (var definition in profile.ClearanceRules)
            {
                var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_Clearance) as IPCB_ClearanceConstraint;
                if (rule == null)
                    throw new InvalidOperationException($"Failed to create clearance rule '{definition.Name}'.");

                rule.SetState_Name(definition.Name);
                rule.SetState_Comment("Auto-generated by Altium MCP Placement");
                rule.SetState_Scope1Expression(BuildNetClassScope(definition.Scope1NetClass));
                rule.SetState_Scope2Expression(definition.Scope2Expression ?? "All");
                rule.SetState_Gap(AltiumApi.MilsToCoord(definition.GapMils));
                rule.SetState_DRCEnabled(true);
                board.AddPCBObject(rule);
            }
        }

        private static void CreateRoutingLayerRule(IPCB_Board board, PlaneRoutingPolicy policy)
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_RoutingLayers) as IPCB_RoutingLayersRule;
            if (rule == null)
                throw new InvalidOperationException("Failed to create routing layers rule.");

            rule.SetState_Name(policy.RuleName);
            rule.SetState_Comment("Keep traces on outer layers; do not route through internal plane layers.");
            rule.SetState_Scope1Expression(policy.ScopeExpression ?? "InNetClass('Logic') Or InNetClass('RF')");
            rule.SetState_DRCEnabled(true);
            rule.ResetRoutingLayers();

            var allowedTokens = policy.AllowedSignalLayerNameTokens ?? new List<string> { "Top", "Bottom" };
            foreach (var layer in EnumerateElectricalLayers(board))
            {
                var layerName = SafeText(board.LayerName(layer));
                var allow = allowedTokens.Any(token =>
                    layerName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                rule.SetState_RoutingLayers(layer, allow);
            }

            if (policy.BlockInternalPlaneLayers)
            {
                foreach (var layer in EnumerateInternalPlaneLayers(board))
                    rule.SetState_RoutingLayers(layer, false);
            }

            board.AddPCBObject(rule);
        }

        private static void CreatePlaneClearanceRule(IPCB_Board board, PlaneRoutingPolicy policy)
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_PowerPlaneClearance) as IPCB_PowerPlaneClearanceRule;
            if (rule == null)
                throw new InvalidOperationException("Failed to create power plane clearance rule.");

            rule.SetState_Name(policy.PlaneClearanceRuleName);
            rule.SetState_Comment("Keep copper away from solid plane regions.");
            rule.SetState_Scope1Expression("InNetClass('Logic') Or InNetClass('RF')");
            rule.SetState_Clearance(AltiumApi.MilsToCoord(policy.PlaneClearanceMils));
            rule.SetState_DRCEnabled(true);
            board.AddPCBObject(rule);
        }

        private static IEnumerable<IV7_Layer> EnumerateSignalLayers(IPCB_Board board)
        {
            return EnumerateFilteredLayers(board, board.Internal_SignalLayerIterator, iterator => iterator.Internal_AddFilter_SignalLayers());
        }

        private static IEnumerable<IV7_Layer> EnumerateElectricalLayers(IPCB_Board board)
        {
            return EnumerateFilteredLayers(board, board.Internal_ElectricalLayerIterator, iterator => iterator.Internal_AddFilter_ElectricalLayers());
        }

        private static IEnumerable<IV7_Layer> EnumerateInternalPlaneLayers(IPCB_Board board)
        {
            return EnumerateFilteredLayers(board, board.Internal_InternalPlaneLayerIterator, iterator => iterator.Internal_AddFilter_InternalPlaneLayers());
        }

        private static IEnumerable<IV7_Layer> EnumerateFilteredLayers(
            IPCB_Board board,
            Func<object> createIterator,
            Action<IPCB_LayerIterator> configure)
        {
            var layers = new List<IV7_Layer>();
            var iteratorObj = createIterator();
            if (iteratorObj is not IPCB_LayerIterator iterator)
                return layers;

            configure(iterator);
            if (!iterator.First())
                return layers;

            do
            {
                var layer = iterator.Internal_Layer();
                if (layer != null)
                    layers.Add(layer);
            }
            while (iterator.Next());

            return layers;
        }

        private static void IterateBoardObjects(
            IPCB_Board board,
            PcbTObjectId objectId,
            Action<object> handler)
        {
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(objectId));
            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    handler(obj);
                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }
        }

        private static string BuildNetClassScope(string netClassName) =>
            $"InNetClass('{SafeText(netClassName).Replace("'", "''")}')";

        private static string BuildSummary(PcbDesignRulesSetupResult result, PcbRulesProfile profile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PCB net classes and MCP design rules applied.");
            sb.AppendLine($"Profile: {PcbRulesProfile.ProfilePath}");
            foreach (var pair in result.NetClassAssignments.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {pair.Key}: {pair.Value.Count} net(s)");

            sb.AppendLine($"Rules created/updated: {result.CreatedRules.Count}");
            foreach (var rule in result.CreatedRules)
                sb.AppendLine($"  - {rule}");

            sb.AppendLine();
            sb.AppendLine("Tip: edit pcb-rules-profile.json in Documents\\AltiumEE to tune widths, tokens, and plane policy.");
            return sb.ToString().TrimEnd();
        }

        private static string SafeText(object value) => value?.ToString()?.Trim() ?? string.Empty;
    }
}
