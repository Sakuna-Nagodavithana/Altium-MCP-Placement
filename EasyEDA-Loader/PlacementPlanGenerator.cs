using DXP;
using EDP;
using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using EasyEDA_Loader.Placement;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    public static class PlacementPlanApplier
    {
        public static string DefaultPlanPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "placement_plan.json");

        public static string ApplyPlan(string planPath = null, bool anchorAsUnions = false)
        {
            var result = ApplyPlanDetailed(planPath, anchorAsUnions);
            var board = GetActiveBoard();
            var indexedCount = board != null ? IndexBoardComponents(board).Count : 0;
            return BuildLegacySummary(result, indexedCount);
        }

        public static IPCB_Board GetActiveBoard() => PcbDocumentHelper.ResolveProjectPcbBoard();

        private static List<string> lastPlannedDesignators = new List<string>();
        private static List<string> lastPlannedAnchors = new List<string>();

        public static IReadOnlyList<string> GetLastPlannedDesignators() => lastPlannedDesignators;

        public static IReadOnlyList<string> GetLastPlannedAnchors() => lastPlannedAnchors;

        public static IcClusterApplyResult ApplyPlanDetailed(
            string planPath = null,
            bool anchorAsUnions = false)
        {
            planPath ??= DefaultPlanPath;
            if (!File.Exists(planPath))
                throw new FileNotFoundException($"Placement plan not found: {planPath}");

            var root = JObject.Parse(File.ReadAllText(planPath));
            var movesElement = root["moves"] as JArray;
            if (movesElement == null)
                throw new InvalidOperationException("Placement plan is missing a moves array.");

            PcbDocumentHelper.EnsureProjectPcbBoard();

            var board = PcbDocumentHelper.ResolveProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open the project PCB document before applying a placement plan.");

            var componentMap = IndexBoardComponents(board);
            if (componentMap.Count == 0)
            {
                throw new InvalidOperationException(
                    "No placed components were found on the active PCB. Open the board (.PcbDoc) and try again.");
            }

            var result = new IcClusterApplyResult
            {
                Anchor = root.Value<string>("anchor") ?? "IC",
                ClusterCount = root.Value<int?>("cluster_count") ?? 0,
            };

            lastPlannedDesignators = new List<string>();
            lastPlannedAnchors = new List<string>();
            if (root["anchors"] is JArray anchorsElement)
            {
                foreach (var anchorItem in anchorsElement)
                {
                    var anchorName = anchorItem.Value<string>();
                    if (!string.IsNullOrWhiteSpace(anchorName))
                        lastPlannedAnchors.Add(anchorName);
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.Anchor) &&
                     !string.Equals(result.Anchor, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                lastPlannedAnchors.Add(result.Anchor);
            }

            var pcbServer = AltiumApi.GlobalVars.PCBServer;

            board.NewUndo();
            pcbServer.PreProcess();
            try
            {
                var moveIndex = 0;
                foreach (var move in movesElement)
                {
                    moveIndex++;
                    if (moveIndex % 25 == 0)
                        PcbDocumentHelper.PumpUi();
                    var designator = move.Value<string>("designator");
                    if (string.IsNullOrWhiteSpace(designator))
                        continue;

                    lastPlannedDesignators.Add(designator);

                    if (!componentMap.TryGetValue(designator, out var component) || component == null)
                    {
                        result.Skipped.Add($"{designator} (not on PCB)");
                        continue;
                    }

                    // A previous placement run may have put this component in a Union
                    // or left its Moveable flag false.  Make the state explicit before
                    // deciding that the component is already at its target.
                    PrepareComponentForMove(component, clearUnion: !anchorAsUnions);

                    var xMils = move.Value<double>("xMils");
                    var yMils = move.Value<double>("yMils");
                    double? rotation = move["rotation"]?.Type == JTokenType.Float || move["rotation"]?.Type == JTokenType.Integer
                        ? move.Value<double?>("rotation")
                        : null;
                    var layerName = move["layer"]?.ToString()?.Trim() ?? "top";

                    if (IsAlreadyAtTarget(component, xMils, yMils, rotation, layerName))
                    {
                        result.AlreadyPlacedCount++;
                        result.MovedDesignators.Add(designator);
                        continue;
                    }

                    if (TryMoveComponent(component, xMils, yMils, rotation, layerName))
                    {
                        result.MovedCount++;
                        result.MovedDesignators.Add(designator);
                    }
                    else
                    {
                        result.Skipped.Add($"{designator} (move failed)");
                    }
                }

                // The default is deliberately independent components.  Union grouping
                // is optional because Altium treats a Union as one placement object;
                // users cannot drag an individual member until that Union is broken.
                foreach (var anchor in lastPlannedAnchors)
                {
                    if (componentMap.TryGetValue(anchor, out var anchorComponent))
                        PrepareComponentForMove(anchorComponent, clearUnion: !anchorAsUnions);
                }

                if (anchorAsUnions)
                {
                    try
                    {
                        result.UnionCount = AnchorClustersAsUnions(
                            board,
                            componentMap,
                            movesElement,
                            lastPlannedAnchors,
                            wrapInPreProcess: false);
                    }
                    catch (Exception ex)
                    {
                        result.Skipped.Add($"Union anchor failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }

            result.PlannedMoveCount = lastPlannedDesignators.Count;
            PcbDocumentHelper.RefreshBoardView(board);
            return result;
        }

        /// <summary>
        /// Group the placed support parts with their anchor IC into Altium PCB Unions.
        /// Components sharing a union index move together when any member is dragged,
        /// so the user can reposition a whole cluster (IC + decoupling caps + passives)
        /// in one drag and then route. Each cluster gets a distinct union index that
        /// is higher than any existing union on the board.
        /// </summary>
        public static int AnchorClustersAsUnions(
            IPCB_Board board,
            Dictionary<string, IPCB_Component> componentMap,
            JArray movesElement,
            IReadOnlyList<string> anchorList,
            bool wrapInPreProcess = true)
        {
            if (board == null || componentMap == null || componentMap.Count == 0)
                return 0;

            // Group designators by anchor. Each anchor IC + its support parts form one union.
            var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var anchor in anchorList ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(anchor))
                    groups[anchor] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anchor };
            }

            foreach (var move in (movesElement ?? new JArray()).OfType<JObject>())
            {
                var anchor = move.Value<string>("anchor");
                var designator = move.Value<string>("designator");
                if (string.IsNullOrWhiteSpace(anchor) || string.IsNullOrWhiteSpace(designator))
                    continue;
                if (!groups.ContainsKey(anchor))
                    groups[anchor] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anchor };
                groups[anchor].Add(designator);
            }

            if (groups.Count == 0)
                return 0;

            // Start union indices above the highest existing union on the board so we
            // never merge with an existing union by accident.
            int nextUnion = FindMaxUnionIndex(board) + 1;
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            int anchoredClusters = 0;

            if (wrapInPreProcess)
                pcbServer.PreProcess();
            try
            {
                foreach (var group in groups)
                {
                    var members = group.Value;
                    if (members.Count < 2)
                        continue; // a single-part cluster has nothing to union

                    var unionId = nextUnion++;
                    bool anySet = false;
                    foreach (var des in members)
                    {
                        if (!componentMap.TryGetValue(des, out var comp) || comp == null)
                            continue;
                        try
                        {
                            if (comp is not IPCB_Primitive prim)
                                continue;
                            prim.BeginModify();
                            try
                            {
                                prim.SetState_UnionIndex(unionId);
                                prim.GraphicallyInvalidate();
                            }
                            finally
                            {
                                prim.EndModify();
                            }
                            anySet = true;
                        }
                        catch
                        {
                            // A single component failing the union call should not abort the rest.
                        }
                    }
                    if (anySet)
                        anchoredClusters++;
                }
            }
            finally
            {
                if (wrapInPreProcess)
                    pcbServer.PostProcess();
            }

            return anchoredClusters;
        }

        /// <summary>Re-anchor clusters from the saved placement plan without re-moving parts.</summary>
        public static int AnchorClustersFromPlan(string planPath = null)
        {
            planPath ??= DefaultPlanPath;
            if (!File.Exists(planPath))
                throw new FileNotFoundException($"Placement plan not found: {planPath}");

            var root = JObject.Parse(File.ReadAllText(planPath));
            var movesElement = root["moves"] as JArray;
            if (movesElement == null)
                return 0;

            PcbDocumentHelper.EnsureProjectPcbBoard();
            var board = PcbDocumentHelper.ResolveProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open the project PCB document before anchoring clusters.");

            var componentMap = IndexBoardComponents(board);
            var anchors = new List<string>();
            if (root["anchors"] is JArray anchorsElement)
            {
                foreach (var item in anchorsElement)
                {
                    var name = item.Value<string>();
                    if (!string.IsNullOrWhiteSpace(name))
                        anchors.Add(name);
                }
            }
            else if (!string.IsNullOrWhiteSpace(root.Value<string>("anchor")) &&
                     !string.Equals(root.Value<string>("anchor"), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                anchors.Add(root.Value<string>("anchor"));
            }

            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            board.NewUndo();
            pcbServer.PreProcess();
            int count;
            try
            {
                count = AnchorClustersAsUnions(board, componentMap, movesElement, anchors, wrapInPreProcess: false);
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }
            PcbDocumentHelper.RefreshBoardView(board);
            return count;
        }

        /// <summary>
        /// Resize every placed component's designator text (the "R1"/"C2" silk label)
        /// to a uniform small size so the board isn't cluttered. Height = text height,
        /// Width = stroke width, both in mils. Single undo batch.
        /// </summary>
        public static string ResizeAllDesignators(double heightMils = 20.0, double widthMils = 5.0)
        {
            PcbDocumentHelper.EnsureProjectPcbBoard();
            var board = PcbDocumentHelper.ResolveProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open the project PCB document before resizing designators.");

            var componentMap = IndexBoardComponents(board);
            if (componentMap.Count == 0)
                throw new InvalidOperationException("No placed components found on the active PCB.");

            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var heightCoord = (int)AltiumApi.MilsToCoord(heightMils);
            var widthCoord = (int)AltiumApi.MilsToCoord(widthMils);
            int resized = 0;

            board.NewUndo();
            pcbServer.PreProcess();
            try
            {
                foreach (var kvp in componentMap)
                {
                    var component = kvp.Value;
                    if (component == null) continue;
                    try
                    {
                        var nameObj = component.GetState_Name();
                        if (nameObj is not IPCB_Text text) continue;
                        if (text is not IPCB_Primitive prim) continue;
                        prim.BeginModify();
                        try
                        {
                            text.SetState_Size(heightCoord);
                            text.SetState_Width(widthCoord);
                            prim.GraphicallyInvalidate();
                        }
                        finally
                        {
                            prim.EndModify();
                        }
                        resized++;
                    }
                    catch
                    {
                        // Skip components whose designator can't be resized (locked, etc.)
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }
            PcbDocumentHelper.RefreshBoardView(board);
            return $"Resized {resized} designator(s) to {heightMils}mil height x {widthMils}mil stroke. Press Ctrl+Z to undo.";
        }

        /// <summary>
        /// Unlock every component on the board so the user can freely move parts
        /// after auto-placement. Some Altium operations inadvertently set the lock
        /// flag, which prevents dragging.
        /// </summary>
        public static int UnlockAllComponents(
            IPCB_Board board,
            bool clearUnions = true)
        {
            if (board == null) return 0;
            int unlocked = 0;
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
            pcbServer.PreProcess();
            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    if (obj is IPCB_Component comp)
                    {
                        try
                        {
                            if (comp is IPCB_Primitive prim)
                            {
                                prim.BeginModify();
                                try
                                {
                                    // Moveable is the PCB object movement lock.  The
                                    // component's LockStrings flag only controls its
                                    // designator/comment strings, so clear both.
                                    prim.SetState_Moveable(true);
                                    comp.SetState_LockStrings(false);
                                    if (clearUnions)
                                        prim.SetState_UnionIndex(0);
                                }
                                finally
                                {
                                    prim.EndModify();
                                }
                                unlocked++;
                            }
                        }
                        catch { }
                    }
                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
                pcbServer.PostProcess();
            }
            return unlocked;
        }

        private static void PrepareComponentForMove(
            IPCB_Component component,
            bool clearUnion)
        {
            if (component is not IPCB_Primitive primitive)
                return;

            primitive.BeginModify();
            try
            {
                primitive.SetState_Moveable(true);
                component.SetState_LockStrings(false);
                if (clearUnion)
                    primitive.SetState_UnionIndex(0);
            }
            finally
            {
                primitive.EndModify();
            }
        }

        private static int FindMaxUnionIndex(IPCB_Board board)
        {
            int max = 0;
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    if (obj is IPCB_Component comp)
                    {
                        try
                        {
                            if (comp is IPCB_Primitive prim)
                            {
                                var u = prim.GetState_UnionIndex();
                                if (u is int iv && iv > max) max = iv;
                            }
                        }
                        catch { /* union API not available on this component */ }
                    }
                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }
            return max;
        }

        private static string BuildLegacySummary(IcClusterApplyResult result, int indexedCount)
        {
            var summary =
                $"Moved {result.MovedCount} component(s), already in place {result.AlreadyPlacedCount}, " +
                $"for cluster '{result.Anchor}' on PCB ({indexedCount} parts indexed). " +
                "The PCB view should now show the selected group.";
            if (result.Skipped.Count > 0)
                summary += $"\nSkipped: {string.Join(", ", result.Skipped.Take(8))}" +
                           (result.Skipped.Count > 8 ? $" (+{result.Skipped.Count - 8} more)" : string.Empty);
            return summary;
        }

        private static IPCB_Board ResolveBoard() => PcbDocumentHelper.ResolveProjectPcbBoard();

        private static bool IsPcbDocument(IDocument document)
        {
            var kind = SafeText(document.DM_DocumentKind());
            return kind.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeText(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

        private static Dictionary<string, IPCB_Component> IndexBoardComponents(IPCB_Board board)
        {
            var map = new Dictionary<string, IPCB_Component>(StringComparer.OrdinalIgnoreCase);
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));

            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    if (obj is IPCB_Component component)
                    {
                        var designator = ReadPcbDesignator(component);
                        if (!string.IsNullOrWhiteSpace(designator) && !map.ContainsKey(designator))
                            map[designator] = component;
                    }

                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return map;
        }

        private static string ReadPcbDesignator(IPCB_Component component)
        {
            var designator = SafeText(component.GetState_SourceDesignator());
            if (!string.IsNullOrWhiteSpace(designator))
                return designator;

            designator = SafeText(component.GetState_Name()?.GetState_Text());
            return designator;
        }

        private static bool IsAlreadyAtTarget(
            IPCB_Component component,
            double xMils,
            double yMils,
            double? rotationDeg,
            string layerName)
        {
            var currentX = CoordUtils.CoordToMils(component.GetState_XLocation());
            var currentY = CoordUtils.CoordToMils(component.GetState_YLocation());
            if (Math.Abs(currentX - xMils) > 0.5 || Math.Abs(currentY - yMils) > 0.5)
                return false;

            // Also confirm the board side matches; if the cap should be on the bottom but
            // is currently on top, it is not "already placed" and must be flipped.
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var isOnBottom = component.GetState_FlippedOnLayer();
                var wantBottom = IsBottomLayerName(layerName);
                if (isOnBottom != wantBottom)
                    return false;
            }

            if (rotationDeg.HasValue &&
                Math.Abs(component.GetState_Rotation() - rotationDeg.Value) > 0.5)
            {
                return false;
            }

            return true;
        }

        private static bool IsBottomLayerName(string name) =>
            string.Equals(name, "bottom", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "bottomlayer", StringComparison.OrdinalIgnoreCase);

        private static bool TryMoveComponent(
            IPCB_Component component,
            double xMils,
            double yMils,
            double? rotationDeg,
            string layerName)
        {
            if (component is not IPCB_Primitive primitive)
                return false;

            var targetX = AltiumApi.MilsToCoord(xMils);
            var targetY = AltiumApi.MilsToCoord(yMils);

            primitive.BeginModify();
            try
            {
                primitive.SetState_Moveable(true);
                component.SetState_LockStrings(false);
                primitive.MoveToXY(targetX, targetY);

                // Flip to the target board side when needed. FlipComponent() is the
                // component-specific API that cleanly flips pads + silk + courtyard to
                // the other side. FlipXY (the primitive-level call) was causing some
                // components to become unmovable -- FlipComponent avoids that.
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var isOnBottom = component.GetState_FlippedOnLayer();
                    var wantBottom = IsBottomLayerName(layerName);
                    if (wantBottom != isOnBottom)
                        component.FlipComponent();
                }

                // Ensure the component is NOT locked -- some Altium operations can
                // inadvertently set the lock flag, which prevents the user from moving
                // the component after placement.
                try { component.SetState_LockStrings(false); } catch { }

                // Set rotation after the flip so it is correct for the final board side.
                if (rotationDeg.HasValue)
                    component.SetState_Rotation(rotationDeg.Value);

                primitive.GraphicallyInvalidate();
            }
            finally
            {
                primitive.EndModify();
            }

            return true;
        }
    }

    public static class PlacementPlanGenerator
    {
        private static readonly PlacementPlannerService Planner = new PlacementPlannerService();

        private static void EnsureConnectivityExport(McpSettings settings)
        {
            var exportPath = settings.ConnectivityFile ?? DesignExporter.DefaultExportPath;
            if (DesignExporter.IsConnectivityExportFresh(exportPath) &&
                File.Exists(exportPath))
            {
                try
                {
                    var existing = JObject.Parse(File.ReadAllText(exportPath));
                    var pcbComponents =
                        existing["pcb"]?["components"] as JArray ?? new JArray();
                    var hasGeometry = pcbComponents.Count > 0 &&
                                      pcbComponents
                                          .OfType<JObject>()
                                          .All(component =>
                                              component["bboxMils"] is JObject &&
                                              component["placement"] is JObject);
                    if (hasGeometry)
                        return;
                }
                catch
                {
                    // Re-export below if the cached file is incomplete or invalid.
                }
            }

            DesignExporter.ExportForPlacementPlanning(exportPath);
            PcbDocumentHelper.PumpUi();
        }

        private static JObject LoadConnectivityExport(McpSettings settings)
        {
            EnsureConnectivityExport(settings);
            var exportPath = settings.ConnectivityFile ?? DesignExporter.DefaultExportPath;
            if (!File.Exists(exportPath))
                throw new FileNotFoundException($"Connectivity export not found: {exportPath}");

            return JObject.Parse(File.ReadAllText(exportPath));
        }

        private static string PlannerSummary(JObject resultRoot, string successPrefix)
        {
            if (resultRoot.Value<bool?>("found") != true)
            {
                var error = resultRoot.Value<string>("error") ?? "Placement plan generation failed.";
                throw new InvalidOperationException(error);
            }

            var moveCount = resultRoot.Value<int?>("move_count") ?? 0;
            var clusterCount = resultRoot.Value<int?>("cluster_count");
            if (clusterCount.HasValue)
            {
                return
                    $"{successPrefix} {clusterCount.Value} module(s) " +
                    $"({moveCount} move(s)) -> {PlacementPlanApplier.DefaultPlanPath}";
            }

            var anchor = resultRoot.Value<string>("anchor") ?? "IC";
            return
                $"{successPrefix} {anchor.Trim().ToUpperInvariant()} " +
                $"({moveCount} move(s)) -> {PlacementPlanApplier.DefaultPlanPath}";
        }

        public static string Generate(
            string anchorDesignator,
            double spacingMils = 80.0,
            double maxRadiusMils = 900.0,
            double maxSchematicDistanceMils = 2500.0,
            string layoutMode = "pin_accurate")
        {
            if (string.IsNullOrWhiteSpace(anchorDesignator))
                throw new ArgumentException("Anchor designator is required.", nameof(anchorDesignator));

            var settings = McpServerManager.LoadSettings();
            var data = LoadConnectivityExport(settings);
            var plan = Planner.BuildIcPlacementPlan(
                data,
                anchorDesignator.Trim(),
                spacingMils,
                maxRadiusMils,
                schematicScale: 0.12,
                maxSchematicDistanceMils: maxSchematicDistanceMils,
                layoutMode: layoutMode.Trim(),
                sameSheetOnly: true,
                excludeGlobalNets: true);
            Planner.WritePlacementPlan(plan, PlacementPlanApplier.DefaultPlanPath);
            return PlannerSummary(plan, "Generated cluster plan for");
        }

        public static string GenerateAll(
            double spacingMils = 80.0,
            double maxRadiusMils = 900.0,
            double maxSchematicDistanceMils = 2500.0,
            string layoutMode = "pin_accurate")
        {
            var settings = McpServerManager.LoadSettings();
            var data = LoadConnectivityExport(settings);
            var plan = Planner.BuildAllIcClusterPlan(
                data,
                spacingMils,
                maxRadiusMils,
                schematicScale: 0.12,
                maxSchematicDistanceMils: maxSchematicDistanceMils,
                layoutMode: layoutMode.Trim(),
                sameSheetOnly: true,
                excludeGlobalNets: true);
            Planner.WritePlacementPlan(plan, PlacementPlanApplier.DefaultPlanPath);
            return PlannerSummary(plan, "Generated all-cluster plan for");
        }

        public static string GenerateForceDirected(
            int iterations = 220,
            double spacingMils = ForceDirectedOptimizer.DefaultSpacingMils,
            double gridMils = 10.0,
            bool lockIcs = true)
        {
            var settings = McpServerManager.LoadSettings();
            var data = LoadConnectivityExport(settings);
            var plan = ForceDirectedOptimizer.OptimizeBoard(
                data,
                iterations: iterations,
                spacingMils: spacingMils,
                gridMils: gridMils,
                lockIcs: lockIcs);
            Planner.WritePlacementPlan(plan, PlacementPlanApplier.DefaultPlanPath);
            if (plan.Value<bool?>("found") != true)
            {
                var error = plan.Value<string>("error") ?? "Force-directed + SA optimization failed.";
                throw new InvalidOperationException(error);
            }

            var moveCount = plan.Value<int?>("move_count") ?? 0;
            var before = plan.Value<double?>("cost_before") ?? 0;
            var after = plan.Value<double?>("cost_after") ?? 0;
            return
                $"Force+SA plan: {moveCount} move(s), cost {before:0} → {after:0} " +
                $"(locked {plan.Value<int?>("locked_count") ?? 0} connectors/ICs, " +
                $"spacing {spacingMils:0} mil, rotations enabled) " +
                $"-> {PlacementPlanApplier.DefaultPlanPath}";
        }
    }
}
