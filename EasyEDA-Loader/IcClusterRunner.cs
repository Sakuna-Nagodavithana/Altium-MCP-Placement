using PCB;

using System;

using System.Collections.Generic;

using System.Linq;

using System.Windows.Forms;



namespace EasyEDA_Loader

{

    public sealed class IcClusterApplyResult
    {

        public string Anchor { get; set; }

        public int MovedCount { get; set; }

        public int AlreadyPlacedCount { get; set; }

        public int SelectedCount { get; set; }

        public int PlannedMoveCount { get; set; }

        public int ClusterCount { get; set; }

        public int UnionCount { get; set; }

        public List<string> MovedDesignators { get; } = new List<string>();

        public List<string> Skipped { get; } = new List<string>();

        public List<string> Anchors { get; } = new List<string>();



        public bool IsAllClusters =>

            string.Equals(Anchor, "ALL", StringComparison.OrdinalIgnoreCase) || Anchors.Count > 1;



        public string ToSummary()

        {

            string summary;

            if (IsAllClusters)

            {

                var anchorList = Anchors.Count > 0

                    ? string.Join(", ", Anchors.Take(10))

                    : "IC/U modules";

                if (Anchors.Count > 10)

                    anchorList += $" (+{Anchors.Count - 10} more)";

                summary =

                    $"Clustered {ClusterCount} module(s) [{anchorList}]: moved {MovedCount}, " +

                    $"already in place {AlreadyPlacedCount}, planned {PlannedMoveCount}. " +

                    $"Selected {SelectedCount} part(s) on the PCB.";

            }

            else

            {

                summary =

                    $"Clustered around {Anchor}: moved {MovedCount}, already in place {AlreadyPlacedCount}, " +

                    $"planned {PlannedMoveCount}. Selected {SelectedCount} part(s) on the PCB.";

            }

            if (UnionCount > 0)

                summary += $"\nAnchored {UnionCount} cluster(s) as Unions — drag any part to move the whole cluster together.";

            if (MovedCount == 0 && AlreadyPlacedCount > 0)

            {

                summary +=

                    "\nParts were already at the planned cluster positions. Selection and zoom were updated so you can review the groups.";

            }



            if (Skipped.Count > 0)

            {

                summary += $"\nSkipped: {string.Join(", ", Skipped.Take(8))}";

                if (Skipped.Count > 8)

                    summary += $" (+{Skipped.Count - 8} more)";

            }



            return summary;

        }

    }



    public static class IcClusterRunner

    {

        public static string RunOneClick(

            string fallbackAnchorDesignator,

            double spacingMils = 80.0,

            double maxRadiusMils = 900.0,

            double maxSchematicDistanceMils = 2500.0)

        {

            var previousCursor = Cursor.Current;

            Cursor.Current = Cursors.WaitCursor;

            try

            {

                var board = PcbDocumentHelper.EnsureProjectPcbBoard();

                if (board == null)

                    throw new InvalidOperationException("Open the project PCB document before clustering IC parts.");



                var anchor = ResolveAnchorDesignator(board, fallbackAnchorDesignator);



                PlacementPlanGenerator.Generate(anchor, spacingMils, maxRadiusMils, maxSchematicDistanceMils);



                return ApplyLatestPlanAndSelect();

            }

            finally

            {

                Cursor.Current = previousCursor;

            }

        }



        public static string RunAllClusters(
            double spacingMils = 80.0,
            double maxRadiusMils = 900.0,
            double maxSchematicDistanceMils = 2500.0)
        {
            // Same path as the MCP panel button — unlock + resize, no mass-select.
            return RunFullAutoPlacement(spacingMils, maxRadiusMils, maxSchematicDistanceMils);
        }

        /// <summary>
        /// Global two-phase board optimizer: force-directed (HPWL) then simulated
        /// annealing with 90° rotations. Locks connectors/ICs by default.
        /// </summary>
        public static string RunForceDirectedOptimize(
            int iterations = 220,
            double spacingMils = ForceDirectedOptimizer.DefaultSpacingMils,
            double gridMils = 10.0,
            bool lockIcs = true)
        {
            var previousCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                var board = PcbDocumentHelper.EnsureProjectPcbBoard();
                if (board == null)
                    throw new InvalidOperationException("Open the project PCB document before optimizing.");

                var generateMsg = PlacementPlanGenerator.GenerateForceDirected(
                    iterations, spacingMils, gridMils, lockIcs);

                var applyResult = PlacementPlanApplier.ApplyPlanDetailed(anchorAsUnions: false);
                try { PcbSelectionHelper.ClearSelection(board); } catch { }
                try { PlacementPlanApplier.UnlockAllComponents(board); } catch { }
                PcbDocumentHelper.RefreshBoardView(board);

                return generateMsg + "\n" + applyResult.ToSummary();
            }
            finally
            {
                Cursor.Current = previousCursor;
            }
        }



        /// <summary>
        /// One-click full-auto placement: generate the plan for all ICs, apply it
        /// (including bottom-side flips for decoupling caps), then resize
        /// designators to a uniform small size. Components remain independent and
        /// moveable; use the explicit Anchor Clusters button when rigid Union
        /// movement is desired.
        /// </summary>
        public static string RunFullAutoPlacement(
            double spacingMils = 55.0,
            double maxRadiusMils = 900.0,
            double maxSchematicDistanceMils = 2500.0,
            double designatorHeightMils = 20.0,
            double designatorWidthMils = 5.0)
        {
            var previousCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                var board = PcbDocumentHelper.EnsureProjectPcbBoard();
                if (board == null)
                    throw new InvalidOperationException("Open the project PCB document before auto-placing.");

                PlacementPlanGenerator.GenerateAll(spacingMils, maxRadiusMils, maxSchematicDistanceMils);

                // Apply directly without auto-selecting -- selecting all placed parts
                // afterward prevented the user from moving individual components.
                var applyResult = PlacementPlanApplier.ApplyPlanDetailed(
                    anchorAsUnions: false);
                applyResult.Anchors.AddRange(PlacementPlanApplier.GetLastPlannedAnchors());
                applyResult.ClusterCount = applyResult.Anchors.Count;

                // Clear any selection so the user can freely click + drag parts.
                try { PcbSelectionHelper.ClearSelection(board); } catch { }
                // Unlock all components -- some Altium operations inadvertently lock parts.
                try { PlacementPlanApplier.UnlockAllComponents(board); } catch { }
                PcbDocumentHelper.RefreshBoardView(board);

                var summary = applyResult.ToSummary();

                try
                {
                    var resizeMsg = PlacementPlanApplier.ResizeAllDesignators(designatorHeightMils, designatorWidthMils);
                    summary += "\n" + resizeMsg;
                }
                catch (Exception ex)
                {
                    summary += $"\nDesignator resize skipped: {ex.Message}";
                }

                // Professional follow-ups: rooms (confinement) + optional unions for cluster drag.
                try
                {
                    summary += "\n" + PlacementRooms.CreateRoomsFromLastPlan(alsoAnchorUnions: true);
                }
                catch (Exception ex)
                {
                    summary += "\nRooms skipped: " + ex.Message;
                }

                return summary;
            }
            finally
            {
                Cursor.Current = previousCursor;
            }
        }



        private static string ApplyLatestPlanAndSelect()

        {

            var board = PcbDocumentHelper.EnsureProjectPcbBoard();

            if (board == null)

                throw new InvalidOperationException("Could not reload the PCB after generating the placement plan.");



            var applyResult = PlacementPlanApplier.ApplyPlanDetailed(
                anchorAsUnions: false);

            applyResult.Anchors.AddRange(PlacementPlanApplier.GetLastPlannedAnchors());

            applyResult.ClusterCount = applyResult.Anchors.Count;



            var selectTargets = new List<string>(PlacementPlanApplier.GetLastPlannedDesignators());

            foreach (var anchor in applyResult.Anchors)

            {

                if (!selectTargets.Contains(anchor, StringComparer.OrdinalIgnoreCase))

                    selectTargets.Add(anchor);

            }



            if (!string.IsNullOrWhiteSpace(applyResult.Anchor) &&

                !string.Equals(applyResult.Anchor, "ALL", StringComparison.OrdinalIgnoreCase) &&

                !selectTargets.Contains(applyResult.Anchor, StringComparer.OrdinalIgnoreCase))

            {

                selectTargets.Add(applyResult.Anchor);

            }



            applyResult.SelectedCount = PcbSelectionHelper.SelectDesignators(board, selectTargets);

            PcbSelectionHelper.ZoomToSelection(board);
            PcbSelectionHelper.ClearSelection(board);
            PlacementPlanApplier.UnlockAllComponents(board);

            PcbDocumentHelper.RefreshBoardView(board);



            return applyResult.ToSummary();

        }



        public static void RunOneClickAfterPanelClosed(

            string fallbackAnchorDesignator,

            double spacingMils,

            double maxRadiusMils,

            double maxSchematicDistanceMils)

        {

            PcbDocumentHelper.PumpUi();



            try

            {

                var summary = RunOneClick(

                    fallbackAnchorDesignator,

                    spacingMils,

                    maxRadiusMils,

                    maxSchematicDistanceMils);



                MessageBox.Show(

                    summary,

                    "Altium MCP Placement - IC Cluster",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Information);

            }

            catch (Exception ex)

            {

                MessageBox.Show(

                    ex.Message,

                    "Altium MCP Placement - IC Cluster",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Error);

            }

        }



        public static void RunAllClustersAfterPanelClosed(

            double spacingMils,

            double maxRadiusMils,

            double maxSchematicDistanceMils)

        {

            PcbDocumentHelper.PumpUi();



            try

            {

                var summary = RunAllClusters(spacingMils, maxRadiusMils, maxSchematicDistanceMils);



                MessageBox.Show(

                    summary,

                    "Altium MCP Placement - All ICs",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Information);

            }

            catch (Exception ex)

            {

                MessageBox.Show(

                    ex.Message,

                    "Altium MCP Placement - All ICs",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Error);

            }

        }



        private static string ResolveAnchorDesignator(IPCB_Board board, string fallbackAnchorDesignator)

        {

            var fromText = (fallbackAnchorDesignator ?? string.Empty).Trim().ToUpperInvariant();

            if (!string.IsNullOrWhiteSpace(fromText))

                return fromText;



            var fromSelection = PcbSelectionHelper.TryGetSelectedIcDesignator(board);

            if (!string.IsNullOrWhiteSpace(fromSelection))

                return fromSelection;



            throw new InvalidOperationException(

                "Enter an anchor designator (e.g. IC1) or select an IC/U part on the PCB first.");

        }

    }

}


