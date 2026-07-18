using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PCB;
using PcbTObjectId = PCB.TObjectId;
using Newtonsoft.Json.Linq;
using EasyEDA_Loader.Floorplan;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Applies a chosen floorplan variant: move ICs/connectors, optional board outline tracks,
    /// then optionally run Auto-Place for passives.
    /// </summary>
    internal static class FloorplanApplier
    {
        public static string DefaultFloorplanPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "floorplan_plan.json");

        public static string Apply(
            FloorplanVariant variant,
            bool drawBoardOutline = true,
            bool autoPlacePassivesAfter = true)
        {
            if (variant == null)
                throw new ArgumentNullException(nameof(variant));

            var plan = variant.ToPlacementPlan();
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultFloorplanPath));
            File.WriteAllText(DefaultFloorplanPath, plan.ToString(Newtonsoft.Json.Formatting.Indented));

            // Also write as placement_plan so Rooms / Auto-Place can reuse anchors.
            File.WriteAllText(PlacementPlanApplier.DefaultPlanPath, plan.ToString(Newtonsoft.Json.Formatting.Indented));

            var applyResult = PlacementPlanApplier.ApplyPlanDetailed(
                DefaultFloorplanPath,
                anchorAsUnions: false);

            var sb = new StringBuilder();
            sb.AppendLine($"Floorplan '{variant.Title}' applied.");
            sb.AppendLine($"Board: {variant.Board.Describe()}");
            sb.AppendLine($"Moved {applyResult.MovedCount} part(s), already placed {applyResult.AlreadyPlacedCount}, skipped {applyResult.Skipped.Count}.");

            if (drawBoardOutline)
            {
                try
                {
                    var n = DrawBoardOutline(variant.Board);
                    sb.AppendLine($"Board outline: drew {n} segment(s) on Mechanical 1.");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Board outline skipped: " + ex.Message);
                }
            }

            if (autoPlacePassivesAfter)
            {
                try
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Auto-Place passives around new IC positions ---");
                    sb.AppendLine(IcClusterRunner.RunFullAutoPlacement(
                        spacingMils: 55,
                        maxRadiusMils: 900,
                        maxSchematicDistanceMils: 2500));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Auto-Place after floorplan failed: " + ex.Message);
                    sb.AppendLine("Run Auto-Place All Components manually.");
                }
            }
            else
            {
                sb.AppendLine("Next: run Auto-Place All Components to pull passives to IC pins.");
            }

            if (applyResult.Skipped.Count > 0)
            {
                sb.AppendLine("Skipped: " + string.Join(", ", applyResult.Skipped.Take(8)));
            }

            return sb.ToString();
        }

        private static int DrawBoardOutline(BoardOutlineSpec board)
        {
            var pcbBoard = PcbDocumentHelper.EnsureProjectPcbBoard();
            if (pcbBoard == null)
                throw new InvalidOperationException("Open a PCB document first.");

            var poly = board.PolygonMils;
            if (poly == null || poly.Count < 2)
                poly = BoardOutlineSpec.FromRectangle(board.WidthMils, board.HeightMils, board.Source, board.Label).PolygonMils;

            // Origin offset: place outline near existing components' lower-left if board is tiny at 0,0
            // For simplicity, draw at absolute 0,0 in mils (user can move).
            var originX = 0.0;
            var originY = 0.0;

            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var created = 0;
            pcbBoard.NewUndo();
            pcbServer.PreProcess();
            try
            {
                // Remove previous MCP outline tracks (comment marker).
                RemoveManagedOutlineTracks(pcbBoard);

                for (var i = 0; i < poly.Count - 1; i++)
                {
                    var a = poly[i];
                    var b = poly[i + 1];
                    var track = pcbServer.PCBObjectFactory(
                        TObjectId.eTrackObject,
                        TDimensionKind.eNoDimension,
                        TObjectCreationMode.eCreate_Default) as IPCB_Track;
                    if (track == null)
                        continue;

                    track.SetState_Width(AltiumApi.MilsToCoord(10));
                    track.SetState_V7Layer(new V7_Layer(TLayerConstant.eMechanical1));
                    track.SetState_X1(AltiumApi.MilsToCoord(originX + a.X));
                    track.SetState_Y1(AltiumApi.MilsToCoord(originY + a.Y));
                    track.SetState_X2(AltiumApi.MilsToCoord(originX + b.X));
                    track.SetState_Y2(AltiumApi.MilsToCoord(originY + b.Y));
                    try
                    {
                        // Tag via comment if available
                        track.GetType().GetMethod("SetState_Comment")
                            ?.Invoke(track, new object[] { "MCP_Floorplan_Outline" });
                    }
                    catch { }

                    pcbBoard.AddPCBObject(track);
                    created++;
                }
            }
            finally
            {
                pcbServer.PostProcess();
                pcbBoard.EndUndo();
            }

            PcbDocumentHelper.RefreshBoardView(pcbBoard);
            return created;
        }

        private static void RemoveManagedOutlineTracks(IPCB_Board board)
        {
            var toRemove = new List<object>();
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            try
            {
                iterator.AddFilter_ObjectSet(new PCB.TObjectSet(PcbTObjectId.eTrackObject));
                for (var obj = iterator.FirstPCBObject(); obj != null; obj = iterator.NextPCBObject())
                {
                    try
                    {
                        var comment = obj.GetType().GetMethod("GetState_Comment")?.Invoke(obj, null) as string
                                      ?? string.Empty;
                        if (comment.IndexOf("MCP_Floorplan_Outline", StringComparison.OrdinalIgnoreCase) >= 0)
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

            foreach (var obj in toRemove)
            {
                try { board.RemovePCBObject(obj); } catch { }
            }
        }
    }
}
