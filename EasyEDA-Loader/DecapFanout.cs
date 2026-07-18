using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// After placement: put plane vias next to decoupling / power pads (dogbone-style fanout).
    /// Pros do this so power pins hit Mid GND / power pours with short loops.
    /// </summary>
    internal static class DecapFanout
    {
        public static string FanoutDecouplingAndPowerPads(
            double viaOffsetMils = 28.0,
            double minSpacingMils = 22.0)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document first.");

            var gndNet = FindRailNet(board, preferGnd: true);
            var powerNets = FindPowerRailNets(board);
            if (gndNet == null && powerNets.Count == 0)
                throw new InvalidOperationException("No GND / power nets found for fanout vias.");

            var profile = PcbRulesProfile.LoadOrCreateDefault();
            var viaDef = (profile.ViaStyleRules ?? new List<ViaStyleRuleDefinition>())
                .FirstOrDefault(r => string.Equals(r.NetClass, "PWR", StringComparison.OrdinalIgnoreCase))
                ?? (profile.ViaStyleRules ?? new List<ViaStyleRuleDefinition>()).FirstOrDefault();
            var diameter = viaDef?.PreferDiameterMils > 0 ? viaDef.PreferDiameterMils : 18.0;
            var hole = viaDef?.PreferHoleMils > 0 ? viaDef.PreferHoleMils : 8.0;

            var existing = CollectViaCenters(board);
            var candidates = new List<(double X, double Y, IPCB_Net Net, string Why)>();

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
                    if (string.IsNullOrEmpty(des))
                        continue;

                    var isDecap = des.StartsWith("C", StringComparison.OrdinalIgnoreCase);
                    var isIc = des.StartsWith("U", StringComparison.OrdinalIgnoreCase) ||
                               des.StartsWith("IC", StringComparison.OrdinalIgnoreCase);
                    if (!isDecap && !isIc)
                        continue;

                    foreach (var pad in EnumeratePads(comp))
                    {
                        var net = pad.Net;
                        if (net == null)
                            continue;
                        var netName = Safe(net.GetState_Name());
                        if (string.IsNullOrEmpty(netName))
                            continue;

                        IPCB_Net target = null;
                        if (gndNet != null && IsGndName(netName))
                            target = gndNet;
                        else if (powerNets.TryGetValue(netName, out var pwr))
                            target = pwr;
                        else
                            continue;

                        // Place via offset from pad center, slightly away from component origin (fanout).
                        var ox = CoordUtils.CoordToMils(comp.GetState_XLocation());
                        var oy = CoordUtils.CoordToMils(comp.GetState_YLocation());
                        var px = pad.X;
                        var py = pad.Y;
                        var dx = px - ox;
                        var dy = py - oy;
                        var len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1)
                        {
                            dx = viaOffsetMils;
                            dy = 0;
                            len = viaOffsetMils;
                        }

                        var ux = dx / len;
                        var uy = dy / len;
                        // For decaps: via just outside the pad toward IC; for IC pads: via just outside away from center.
                        var sign = isDecap ? -1.0 : 1.0;
                        var vx = px + ux * sign * viaOffsetMils;
                        var vy = py + uy * sign * viaOffsetMils;
                        candidates.Add((vx, vy, target, $"{des}.{pad.Name}/{netName}"));
                    }
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            if (candidates.Count == 0)
                return "No GND/power pads found on C*/U* parts for fanout.";

            var placed = 0;
            var skipped = 0;
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            board.NewUndo();
            pcbServer.PreProcess();
            try
            {
                foreach (var c in candidates)
                {
                    if (existing.Any(e => Dist(e.X, e.Y, c.X, c.Y) < minSpacingMils))
                    {
                        skipped++;
                        continue;
                    }

                    var via = CreateVia(board, c.Net, c.X, c.Y, diameter, hole);
                    if (via == null)
                    {
                        skipped++;
                        continue;
                    }

                    board.AddPCBObject(via);
                    existing.Add((c.X, c.Y));
                    placed++;
                }

                board.GraphicallyInvalidate();
            }
            finally
            {
                pcbServer.PostProcess();
                board.EndUndo();
            }

            PcbDocumentHelper.RefreshBoardView(board);

            return
                $"Decap/power fanout vias: placed {placed}, skipped {skipped} (too close / failed).\n" +
                $"Via {diameter:0}/{hole:0} mil · offset {viaOffsetMils:0} mil from pads.\n" +
                "Pros put short vias at power/GND pads so planes carry current — then route RF/signals.\n" +
                "Ctrl+Z to undo. For dense BGAs also set Fanout Control rules (Setup Net Classes).";
        }

        /// <summary>Add a board-level Fanout Control rule (BGA/auto) used by Altium Interactive Fanout.</summary>
        public static void EnsureFanoutControlRule(IPCB_Board board)
        {
            if (board == null)
                return;
            const string name = "MCP - Fanout Control";
            var pcbServer = AltiumApi.GlobalVars.PCBServer;

            // Skip if already present.
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            try
            {
                iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eRuleObject));
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    try
                    {
                        var n = obj.GetType().GetMethod("GetState_Name")?.Invoke(obj, null) as string;
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                    catch { }
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            try
            {
                var rule = pcbServer.Internal_PCBRuleFactory((int)TRuleKind.eRule_FanoutControl);
                if (rule == null)
                    return;
                rule.GetType().GetMethod("SetState_Name")?.Invoke(rule, new object[] { name });
                rule.GetType().GetMethod("SetState_Comment")?.Invoke(rule, new object[]
                {
                    "MCP default fanout: Auto style for Interactive Fanout / BGA escape."
                });
                rule.GetType().GetMethod("SetState_Scope1Expression")?.Invoke(rule, new object[] { "All" });
                rule.GetType().GetMethod("SetState_DRCEnabled")?.Invoke(rule, new object[] { true });

                // Prefer Auto fanout style if enum available.
                try
                {
                    var style = Enum.Parse(typeof(TFanoutStyle), "eFanoutStyle_Auto");
                    rule.GetType().GetMethod("SetState_FanoutStyle")?.Invoke(rule, new[] { style });
                }
                catch { }

                try
                {
                    var dir = Enum.Parse(typeof(TFanoutDirection), "eFanoutDirection_OutOnly");
                    rule.GetType().GetMethod("SetState_FanoutDirection")?.Invoke(rule, new[] { dir });
                }
                catch { }

                board.AddPCBObject(rule);
            }
            catch
            {
                // Optional — not all Altium builds expose Fanout Control the same way.
            }
        }

        private sealed class PadInfo
        {
            public string Name;
            public double X, Y;
            public IPCB_Net Net;
        }

        private static IEnumerable<PadInfo> EnumeratePads(IPCB_Component component)
        {
            object gItObj = null;
            try
            {
                gItObj = component.Internal_GroupIterator_Create();
                var gIt = (IPCB_AbstractIterator)gItObj;
                gIt.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.ePadObject));
                for (var pObj = gIt.FirstPCBObject(); pObj != null; pObj = gIt.NextPCBObject())
                {
                    if (pObj is not IPCB_Pad pad)
                        continue;
                    IPCB_Net net = null;
                    try { net = (pad as IPCB_Primitive)?.Internal_GetState_Net() as IPCB_Net; } catch { }
                    yield return new PadInfo
                    {
                        Name = Safe(pad.GetState_Name()),
                        X = CoordUtils.CoordToMils(pad.GetState_XLocation()),
                        Y = CoordUtils.CoordToMils(pad.GetState_YLocation()),
                        Net = net,
                    };
                }
            }
            finally
            {
                if (gItObj != null)
                {
                    try { component.GroupIterator_Destroy(ref gItObj); } catch { }
                }
            }
        }

        private static IPCB_Via CreateVia(IPCB_Board board, IPCB_Net net, double x, double y, double size, double hole)
        {
            var via = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.eViaObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Via;
            if (via == null)
                return null;
            via.SetState_HighLayer(new V7_Layer(TLayerConstant.eTopLayer));
            via.SetState_LowLayer(new V7_Layer(TLayerConstant.eBottomLayer));
            via.SetState_XLocation(AltiumApi.MilsToCoord(x));
            via.SetState_YLocation(AltiumApi.MilsToCoord(y));
            via.SetState_Size(AltiumApi.MilsToCoord(size));
            via.SetState_HoleSize(AltiumApi.MilsToCoord(hole));
            try
            {
                via.GetType().GetMethod("SetState_Net")?.Invoke(via, new object[] { net });
            }
            catch
            {
                try { via.GetType().GetMethod("Internal_SetState_Net")?.Invoke(via, new object[] { net }); }
                catch { }
            }
            return via;
        }

        private static List<(double X, double Y)> CollectViaCenters(IPCB_Board board)
        {
            var list = new List<(double, double)>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eViaObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Via via)
                        continue;
                    list.Add((
                        CoordUtils.CoordToMils(via.GetState_XLocation()),
                        CoordUtils.CoordToMils(via.GetState_YLocation())));
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return list;
        }

        private static IPCB_Net FindRailNet(IPCB_Board board, bool preferGnd)
        {
            IPCB_Net exact = null;
            IPCB_Net fuzzy = null;
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eNetObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Net net)
                        continue;
                    var name = Safe(net.GetState_Name());
                    if (preferGnd)
                    {
                        if (string.Equals(name, "GND", StringComparison.OrdinalIgnoreCase))
                            exact = net;
                        else if (IsGndName(name))
                            fuzzy ??= net;
                    }
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return exact ?? fuzzy;
        }

        private static Dictionary<string, IPCB_Net> FindPowerRailNets(IPCB_Board board)
        {
            var map = new Dictionary<string, IPCB_Net>(StringComparer.OrdinalIgnoreCase);
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eNetObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Net net)
                        continue;
                    var name = Safe(net.GetState_Name());
                    if (string.IsNullOrEmpty(name) || IsGndName(name))
                        continue;
                    if (PcbClearanceDrc.PowerRail(name) != null)
                        map[name] = net;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return map;
        }

        private static bool IsGndName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            var n = name.Trim();
            return string.Equals(n, "GND", StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith("GND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "VSS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "AGND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "DGND", StringComparison.OrdinalIgnoreCase);
        }

        private static double Dist(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
    }
}
