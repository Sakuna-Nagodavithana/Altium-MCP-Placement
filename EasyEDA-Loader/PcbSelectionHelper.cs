using DXP;
using EDP;
using PCB;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader
{
    public static class PcbSelectionHelper
    {
        private static readonly Regex IcDesignator = new Regex(@"^(IC|U)\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string TryGetSelectedIcDesignator(IPCB_Board board)
        {
            if (board == null)
                return null;

            foreach (var component in EnumerateComponents(board))
            {
                if (!IsComponentSelected(component))
                    continue;

                var designator = ReadPcbDesignator(component);
                if (string.IsNullOrWhiteSpace(designator))
                    continue;

                if (IcDesignator.IsMatch(designator))
                    return designator.ToUpperInvariant();
            }

            return null;
        }

        public static int SelectDesignators(IPCB_Board board, IEnumerable<string> designators)
        {
            if (board == null)
                return 0;

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var designator in designators ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(designator))
                    targets.Add(designator.Trim());
            }

            var selectedCount = 0;
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            pcbServer.PreProcess();
            try
            {
                board.SelectedObjects_BeginUpdate();
                try
                {
                    board.SelectedObjects_Clear();

                    foreach (var component in EnumerateComponents(board))
                    {
                        if (component is not IPCB_Primitive primitive)
                            continue;

                        var designator = ReadPcbDesignator(component);
                        var shouldSelect = !string.IsNullOrWhiteSpace(designator) && targets.Contains(designator);
                        primitive.SetState_Selected(shouldSelect);
                        if (shouldSelect)
                        {
                            board.SelectedObjects_Add(component);
                            selectedCount++;
                        }
                    }
                }
                finally
                {
                    board.SelectedObjects_EndUpdate();
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            PcbDocumentHelper.RefreshBoardView(board);
            return selectedCount;
        }

        public static void ZoomToSelection(IPCB_Board board)
        {
            if (board == null)
                return;

            if (!TryGetSelectionBounds(board, out var x1, out var y1, out var x2, out var y2))
                return;

            var margin = AltiumApi.MilsToCoord(400);
            try
            {
                board.GraphicalView_ZoomOnRect(
                    x1 - margin,
                    y1 - margin,
                    x2 + margin,
                    y2 + margin);
                board.GraphicalView_ZoomRedraw();
            }
            catch
            {
                // Zoom is best-effort.
            }
        }

        /// <summary>
        /// Clear the current PCB selection so the user can freely click and drag
        /// individual components after an auto-place batch.
        /// </summary>
        public static void ClearSelection(IPCB_Board board)
        {
            if (board == null)
                return;
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            pcbServer?.PreProcess();
            object iteratorObj = null;
            try
            {
                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                iteratorObj = board.Internal_BoardIterator_Create();
                var iterator = (IPCB_AbstractIterator)iteratorObj;
                iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    if (obj is IPCB_Primitive prim && prim.GetState_Selected())
                    {
                        prim.SetState_Selected(false);
                    }
                    obj = iterator.NextPCBObject();
                }
            }
            catch
            {
                // Best-effort clear.
            }
            finally
            {
                if (iteratorObj != null)
                {
                    try { board.BoardIterator_Destroy(ref iteratorObj); } catch { }
                }
                try { board.SelectedObjects_EndUpdate(); } catch { }
                pcbServer?.PostProcess();
            }
        }

        private static bool TryGetSelectionBounds(
            IPCB_Board board,
            out int x1,
            out int y1,
            out int x2,
            out int y2)
        {
            x1 = y1 = x2 = y2 = 0;
            var hasBounds = false;

            foreach (var component in EnumerateComponents(board))
            {
                if (!IsComponentSelected(component))
                    continue;

                if (component is not IPCB_Primitive primitive)
                    continue;

                ICoordRect bounds;
                try
                {
                    bounds = primitive.Internal_BoundingRectangleForSelection();
                }
                catch
                {
                    continue;
                }

                if (bounds == null)
                    continue;

                var left = bounds.GetLeft();
                var bottom = bounds.GetBottom();
                var right = bounds.GetRight();
                var top = bounds.GetTop();

                if (!hasBounds)
                {
                    x1 = left;
                    y1 = bottom;
                    x2 = right;
                    y2 = top;
                    hasBounds = true;
                    continue;
                }

                x1 = Math.Min(x1, left);
                y1 = Math.Min(y1, bottom);
                x2 = Math.Max(x2, right);
                y2 = Math.Max(y2, top);
            }

            return hasBounds;
        }

        private static IEnumerable<IPCB_Component> EnumerateComponents(IPCB_Board board)
        {
            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));

            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    if (obj is IPCB_Component component)
                        yield return component;
                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }
        }

        private static bool IsComponentSelected(IPCB_Component component)
        {
            if (component is not IPCB_Primitive primitive)
                return false;

            try
            {
                return primitive.GetState_Selected();
            }
            catch
            {
                return false;
            }
        }

        private static string ReadPcbDesignator(IPCB_Component component)
        {
            var designator = component.GetState_SourceDesignator();
            if (!string.IsNullOrWhiteSpace(designator))
                return designator.Trim();

            return component.GetState_Name()?.GetState_Text()?.Trim() ?? string.Empty;
        }
    }
}
