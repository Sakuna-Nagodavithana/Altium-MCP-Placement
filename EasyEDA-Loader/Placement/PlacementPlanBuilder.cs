using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    internal static class PlacementPlanBuilder
    {
        internal static Tuple<JArray, string> BuildMovesForGrouping(
            JObject grouping,
            Dictionary<string, JObject> pcbComponents,
            double spacingMils = 80.0,
            string layoutMode = "pin_accurate",
            double maxRadiusMils = 900.0,
            double schematicScale = 0.12,
            int gridCols = 6,
            List<Tuple<double, double>> sharedPlacedPoints = null,
            List<string> sharedPlacedLayers = null,
            List<Tuple<double, double>> sharedPlacedHalfSizes = null)
        {
            var target = PlacementConstants.JsonStr(grouping["anchor"]);
            if (!pcbComponents.TryGetValue(target, out var anchorPcb))
                return Tuple.Create(new JArray(), $"PCB placement for '{target}' not found.");

            var anchorXy = PlacementConstants.PlacementXy(anchorPcb["placement"]);
            if (anchorXy == null)
                return Tuple.Create(new JArray(), $"PCB coordinates missing for anchor '{target}'.");

            var anchorSchXy = PlacementConstants.PlacementXy(grouping["anchor_placement"]);
            var pinLayout = grouping["pin_layout"] as JObject ?? new JObject();
            var pcbPinIndex = PlacementLayout.BuildPcbPinIndex(anchorPcb);
            var mode = PlacementLayout.NormalizeLayoutMode(layoutMode);
            var effectiveScale = schematicScale;
            if (mode == "pin_accurate")
            {
                effectiveScale = PlacementLayout.AutoSchematicScale(
                    grouping["support_components"] as JArray ?? new JArray(),
                    maxRadiusMils,
                    schematicScale);
            }

            var supportDes = new HashSet<string>(
                (grouping["support_components"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant())
                    .Where(d => !string.IsNullOrEmpty(d)),
                StringComparer.OrdinalIgnoreCase);

            var keepoutBoxes = grouping["data"] is JObject dataObj
                ? PlacementLayout.CollectKeepoutBoxes(dataObj)
                : new List<Tuple<double, double, double, double>>();
            var skip = new HashSet<string>(supportDes, StringComparer.OrdinalIgnoreCase) { target.ToUpperInvariant() };
            var pcbObstacles = PlacementLayout.CollectPcbObstacles(pcbComponents, skip);

            var moves = new JArray();
            var fallbackIndex = 0;
            var netSlotCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pinSlotCounts = new Dictionary<Tuple<string, string>, int>();
            var placedPoints = sharedPlacedPoints ?? new List<Tuple<double, double>>();
            var placedLayers = sharedPlacedLayers ?? new List<string>();
            var placedHalfSizes = sharedPlacedHalfSizes ?? new List<Tuple<double, double>>();

            // Seed the anchor IC as a fixed obstacle so local placement never overlaps it.
            // Skip re-seeding when a shared list already contains this anchor (multi-cluster).
            var alreadySeeded = sharedPlacedPoints != null && placedPoints.Any(p =>
                Math.Abs(p.Item1 - anchorXy.Item1) < 0.5 && Math.Abs(p.Item2 - anchorXy.Item2) < 0.5);
            if (!alreadySeeded)
            {
                var anchorLayer = PlacementLayout.NormalizeLayerName(PlacementConstants.JsonStr(anchorPcb["layer"]));
                var anchorHalf = PlacementLayout.GetBboxHalfSize(anchorPcb)
                    ?? Tuple.Create(60.0, 60.0);
                placedPoints.Add(anchorXy);
                placedLayers.Add(anchorLayer);
                placedHalfSizes.Add(anchorHalf);
            }

            var chains = grouping["chains"] as JArray ?? new JArray();
            var chainLookup = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var supportByDes = (grouping["support_components"] as JArray ?? new JArray())
                .OfType<JObject>()
                .ToDictionary(
                    item => PlacementConstants.JsonStr(item["designator"]).Trim().ToUpperInvariant(),
                    item => item,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var chain in chains.OfType<JObject>())
            {
                var members = (chain["members"] as JArray ?? new JArray())
                    .Select(m => PlacementConstants.JsonStr(m).Trim().ToUpperInvariant())
                    .Where(m => !string.IsNullOrEmpty(m))
                    .ToList();
                for (var index = 0; index < members.Count; index++)
                {
                    chainLookup[members[index]] = new JObject
                    {
                        ["chain_id"] = chain["chain_id"],
                        ["chain_index"] = index,
                        ["chain_length"] = members.Count,
                        ["chain_members"] = new JArray(members),
                    };
                }
            }

            var chainedDesignators =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDesignators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AppendMove(
                JObject item,
                double targetX,
                double targetY,
                string method,
                double? targetPinAngle,
                double? standoffMils,
                int pinSlot,
                double angleOffsetDeg,
                JObject chainMeta,
                double? rotationDeg,
                string layer = "top",
                bool mirror = false)
            {
                var designator = PlacementConstants.JsonStr(item["designator"]);
                if (!seenDesignators.Add(designator))
                    return;
                if (!pcbComponents.TryGetValue(designator, out var pcbComponent))
                    return;

                var current = pcbComponent["placement"] as JObject ?? new JObject();
                var currentXy = PlacementConstants.PlacementXy(current);
                var placementAngle = Math.Atan2(targetY - anchorXy.Item2, targetX - anchorXy.Item1) * 180.0 / Math.PI;
                double? rotation = rotationDeg;
                if (!rotation.HasValue)
                    rotation = current["rotation"]?.Type == JTokenType.Float || current["rotation"]?.Type == JTokenType.Integer
                        ? current.Value<double?>("rotation")
                        : null;
                if (!rotation.HasValue && item["schematic"]?["placement"] is JObject schPlacement)
                {
                    rotation = schPlacement["rotation"]?.Type == JTokenType.Float || schPlacement["rotation"]?.Type == JTokenType.Integer
                        ? schPlacement.Value<double?>("rotation")
                        : null;
                }

                var move = new JObject
                {
                    ["designator"] = designator,
                    ["anchor"] = target,
                    ["comment"] = item["comment"],
                    ["xMils"] = Math.Round(targetX, 3),
                    ["yMils"] = Math.Round(targetY, 3),
                    ["rotation"] = rotation.HasValue ? (JToken)rotation.Value : JValue.CreateNull(),
                    // Target board side: "top" or "bottom". Decoupling caps target the
                    // bottom side directly under the IC pin; everything else stays on top.
                    ["layer"] = string.IsNullOrWhiteSpace(layer) ? "top" : layer,
                    ["mirror"] = mirror,
                    ["method"] = method,
                    ["roles"] = item["roles"] ?? new JArray(),
                    ["nets"] = item["nets"] ?? new JArray(),
                    ["primary_net"] = item["primary_net"]?.DeepClone() ?? JValue.CreateNull(),
                    ["primary_ic_pin"] = item["primary_ic_pin"]?.DeepClone() ?? JValue.CreateNull(),
                    ["primary_ic_pin_name"] = item["primary_ic_pin_name"]?.DeepClone() ?? JValue.CreateNull(),
                    ["linked_ic_pins"] = item["linked_ic_pins"] ?? new JArray(),
                    ["targetPinAngleDeg"] = targetPinAngle.HasValue
                        ? (JToken)targetPinAngle.Value
                        : item["schematic"]?["pinAngleDeg"]?.DeepClone() ?? JValue.CreateNull(),
                    ["pinSlot"] = pinSlot,
                    ["angleOffsetDeg"] = Math.Round(angleOffsetDeg, 2),
                    ["standoffMils"] = standoffMils.HasValue
                        ? Math.Round(standoffMils.Value, 3)
                        : Math.Round(Math.Sqrt(Math.Pow(targetX - anchorXy.Item1, 2) + Math.Pow(targetY - anchorXy.Item2, 2)), 3),
                    ["placementAngleDeg"] = Math.Round(placementAngle, 2),
                    ["current"] = new JObject
                    {
                        ["xMils"] = currentXy != null ? (JToken)currentXy.Item1 : JValue.CreateNull(),
                        ["yMils"] = currentXy != null ? (JToken)currentXy.Item2 : JValue.CreateNull(),
                        ["rotation"] = current["rotation"]?.DeepClone() ?? JValue.CreateNull(),
                        ["layer"] = pcbComponent["layer"] ?? current["layer"] ?? "Top",
                    },
                };

                if (chainMeta != null)
                {
                    move["chainId"] = chainMeta["chain_id"];
                    move["chainIndex"] = chainMeta["chain_index"];
                    move["chainLength"] = chainMeta["chain_length"];
                    move["chainMembers"] = chainMeta["chain_members"];
                }

                moves.Add(move);
            }

            if ((mode == "pin_near" ||
                 mode == "pin_chain" ||
                 mode == "pin_accurate") &&
                chains.Count > 0)
            {
                foreach (var chain in chains.OfType<JObject>())
                {
                    var members = (chain["members"] as JArray ?? new JArray())
                        .Select(m => PlacementConstants.JsonStr(m).Trim().ToUpperInvariant())
                        .Where(m => !string.IsNullOrEmpty(m))
                        .ToList();

                    // Decoupling caps need the dedicated bottom-side, pin-accurate
                    // layout. RF matching chains use the Pi/T placement below.
                    var chainItems = members
                        .Where(member => supportByDes.ContainsKey(member))
                        .Select(member => supportByDes[member])
                        .ToList();
                    var hasDecoupling = chainItems.Any(item =>
                        string.Equals(
                            PlacementConstants.JsonStr(item["primary_role"]),
                            "decoupling",
                            StringComparison.OrdinalIgnoreCase));
                    var usesRfPiT = mode == "pin_accurate" &&
                                    chainItems.Any(item =>
                                        PlacementLayout.ShouldUseRfPiTPlacement(
                                            grouping,
                                            item,
                                            chainLookup));
                    if (hasDecoupling || usesRfPiT)
                        continue;

                    Tuple<double, double> previousXy = null;
                    Tuple<double, double> chainPinXy = null;
                    for (var index = 0; index < members.Count; index++)
                    {
                        if (!supportByDes.TryGetValue(members[index], out var item))
                            continue;
                        chainLookup.TryGetValue(members[index], out var meta);
                        pcbComponents.TryGetValue(members[index], out var chainPcbComp);
                        chainedDesignators.Add(members[index]);

                        // Resolve the IC pin PCB location for the first chain member so
                        // the chain starts at the pin (not the IC center). Subsequent
                        // members are placed relative to the previous member.
                        if (index == 0)
                        {
                            var pinNumber = PlacementConstants.JsonStr(item["primary_ic_pin"]);
                            chainPinXy = PlacementLayout.ResolveIcPinPcbXy(
                                pinNumber, pcbPinIndex, pinLayout, anchorSchXy, anchorXy, effectiveScale);
                        }

                        var result = PlacementLayout.ChainTargetXy(
                            item, index, members.Count, anchorXy, spacingMils, maxRadiusMils, placedPoints,
                            previousXy, chainPinXy, chainPcbComp, keepoutBoxes, pcbObstacles, placedLayers, placedHalfSizes);
                        previousXy = Tuple.Create(result.X, result.Y);
                        AppendMove(
                            item,
                            result.X,
                            result.Y,
                            result.Method,
                            result.TargetPinAngle,
                            result.StandoffMils,
                            result.PinSlot,
                            result.AngleOffsetDeg,
                            meta,
                            null);
                    }
                }
            }

            var supportComponents = grouping["support_components"] as JArray ?? new JArray();
            for (var index = 0; index < supportComponents.Count; index++)
            {
                if (!(supportComponents[index] is JObject item))
                    continue;
                var designator = PlacementConstants.JsonStr(item["designator"]);
                if (chainedDesignators.Contains(designator))
                    continue;
                if (!pcbComponents.TryGetValue(designator, out var pcbComponent))
                    continue;

                double? targetPinAngle = null;
                double? standoffMils = null;
                var pinSlot = 0;
                var angleOffsetDeg = 0.0;
                double? rotationDeg = null;
                double targetX;
                double targetY;
                string method;

                if (mode == "pin_accurate" &&
                    PlacementLayout.ShouldUseRfPiTPlacement(grouping, item, chainLookup))
                {
                    var pinNumber = PlacementConstants.JsonStr(item["primary_ic_pin"]);
                    var pinXy = PlacementLayout.ResolveIcPinPcbXy(
                        pinNumber,
                        pcbPinIndex,
                        pinLayout,
                        anchorSchXy,
                        anchorXy,
                        effectiveScale);
                    chainLookup.TryGetValue(designator, out var chainMeta);
                    var chainIndex = chainMeta?.Value<int?>("chain_index") ?? 0;
                    if (pinXy != null)
                    {
                        var rfResult = PlacementLayout.RfPiTTargetXy(
                            item,
                            chainIndex,
                            pinXy,
                            anchorXy,
                            spacingMils,
                            maxRadiusMils,
                            placedPoints,
                            pcbComponent,
                            keepoutBoxes,
                            pcbObstacles,
                            placedLayers,
                            placedHalfSizes);
                        AppendMove(
                            item,
                            rfResult.X,
                            rfResult.Y,
                            rfResult.Method,
                            rfResult.TargetPinAngle,
                            rfResult.StandoffMils,
                            rfResult.PinSlot,
                            rfResult.AngleOffsetDeg,
                            chainMeta,
                            rfResult.RotationDeg,
                            rfResult.Layer,
                            rfResult.Mirror);
                        continue;
                    }
                }

                if (mode == "schematic_mirror")
                {
                    var mirror = PlacementLayout.MirrorTargetXy(item, anchorXy, effectiveScale, maxRadiusMils);
                    targetX = mirror.Item1;
                    targetY = mirror.Item2;
                    method = mirror.Item3;
                }
                else if (mode == "pin_accurate")
                {
                    var accurate = PlacementLayout.PinAccurateTargetXy(
                        item,
                        anchorXy,
                        anchorSchXy,
                        pinLayout,
                        pcbPinIndex,
                        effectiveScale,
                        spacingMils,
                        maxRadiusMils,
                        pinSlotCounts,
                        placedPoints,
                        pcbComponent,
                        keepoutBoxes,
                        pcbObstacles,
                        placedLayers,
                        placedHalfSizes);
                    targetX = accurate.X;
                    targetY = accurate.Y;
                    method = accurate.Method;
                    targetPinAngle = accurate.TargetPinAngle;
                    standoffMils = accurate.StandoffMils;
                    pinSlot = accurate.PinSlot;
                    angleOffsetDeg = accurate.AngleOffsetDeg;
                    rotationDeg = accurate.RotationDeg;
                    AppendMove(
                        item,
                        targetX,
                        targetY,
                        method,
                        targetPinAngle,
                        standoffMils,
                        pinSlot,
                        angleOffsetDeg,
                        null,
                        rotationDeg,
                        accurate.Layer,
                        accurate.Mirror);
                    continue;
                }
                else if (mode == "pin_near" || mode == "pin_chain")
                {
                    var near = PlacementLayout.PinNearTargetXy(
                        item, index, anchorXy, spacingMils, maxRadiusMils, pinSlotCounts, placedPoints,
                        pcbComponent, keepoutBoxes, pcbObstacles, placedLayers, placedHalfSizes);
                    targetX = near.X;
                    targetY = near.Y;
                    method = near.Method;
                    targetPinAngle = near.TargetPinAngle;
                    standoffMils = near.StandoffMils;
                    pinSlot = near.PinSlot;
                    angleOffsetDeg = near.AngleOffsetDeg;
                }
                else if (mode == "compact")
                {
                    var compact = PlacementLayout.CompactTargetXy(
                        item, index, anchorXy, spacingMils, maxRadiusMils, netSlotCounts);
                    targetX = compact.Item1;
                    targetY = compact.Item2;
                    method = compact.Item3;
                    placedLayers.Add("top");
                    var compactHalf = PlacementLayout.GetBboxHalfSize(pcbComponent)
                        ?? Tuple.Create(PlacementLayout.CourtyardHalfSizeMils(item, pcbComponent),
                                        PlacementLayout.CourtyardHalfSizeMils(item, pcbComponent));
                    placedHalfSizes.Add(compactHalf);
                }
                else
                {
                    var row = fallbackIndex / gridCols;
                    var col = fallbackIndex % gridCols;
                    targetX = anchorXy.Item1 + (col - gridCols / 2.0) * spacingMils;
                    targetY = anchorXy.Item2 - spacingMils * (1.5 + row);
                    method = "grid_fallback";
                    fallbackIndex++;
                    placedLayers.Add("top");
                    placedHalfSizes.Add(PlacementLayout.GetBboxHalfSize(pcbComponent)
                        ?? Tuple.Create(30.0, 30.0));
                }

                AppendMove(
                    item,
                    targetX,
                    targetY,
                    method,
                    targetPinAngle,
                    standoffMils,
                    pinSlot,
                    angleOffsetDeg,
                    null,
                    rotationDeg);
            }

            return Tuple.Create(moves, (string)null);
        }

        private static JObject ResolveFinalMoveOverlaps(
            JArray moves,
            Dictionary<string, JObject> pcbComponents,
            JObject data,
            double spacingMils,
            double maxRadiusMils)
        {
            var moveDesignators = new HashSet<string>(
                moves
                    .OfType<JObject>()
                    .Select(move => PlacementConstants.JsonStr(move["designator"]))
                    .Where(designator => !string.IsNullOrWhiteSpace(designator)),
                StringComparer.OrdinalIgnoreCase);
            var occupied = new List<PlacementBox>();

            // Every component that is not being moved is a fixed obstacle. This
            // includes the anchor IC itself, which the earlier local collision pass
            // incorrectly skipped and allowed top-side passives to overlap.
            foreach (var pair in pcbComponents)
            {
                if (moveDesignators.Contains(pair.Key))
                    continue;
                var xy = PlacementConstants.PlacementXy(pair.Value["placement"]);
                if (xy == null)
                    continue;
                var layer = PlacementConstants.JsonStr(pair.Value["layer"]);
                var rotation = pair.Value["placement"]?.Value<double?>("rotation");
                occupied.Add(PlacementLayout.CreatePlacementBox(
                    pair.Key,
                    layer,
                    xy.Item1,
                    xy.Item2,
                    pair.Value,
                    rotation,
                    fallbackHalfSize: 60.0));
            }

            var keepouts = data != null
                ? PlacementLayout.CollectKeepoutBoxes(data)
                : new List<Tuple<double, double, double, double>>();
            var adjustedCount = 0;
            var unresolved = new JArray();
            var resolvedByDesignator =
                new Dictionary<string, PlacementBox>(StringComparer.OrdinalIgnoreCase);

            foreach (var move in moves.OfType<JObject>())
            {
                var designator = PlacementConstants.JsonStr(move["designator"]);
                if (!pcbComponents.TryGetValue(designator, out var pcbComponent))
                    continue;

                var desiredX = move.Value<double>("xMils");
                var desiredY = move.Value<double>("yMils");
                var layer = PlacementConstants.JsonStr(move["layer"]);
                var rotation = move["rotation"]?.Type == JTokenType.Float ||
                               move["rotation"]?.Type == JTokenType.Integer
                    ? move.Value<double?>("rotation")
                    : null;
                var fallback = PlacementLayout.CourtyardHalfSizeMils(
                    new JObject
                    {
                        ["designator"] = designator,
                        ["comment"] = move["comment"],
                    },
                    pcbComponent);
                var movingBox = PlacementLayout.CreatePlacementBox(
                    designator,
                    layer,
                    desiredX,
                    desiredY,
                    pcbComponent,
                    rotation,
                    fallback);

                var anchor = PlacementConstants.JsonStr(move["anchor"]);
                var anchorXy = pcbComponents.TryGetValue(anchor, out var anchorComponent)
                    ? PlacementConstants.PlacementXy(anchorComponent["placement"])
                    : null;

                // Keep a series chain local to the previous resolved member instead
                // of scattering all members around the IC when deconflicting.
                var chainIndex = move.Value<int?>("chainIndex") ?? 0;
                if (chainIndex > 0 &&
                    move["chainMembers"] is JArray chainMembers &&
                    chainIndex - 1 < chainMembers.Count)
                {
                    var previousDesignator =
                        PlacementConstants.JsonStr(chainMembers[chainIndex - 1]);
                    if (resolvedByDesignator.TryGetValue(
                            previousDesignator,
                            out var previousBox))
                    {
                        anchorXy = Tuple.Create(previousBox.X, previousBox.Y);
                    }
                }

                anchorXy ??= Tuple.Create(desiredX, desiredY);

                var clear = PlacementLayout.FindClearPlacement(
                    desiredX,
                    desiredY,
                    anchorXy,
                    movingBox,
                    occupied,
                    keepouts,
                    spacingMils,
                    maxRadiusMils);
                movingBox.X = clear.Item1;
                movingBox.Y = clear.Item2;

                if (clear.Item3)
                {
                    move["xMils"] = Math.Round(clear.Item1, 3);
                    move["yMils"] = Math.Round(clear.Item2, 3);
                    move["collisionAdjusted"] = true;
                    move["method"] =
                        PlacementConstants.JsonStr(move["method"]) + "_deconflict";
                    adjustedCount++;
                }

                var validationGap = Math.Max(spacingMils * 0.25, 20.0);
                var stillBlocked = occupied.Any(box =>
                    PlacementLayout.BoxesOverlap(
                        movingBox,
                        box,
                        validationGap));
                if (stillBlocked)
                {
                    move["collisionUnresolved"] = true;
                    unresolved.Add(designator);
                }

                occupied.Add(movingBox);
                resolvedByDesignator[designator] = movingBox;
            }

            return new JObject
            {
                ["adjusted_count"] = adjustedCount,
                ["unresolved_count"] = unresolved.Count,
                ["unresolved_designators"] = unresolved,
                ["all_clear"] = unresolved.Count == 0,
            };
        }

        public static JObject BuildAllIcClusterPlan(
            JObject data,
            double spacingMils = 80.0,
            string layoutMode = "pin_accurate",
            double maxRadiusMils = 900.0,
            double schematicScale = 0.12,
            bool sameSheetOnly = true,
            double maxSchematicDistanceMils = 2500.0,
            bool excludeGlobalNets = true,
            int minSupportCount = 1)
        {
            var anchorDesignators = PlacementSupport.ListClusterAnchorDesignators(
                data,
                sameSheetOnly,
                maxSchematicDistanceMils,
                excludeGlobalNets,
                minSupportCount);
            if (anchorDesignators.Count == 0)
            {
                return new JObject
                {
                    ["found"] = false,
                    ["mode"] = "all_clusters",
                    ["error"] = "No IC/U modules with local support parts were found on the PCB export.",
                };
            }

            var pcbComponents = PlacementConstants.PcbComponentIndex(data);
            var passiveOwner = PlacementSupport.ComputePassiveAnchorOwnership(
                data,
                anchorDesignators,
                pcbComponents,
                sameSheetOnly,
                maxSchematicDistanceMils,
                excludeGlobalNets,
                schematicScale);

            var assignedPassives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allMoves = new JArray();
            var clusterSummaries = new JArray();
            var clusterVerifications = new JArray();

            // Shared collision state across all IC clusters so cluster B cannot land on
            // cluster A's already-planned positions.
            var sharedPlacedPoints = new List<Tuple<double, double>>();
            var sharedPlacedLayers = new List<string>();
            var sharedPlacedHalfSizes = new List<Tuple<double, double>>();

            foreach (var anchor in anchorDesignators)
            {
                var grouping = PlacementSupport.GetIcSupportComponents(
                    data,
                    anchor,
                    sameSheetOnly,
                    maxSchematicDistanceMils,
                    excludeGlobalNets);
                grouping["data"] = data;
                grouping["pcb_keepouts"] = data["pcb"]?["keepouts"]?.DeepClone();
                if (grouping.Value<bool?>("found") != true)
                {
                    clusterSummaries.Add(new JObject
                    {
                        ["anchor"] = anchor,
                        ["move_count"] = 0,
                        ["support_count"] = 0,
                        ["note"] = grouping.Value<string>("error") ?? "not_found",
                    });
                    continue;
                }

                var availableSupport = (grouping["support_components"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Where(item =>
                    {
                        var des = PlacementConstants.JsonStr(item["designator"]);
                        return passiveOwner.TryGetValue(des, out var owner) && owner == anchor;
                    })
                    .ToList();

                if (availableSupport.Count == 0)
                {
                    clusterSummaries.Add(new JObject
                    {
                        ["anchor"] = anchor,
                        ["move_count"] = 0,
                        ["support_count"] = 0,
                        ["note"] = "support_parts_owned_by_other_ics",
                    });
                    continue;
                }

                var filteredGrouping = (JObject)grouping.DeepClone();
                var expandedSupport = PlacementChains.ExpandSeriesChainSupport(
                    data,
                    anchor,
                    new JArray(availableSupport),
                    maxSchematicDistanceMils);
                filteredGrouping["support_components"] = expandedSupport;
                filteredGrouping["chains"] = PlacementChains.DetectSupportChains(data, anchor, expandedSupport);

                var buildResult = BuildMovesForGrouping(
                    filteredGrouping,
                    pcbComponents,
                    spacingMils,
                    layoutMode,
                    maxRadiusMils,
                    schematicScale,
                    6,
                    sharedPlacedPoints,
                    sharedPlacedLayers,
                    sharedPlacedHalfSizes);
                var moves = buildResult.Item1;
                var error = buildResult.Item2;
                if (!string.IsNullOrEmpty(error))
                {
                    clusterSummaries.Add(new JObject
                    {
                        ["anchor"] = anchor,
                        ["move_count"] = 0,
                        ["support_count"] = availableSupport.Count,
                        ["note"] = error,
                    });
                    continue;
                }

                foreach (var move in moves.OfType<JObject>())
                {
                    var des = PlacementConstants.JsonStr(move["designator"]);
                    if (!assignedPassives.Add(des))
                        continue;
                    allMoves.Add(move);
                }

                pcbComponents.TryGetValue(anchor, out var anchorPcb);
                var anchorXy = PlacementConstants.PlacementXy(anchorPcb?["placement"]) ?? Tuple.Create(0.0, 0.0);
                var verification = PlacementSupport.VerifyClusterPlacement(
                    anchor,
                    anchorXy,
                    new JArray(availableSupport),
                    moves,
                    spacingMils,
                    maxRadiusMils);
                clusterVerifications.Add(verification);

                clusterSummaries.Add(new JObject
                {
                    ["anchor"] = anchor,
                    ["anchor_comment"] = grouping["anchor_comment"],
                    ["move_count"] = moves.Count,
                    ["support_count"] = availableSupport.Count,
                    ["verification_ok"] = verification.Value<bool?>("all_ok") ?? false,
                    ["verification_warn_count"] = verification.Value<int?>("warn_count") ?? 0,
                });
            }

            var activeClusters = clusterSummaries
                .OfType<JObject>()
                .Where(summary => summary.Value<int?>("move_count") > 0)
                .ToList();
            if (allMoves.Count == 0)
            {
                return new JObject
                {
                    ["found"] = false,
                    ["mode"] = "all_clusters",
                    ["anchors"] = new JArray(anchorDesignators),
                    ["clusters"] = clusterSummaries,
                    ["error"] = "No cluster moves were generated for the available IC/U modules.",
                };
            }

            var collisionValidation = ResolveFinalMoveOverlaps(
                allMoves,
                pcbComponents,
                data,
                spacingMils,
                maxRadiusMils);

            return new JObject
            {
                ["found"] = true,
                ["schemaVersion"] = PlacementConstants.PlanSchemaVersion,
                ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["mode"] = "all_clusters",
                ["anchor"] = "ALL",
                ["anchors"] = new JArray(anchorDesignators),
                ["cluster_count"] = activeClusters.Count,
                ["clusterName"] = "ALL_ICU_CLUSTERS",
                ["layoutMode"] = (layoutMode ?? "pin_near").ToLowerInvariant(),
                ["spacingMils"] = spacingMils,
                ["maxRadiusMils"] = maxRadiusMils,
                ["move_count"] = allMoves.Count,
                ["support_count"] = assignedPassives.Count,
                ["collision_validation"] = collisionValidation,
                ["verification"] = new JObject
                {
                    ["all_ok"] = clusterVerifications.OfType<JObject>().All(item => item.Value<bool?>("all_ok") == true),
                    ["cluster_count"] = clusterVerifications.Count,
                    ["ok_count"] = clusterVerifications.OfType<JObject>().Sum(item => item.Value<int?>("ok_count") ?? 0),
                    ["warn_count"] = clusterVerifications.OfType<JObject>().Sum(item => item.Value<int?>("warn_count") ?? 0),
                    ["clusters"] = clusterVerifications,
                },
                ["filters"] = new JObject
                {
                    ["same_sheet_only"] = sameSheetOnly,
                    ["max_schematic_distance_mils"] = maxSchematicDistanceMils,
                    ["exclude_global_nets"] = excludeGlobalNets,
                    ["min_support_count"] = minSupportCount,
                },
                ["clusters"] = clusterSummaries,
                ["moves"] = allMoves,
            };
        }

        public static JObject BuildIcPlacementPlan(
            JObject data,
            string icDesignator,
            double spacingMils = 80.0,
            string layoutMode = "pin_accurate",
            double maxRadiusMils = 900.0,
            double schematicScale = 0.12,
            bool sameSheetOnly = true,
            double maxSchematicDistanceMils = 2500.0,
            bool excludeGlobalNets = true,
            int gridCols = 6)
        {
            var grouping = PlacementSupport.GetIcSupportComponents(
                data,
                icDesignator,
                sameSheetOnly,
                maxSchematicDistanceMils,
                excludeGlobalNets);
            if (grouping.Value<bool?>("found") != true)
                return grouping;

            var target = PlacementConstants.JsonStr(grouping["anchor"]);
            var pcbComponents = PlacementConstants.PcbComponentIndex(data);

            var chains = PlacementChains.DetectSupportChains(
                data,
                target,
                PlacementChains.ExpandSeriesChainSupport(
                    data,
                    target,
                    grouping["support_components"] as JArray ?? new JArray(),
                    maxSchematicDistanceMils));
            var expandedSupport = PlacementChains.ExpandSeriesChainSupport(
                data,
                target,
                grouping["support_components"] as JArray ?? new JArray(),
                maxSchematicDistanceMils);

            grouping = (JObject)grouping.DeepClone();
            grouping["support_components"] = expandedSupport;
            grouping["chains"] = chains;
            grouping["data"] = data;

            var buildResult = BuildMovesForGrouping(
                grouping,
                pcbComponents,
                spacingMils,
                layoutMode,
                maxRadiusMils,
                schematicScale,
                gridCols);
            var moves = buildResult.Item1;
            var error = buildResult.Item2;
            if (!string.IsNullOrEmpty(error))
            {
                return new JObject
                {
                    ["found"] = false,
                    ["anchor"] = target,
                    ["error"] = error,
                };
            }

            var collisionValidation = ResolveFinalMoveOverlaps(
                moves,
                pcbComponents,
                data,
                spacingMils,
                maxRadiusMils);

            pcbComponents.TryGetValue(target, out var anchorPcb);
            var anchorXy = PlacementConstants.PlacementXy(anchorPcb?["placement"]) ?? Tuple.Create(0.0, 0.0);
            var verification = PlacementSupport.VerifyClusterPlacement(
                target,
                anchorXy,
                expandedSupport,
                moves,
                spacingMils,
                maxRadiusMils);

            return new JObject
            {
                ["found"] = true,
                ["schemaVersion"] = PlacementConstants.PlanSchemaVersion,
                ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["anchor"] = target,
                ["clusterName"] = target,
                ["layoutMode"] = (layoutMode ?? "pin_near").ToLowerInvariant(),
                ["spacingMils"] = spacingMils,
                ["maxRadiusMils"] = maxRadiusMils,
                ["support_count"] = (grouping["support_components"] as JArray)?.Count ?? 0,
                ["move_count"] = moves.Count,
                ["has_schematic_coords"] = grouping.Value<bool?>("has_schematic_coords") ?? false,
                ["has_pin_layout"] = grouping.Value<bool?>("has_pin_layout") ?? false,
                ["filters"] = grouping["filters"]?.DeepClone(),
                ["rejected_counts"] = grouping["rejected_counts"]?.DeepClone(),
                ["chains"] = chains,
                ["chain_count"] = chains.Count,
                ["collision_validation"] = collisionValidation,
                ["verification"] = verification,
                ["moves"] = moves,
            };
        }
    }
}
