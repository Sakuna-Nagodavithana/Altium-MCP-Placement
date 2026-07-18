using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Places GND stitch / fence vias along RF and HighSpeed (clock) routes after routing.
    /// </summary>
    internal static class ViaStitcher
    {
        public static string StitchRfAndClocks(
            double pitchMils = 50.0,
            double fenceOffsetMils = 30.0,
            bool includeRf = true,
            bool includeHighSpeed = true)
        {
            var board = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document first.");

            var classified = PcbDesignRulesSetup.Classify(useConnectivityHints: true);
            var targetNets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeRf && classified.NetClassAssignments.TryGetValue("RF", out var rfNets))
            {
                foreach (var n in rfNets)
                    targetNets.Add(n);
            }

            if (includeHighSpeed && classified.NetClassAssignments.TryGetValue("HighSpeed", out var hsNets))
            {
                foreach (var n in hsNets)
                    targetNets.Add(n);
            }

            if (targetNets.Count == 0)
                return "No RF / HighSpeed (clock) nets found. Run Setup Net Classes first, or check connectivity export.";

            var gndNet = FindPreferredGndNet(board);
            if (gndNet == null)
                throw new InvalidOperationException("Could not find a GND net on the PCB.");

            var profile = PcbRulesProfile.LoadOrCreateDefault();
            var viaDef = (profile.ViaStyleRules ?? new List<ViaStyleRuleDefinition>())
                .FirstOrDefault(r => string.Equals(r.NetClass, "RF", StringComparison.OrdinalIgnoreCase))
                ?? (profile.ViaStyleRules ?? new List<ViaStyleRuleDefinition>())
                    .FirstOrDefault(r => string.Equals(r.NetClass, "PWR", StringComparison.OrdinalIgnoreCase));

            var diameterMils = viaDef?.PreferDiameterMils > 0 ? viaDef.PreferDiameterMils : 18.0;
            var holeMils = viaDef?.PreferHoleMils > 0 ? viaDef.PreferHoleMils : 8.0;

            var existing = CollectExistingViaCenters(board);
            var tracks = CollectTracksForNets(board, targetNets);
            if (tracks.Count == 0)
                return $"Found {targetNets.Count} RF/HS net(s) but no copper tracks yet. Route them first, then stitch.";

            var candidates = new List<(double X, double Y)>();
            foreach (var track in tracks)
            {
                var length = Math.Sqrt(
                    (track.X2 - track.X1) * (track.X2 - track.X1) +
                    (track.Y2 - track.Y1) * (track.Y2 - track.Y1));
                if (length < 10)
                    continue;

                var dx = (track.X2 - track.X1) / length;
                var dy = (track.Y2 - track.Y1) / length;
                // Perpendicular unit vectors for fence vias on both sides.
                var px = -dy;
                var py = dx;

                var steps = Math.Max(1, (int)Math.Floor(length / Math.Max(20.0, pitchMils)));
                for (var i = 0; i <= steps; i++)
                {
                    var u = i / (double)steps;
                    var cx = track.X1 + (track.X2 - track.X1) * u;
                    var cy = track.Y1 + (track.Y2 - track.Y1) * u;
                    candidates.Add((cx + px * fenceOffsetMils, cy + py * fenceOffsetMils));
                    candidates.Add((cx - px * fenceOffsetMils, cy - py * fenceOffsetMils));
                }
            }

            // Deduplicate candidates and skip near existing vias / pads.
            var placed = 0;
            var skipped = 0;
            var minSpacing = Math.Max(diameterMils + 6.0, pitchMils * 0.45);
            var accepted = new List<(double X, double Y)>();

            foreach (var c in candidates)
            {
                if (accepted.Any(a => Dist(a.X, a.Y, c.X, c.Y) < minSpacing) ||
                    existing.Any(e => Dist(e.X, e.Y, c.X, c.Y) < minSpacing))
                {
                    skipped++;
                    continue;
                }

                accepted.Add(c);
            }

            if (accepted.Count == 0)
                return "No stitch via locations available (existing vias already dense enough).";

            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            pcbServer.PreProcess();
            try
            {
                board.NewUndo();
                foreach (var pt in accepted)
                {
                    var via = CreateBoardVia(board, gndNet, pt.X, pt.Y, diameterMils, holeMils);
                    if (via == null)
                    {
                        skipped++;
                        continue;
                    }

                    board.AddPCBObject(via);
                    placed++;
                    existing.Add(pt);
                }

                board.GraphicallyInvalidate();
            }
            finally
            {
                pcbServer.PostProcess();
            }

            var netPreview = string.Join(", ", targetNets.OrderBy(n => n).Take(12));
            if (targetNets.Count > 12)
                netPreview += $" … (+{targetNets.Count - 12})";

            return
                $"Via stitch complete.\n" +
                $"Placed {placed} GND vias along RF/HighSpeed routes (pitch {pitchMils:0} mil, offset {fenceOffsetMils:0} mil).\n" +
                $"Skipped {skipped} candidates (too close to existing copper).\n" +
                $"Nets: {netPreview}\n" +
                $"Via size {diameterMils:0} / hole {holeMils:0} mil. Ctrl+Z to undo.";
        }

        private static IPCB_Via CreateBoardVia(
            IPCB_Board board,
            IPCB_Net net,
            double xMils,
            double yMils,
            double sizeMils,
            double holeMils)
        {
            var via = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.eViaObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Via;
            if (via == null)
                return null;

            via.SetState_HighLayer(new V7_Layer(TLayerConstant.eTopLayer));
            via.SetState_LowLayer(new V7_Layer(TLayerConstant.eBottomLayer));
            via.SetState_XLocation(AltiumApi.MilsToCoord(xMils));
            via.SetState_YLocation(AltiumApi.MilsToCoord(yMils));
            via.SetState_Size(AltiumApi.MilsToCoord(sizeMils));
            via.SetState_HoleSize(AltiumApi.MilsToCoord(holeMils));
            AssignNet(via, net);
            return via;
        }

        private static void AssignNet(IPCB_Via via, IPCB_Net net)
        {
            if (via == null || net == null)
                return;

            try
            {
                var method = via.GetType().GetMethod("SetState_Net");
                if (method != null)
                {
                    method.Invoke(via, new object[] { net });
                    return;
                }
            }
            catch { }

            try
            {
                var method = via.GetType().GetMethod("Internal_SetState_Net");
                method?.Invoke(via, new object[] { net });
            }
            catch { }
        }

        private static IPCB_Net FindPreferredGndNet(IPCB_Board board)
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
                    var name = (net.GetState_Name() ?? string.Empty).Trim();
                    if (string.Equals(name, "GND", StringComparison.OrdinalIgnoreCase))
                        exact = net;
                    else if (fuzzy == null &&
                             (name.Equals("DGND", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("AGND", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("VSS", StringComparison.OrdinalIgnoreCase) ||
                              name.StartsWith("GND", StringComparison.OrdinalIgnoreCase)))
                        fuzzy = net;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return exact ?? fuzzy;
        }

        private static List<(double X, double Y)> CollectExistingViaCenters(IPCB_Board board)
        {
            var list = new List<(double X, double Y)>();
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

        private static List<TrackSeg> CollectTracksForNets(IPCB_Board board, HashSet<string> nets)
        {
            var tracks = new List<TrackSeg>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eTrackObject));
            try
            {
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    if (obj is not IPCB_Track track)
                        continue;

                    var netName = string.Empty;
                    try
                    {
                        if ((track as IPCB_Primitive)?.Internal_GetState_Net() is IPCB_Net net)
                            netName = net.GetState_Name() ?? string.Empty;
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(netName) || !nets.Contains(netName))
                        continue;

                    var layer = string.Empty;
                    try
                    {
                        layer = board.LayerName(track.GetState_V7Layer())?.ToString() ?? string.Empty;
                    }
                    catch
                    {
                        layer = track.GetState_V7Layer()?.ToString() ?? string.Empty;
                    }

                    var low = layer.ToLowerInvariant();
                    if (low.Contains("mechanical") || low.Contains("courtyard") || low.Contains("assembly") ||
                        low.Contains("overlay") || low.Contains("component center"))
                        continue;

                    tracks.Add(new TrackSeg
                    {
                        X1 = CoordUtils.CoordToMils(track.GetState_X1()),
                        Y1 = CoordUtils.CoordToMils(track.GetState_Y1()),
                        X2 = CoordUtils.CoordToMils(track.GetState_X2()),
                        Y2 = CoordUtils.CoordToMils(track.GetState_Y2()),
                    });
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            return tracks;
        }

        private static double Dist(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class TrackSeg
        {
            public double X1, Y1, X2, Y2;
        }
    }
}
