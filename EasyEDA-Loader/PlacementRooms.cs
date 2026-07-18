using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Creates Altium Room Definition rules (Confinement Constraints) around each IC cluster.
    /// Pros use rooms to keep MCU / RF / power blocks together and apply local rules.
    /// </summary>
    internal static class PlacementRooms
    {
        public const string RoomRulePrefix = "MCP - Room ";
        public const string RoomClassPrefix = "MCP_Room_";
        private const double MarginMils = 80.0;

        public static string CreateRoomsFromLastPlan(bool alsoAnchorUnions = true)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document first.");

            var planPath = PlacementPlanApplier.DefaultPlanPath;
            if (!File.Exists(planPath))
                throw new FileNotFoundException(
                    "No placement_plan.json yet. Run Auto-Place All Components first.", planPath);

            var plan = JObject.Parse(File.ReadAllText(planPath));
            var moves = plan["moves"] as JArray ?? new JArray();
            var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var move in moves.OfType<JObject>())
            {
                var anchor = move.Value<string>("anchor");
                var des = move.Value<string>("designator");
                if (string.IsNullOrWhiteSpace(anchor) || string.IsNullOrWhiteSpace(des))
                    continue;
                if (!groups.ContainsKey(anchor))
                    groups[anchor] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anchor };
                groups[anchor].Add(des);
            }

            // Include standalone anchors listed in plan even with no moves.
            foreach (var a in plan["anchors"] as JArray ?? new JArray())
            {
                var name = a?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!groups.ContainsKey(name))
                    groups[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            }

            if (groups.Count == 0)
                return "No IC clusters found in placement_plan.json.";

            var componentMap = BuildComponentMap(board);
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var created = 0;
            var skipped = new List<string>();
            var sb = new StringBuilder();

            board.NewUndo();
            pcbServer.PreProcess();
            try
            {
                RemoveManagedRoomsAndClasses(board);

                foreach (var pair in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var members = pair.Value
                        .Where(d => componentMap.ContainsKey(d))
                        .ToList();
                    if (members.Count == 0)
                    {
                        skipped.Add(pair.Key + " (not on PCB)");
                        continue;
                    }

                    if (!TryGetClusterBounds(componentMap, members, out var left, out var bottom, out var right, out var top))
                    {
                        skipped.Add(pair.Key + " (no bounds)");
                        continue;
                    }

                    left -= AltiumApi.MilsToCoord(MarginMils);
                    bottom -= AltiumApi.MilsToCoord(MarginMils);
                    right += AltiumApi.MilsToCoord(MarginMils);
                    top += AltiumApi.MilsToCoord(MarginMils);

                    var className = SanitizeClassName(RoomClassPrefix + pair.Key);
                    var ruleName = RoomRulePrefix + pair.Key;

                    var compClass = pcbServer.Internal_PCBClassFactoryByClassMember(
                        (int)TClassMemberKind.eClassMemberKind_Component) as IPCB_ObjectClass;
                    if (compClass == null)
                    {
                        skipped.Add(pair.Key + " (class factory failed)");
                        continue;
                    }

                    compClass.SetState_Name(className);
                    compClass.SetState_SuperClass(false);
                    foreach (var m in members)
                        compClass.AddMemberByName(m);
                    board.AddPCBObject(compClass);

                    var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_ConfinementConstraint)
                        as IPCB_ConfinementConstraint;
                    if (rule == null)
                    {
                        skipped.Add(pair.Key + " (room rule factory failed)");
                        continue;
                    }

                    rule.SetState_Name(ruleName);
                    rule.SetState_Comment(
                        $"Auto room for cluster {pair.Key} ({members.Count} parts). Keep components inside.");
                    rule.SetState_Scope1Expression($"InComponentClass('{className}')");
                    rule.SetState_DRCEnabled(true);

                    try
                    {
                        // eConfineIn = keep matched components inside the room.
                        rule.SetState_Kind(TConfinementStyle.eConfineIn);
                    }
                    catch
                    {
                        try { rule.GetType().GetMethod("SetState_Kind")?.Invoke(rule, new object[] { 0 }); }
                        catch { }
                    }

                    try
                    {
                        // ConstraintLayer takes a layer constant (int), not V7_Layer.
                        rule.SetState_ConstraintLayer((int)TLayerConstant.eTopLayer);
                    }
                    catch
                    {
                        try
                        {
                            rule.GetType().GetMethod("SetState_ConstraintLayer")
                                ?.Invoke(rule, new object[] { (int)TLayerConstant.eTopLayer });
                        }
                        catch { }
                    }

                    if (!TrySetBoundingRect(rule, left, bottom, right, top))
                    {
                        skipped.Add(pair.Key + " (bounding rect failed)");
                        continue;
                    }

                    board.AddPCBObject(rule);
                    created++;
                    sb.AppendLine(
                        $"  {ruleName}: {members.Count} parts, " +
                        $"{CoordUtils.CoordToMils(right - left):0}×{CoordUtils.CoordToMils(top - bottom):0} mil");
                }

                if (alsoAnchorUnions)
                {
                    try
                    {
                        PlacementPlanApplier.AnchorClustersFromPlan();
                    }
                    catch { }
                }
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }

            PcbDocumentHelper.RefreshBoardView(board);

            return
                $"Created {created} placement room(s) (Altium Confinement / Room Definition).\n" +
                sb +
                (skipped.Count > 0 ? $"Skipped: {string.Join(", ", skipped.Take(8))}\n" : "") +
                "Pros use rooms to keep IC+passives together and apply local rules. " +
                "Drag the room outline or use Unions to move a whole cluster. Ctrl+Z to undo.";
        }

        private static string SanitizeClassName(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                    sb.Append(ch);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        private static Dictionary<string, IPCB_Component> BuildComponentMap(IPCB_Board board)
        {
            var map = new Dictionary<string, IPCB_Component>(StringComparer.OrdinalIgnoreCase);
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Component comp)
                        continue;
                    var des = (comp.GetState_SourceDesignator() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(des))
                        map[des] = comp;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return map;
        }

        private static bool TryGetClusterBounds(
            Dictionary<string, IPCB_Component> map,
            List<string> members,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = bottom = right = top = 0;
            var any = false;
            foreach (var des in members)
            {
                if (!map.TryGetValue(des, out var comp) || comp is not IPCB_Primitive prim)
                    continue;
                ICoordRect rect = null;
                try { rect = comp.Internal_BoundingRectangleNoNameComment(); } catch { }
                if (rect == null)
                {
                    try { rect = prim.Internal_BoundingRectangleForSelection(); } catch { }
                }
                if (rect == null)
                    continue;

                var l = rect.GetLeft();
                var b = rect.GetBottom();
                var r = rect.GetRight();
                var t = rect.GetTop();
                if (!any)
                {
                    left = l; bottom = b; right = r; top = t;
                    any = true;
                }
                else
                {
                    left = Math.Min(left, l);
                    bottom = Math.Min(bottom, b);
                    right = Math.Max(right, r);
                    top = Math.Max(top, t);
                }
            }

            return any;
        }

        private static bool TrySetBoundingRect(IPCB_ConfinementConstraint rule, int left, int bottom, int right, int top)
        {
            // Prefer typed BoundingRectangle setter if available.
            try
            {
                var method = rule.GetType().GetMethod("SetState_BoundingRectangle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    var paramType = method.GetParameters().FirstOrDefault()?.ParameterType;
                    if (paramType != null)
                    {
                        var rect = CreateCoordRect(paramType, left, bottom, right, top);
                        if (rect != null)
                        {
                            method.Invoke(rule, new[] { rect });
                            return true;
                        }
                    }
                }
            }
            catch { }

            // Fallback: X/Y + segments or individual edges via reflection.
            try
            {
                rule.SetState_XLocation(left);
                rule.SetState_YLocation(bottom);
            }
            catch { }

            try
            {
                var setLeft = rule.GetType().GetMethod("SetState_BoundingRectangle");
                // Last resort: set Left/Right/Top/Bottom on an existing rect from GetState
                var get = rule.GetType().GetMethod("GetState_BoundingRectangle")
                          ?? rule.GetType().GetMethod("Internal_GetState_BoundingRectangle");
                if (get != null)
                {
                    var existing = get.Invoke(rule, null);
                    if (existing != null)
                    {
                        SetRectEdges(existing, left, bottom, right, top);
                        var set = rule.GetType().GetMethod("SetState_BoundingRectangle")
                                  ?? rule.GetType().GetMethod("Internal_SetState_BoundingRectangle");
                        set?.Invoke(rule, new[] { existing });
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static object CreateCoordRect(Type paramType, int left, int bottom, int right, int top)
        {
            try
            {
                object instance = null;
                if (paramType.IsInterface)
                {
                    // Look for concrete CoordRect in PCB assemblies.
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type concrete = null;
                        try
                        {
                            concrete = asm.GetTypes().FirstOrDefault(t =>
                                !t.IsInterface && !t.IsAbstract && paramType.IsAssignableFrom(t) &&
                                (t.Name.IndexOf("CoordRect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 t.Name.Equals("TCoordRect", StringComparison.OrdinalIgnoreCase)));
                        }
                        catch { }

                        if (concrete != null)
                        {
                            instance = Activator.CreateInstance(concrete);
                            break;
                        }
                    }
                }
                else
                {
                    instance = Activator.CreateInstance(paramType);
                }

                if (instance == null)
                    return null;

                SetRectEdges(instance, left, bottom, right, top);
                return instance;
            }
            catch
            {
                return null;
            }
        }

        private static void SetRectEdges(object rect, int left, int bottom, int right, int top)
        {
            InvokeSet(rect, "SetLeft", left);
            InvokeSet(rect, "SetBottom", bottom);
            InvokeSet(rect, "SetRight", right);
            InvokeSet(rect, "SetTop", top);
            InvokeSet(rect, "SetState_Left", left);
            InvokeSet(rect, "SetState_Bottom", bottom);
            InvokeSet(rect, "SetState_Right", right);
            InvokeSet(rect, "SetState_Top", top);

            // Property setters
            TrySetProp(rect, "Left", left);
            TrySetProp(rect, "Bottom", bottom);
            TrySetProp(rect, "Right", right);
            TrySetProp(rect, "Top", top);
        }

        private static void InvokeSet(object target, string method, int value)
        {
            try
            {
                target.GetType().GetMethod(method)?.Invoke(target, new object[] { value });
            }
            catch { }
        }

        private static void TrySetProp(object target, string name, int value)
        {
            try
            {
                var prop = target.GetType().GetProperty(name);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(target, value);
            }
            catch { }
        }

        private static void RemoveManagedRoomsAndClasses(IPCB_Board board)
        {
            var toRemove = new List<object>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            try
            {
                // Rules
                iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eRuleObject));
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    try
                    {
                        var name = obj.GetType().GetMethod("GetState_Name")?.Invoke(obj, null) as string
                                   ?? string.Empty;
                        if (name.StartsWith(RoomRulePrefix, StringComparison.OrdinalIgnoreCase))
                            toRemove.Add(obj);
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            iteratorObj = board.Internal_BoardIterator_Create();
            iterator = (IPCB_AbstractIterator)iteratorObj;
            try
            {
                iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eClassObject));
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    try
                    {
                        if (obj is IPCB_ObjectClass cls)
                        {
                            var name = cls.GetState_Name() ?? string.Empty;
                            if (name.StartsWith(RoomClassPrefix, StringComparison.OrdinalIgnoreCase))
                                toRemove.Add(cls);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            foreach (var obj in toRemove)
            {
                try { board.RemovePCBObject(obj); } catch { }
            }
        }
    }
}
