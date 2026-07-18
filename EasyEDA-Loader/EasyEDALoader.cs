using DXP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class EasyEDALoaderModule : ServerModule
    {
        private bool noGUIMode;

        public EasyEDALoaderModule(IClient argClient)
          : base(argClient, "Altium-MCP-Placement")
        {
            noGUIMode = argClient.ProductInfo().SupportsUIFeature("NoGUI", false);
        }

        protected override IServerDocument NewDocumentInstance(string argKind, string argFileName) => (IServerDocument)null;

        protected override void InitializeCommands()
        {
            RegisterCommand("EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDABomBuilder", new CommandProc(Run));
            RegisterCommand("EasyEDAExportConnectivity", new CommandProc(ExportConnectivity));
            RegisterCommand("EasyEDAMcpPanel", new CommandProc(OpenMcpPanel));
            RegisterCommand("EasyEDAApplyPlacementPlan", new CommandProc(ApplyPlacementPlan));
            RegisterCommand("EasyEDAClusterIcParts", new CommandProc(ClusterIcParts));
            RegisterCommand("EasyEDAClusterAllIcParts", new CommandProc(ClusterAllIcParts));
            RegisterCommand("EasyEDASetupPcbRules", new CommandProc(SetupPcbRules));
            RegisterCommand("EasyEDARunClearanceDrc", new CommandProc(RunClearanceDrc));
            RegisterCommand("EasyEDAViaStitch", new CommandProc(ViaStitchRfClocks));
            RegisterCommand("EasyEDADesignWorkflow", new CommandProc(OpenDesignWorkflow));
            RegisterCommand("EasyEDAStackupAdvisor", new CommandProc(OpenStackupAdvisor));
            RegisterCommand("EasyEDARoutePriority", new CommandProc(OpenRoutePriority));
            RegisterCommand("EasyEDACreateRooms", new CommandProc(CreatePlacementRooms));
            RegisterCommand("EasyEDAFanoutDecap", new CommandProc(FanoutDecapVias));
            RegisterCommand("EasyEDAFloorplanPreview", new CommandProc(OpenFloorplanPreview));
            RegisterCommand("EasyEDABoardNeeds", new CommandProc(OpenBoardNeeds));
        }

        private void RegisterCommand(string argCommandId, CommandProc commandProc) => ((DXP.CommandLauncher)CommandLauncher).RegisterCommand(argCommandId, (CommandProc)((IServerDocumentView view, ref string parameters) =>
        {
            try
            {
                commandProc(view, ref parameters);
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                {
                    throw;
                }
                else
                {
                    int num = (int)MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
        }));

        private static void PlaceComponent(string schLibraryPath, string partName)
        {
            var currentSheet = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
            if (currentSheet == null)
                throw new InvalidOperationException("Must be in a schematic document before placing a component.");

            var newComponent = AltiumApi.GlobalVars.SCHServer.LoadComponentFromLibrary(partName, schLibraryPath);
            currentSheet.AddSchObject(newComponent);
            newComponent.MoveToXY(0, 0);
            newComponent.SetState_Orientation(TRotationBy90.eRotate0);
            currentSheet.GraphicallyInvalidate();
        }

        /// <summary>
        /// True for EasyEDA API parameter keys that carry the LCSC C... part number
        /// (e.g. "LCSC Part", "Supplier Part"). These get renamed to "JLCPCB Part"
        /// when stamped onto the Altium component.
        /// </summary>
        private static bool IsLcscPartKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            return string.Equals(key, "LCSC Part", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "LCSC Part #", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "Supplier Part", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "LCSC", StringComparison.OrdinalIgnoreCase);
        }

        private void ExportConnectivity(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                string outputPath = DesignExporter.ExportFullProject();
                if (!noGUIMode)
                {
                    var message = $"Project data exported for MCP:\n{outputPath}";
                    try
                    {
                        if (System.IO.File.Exists(PcbFullDrc.DefaultReportPath))
                        {
                            var reportJson = System.IO.File.ReadAllText(PcbFullDrc.DefaultReportPath);
                            var report = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(reportJson);
                            if (report != null)
                                message += "\n\n" + PcbFullDrc.FormatUserMessage(report);
                        }
                        else if (System.IO.File.Exists(PcbClearanceDrc.DefaultReportPath))
                        {
                            var reportJson = System.IO.File.ReadAllText(PcbClearanceDrc.DefaultReportPath);
                            var report = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(reportJson);
                            if (report != null)
                                message += "\n\n" + PcbClearanceDrc.FormatUserMessage(report);
                        }
                    }
                    catch { }

                    MessageBox.Show(
                        message,
                        "Altium MCP Placement",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void RunClearanceDrc(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                if (noGUIMode)
                {
                    PcbFullDrc.RunFullCheck(runAltiumBatch: true);
                    return;
                }

                PcbFullDrc.RunAndShowResults();
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void OpenDesignWorkflow(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new DesignWorkflowWindow();
            window.Show();
        }

        private void OpenStackupAdvisor(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new StackupAdvisorWindow();
            window.Show();
        }

        private void OpenRoutePriority(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new RoutingPriorityWindow();
            window.Show();
        }

        private void CreatePlacementRooms(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var message = PlacementRooms.CreateRoomsFromLastPlan(alsoAnchorUnions: true);
            if (!noGUIMode)
                MessageBox.Show(message, "Altium MCP Rooms", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void FanoutDecapVias(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var message = DecapFanout.FanoutDecouplingAndPowerPads();
            if (!noGUIMode)
                MessageBox.Show(message, "Altium MCP Fanout", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenFloorplanPreview(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new FloorplanPreviewWindow();
            window.Show();
        }

        private void OpenBoardNeeds(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new BoardNeedsWindow();
            window.Show();
        }

        private void ViaStitchRfClocks(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                var message = ViaStitcher.StitchRfAndClocks();
                if (!noGUIMode)
                {
                    MessageBox.Show(
                        message,
                        "Altium MCP Via Stitch",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void OpenMcpPanel(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            var window = new McpControlWindow();
            window.ShowDialog();
        }

        private void ClusterAllIcParts(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                // Same full workflow as the MCP panel "Auto-Place All Components" button.
                var message = IcClusterRunner.RunFullAutoPlacement();
                if (!noGUIMode)
                {
                    MessageBox.Show(
                        message,
                        "Altium MCP Placement",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void ClusterIcParts(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                var anchor = "IC1";
                if (!string.IsNullOrWhiteSpace(argParameters))
                    anchor = argParameters.Trim();

                var message = IcClusterRunner.RunOneClick(anchor);
                if (!noGUIMode)
                {
                    MessageBox.Show(
                        message,
                        "Altium MCP Placement",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void SetupPcbRules(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                var classified = PcbDesignRulesSetup.Classify(useConnectivityHints: true);

                var preview = new NetClassPreviewWindow(
                    classified.NetClassAssignments,
                    PcbDesignRulesSetup.ManagedNetClassOrder);
                if (preview.ShowDialog() != true || preview.ResultAssignments == null)
                    return;

                var result = PcbDesignRulesSetup.Apply(preview.ResultAssignments);
                if (!noGUIMode)
                {
                    MessageBox.Show(
                        result.Summary,
                        "Altium MCP Placement - PCB Rules",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void ApplyPlacementPlan(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            try
            {
                var message = PlacementPlanApplier.ApplyPlan();
                if (!noGUIMode)
                {
                    MessageBox.Show(
                        message,
                        "Altium MCP Placement",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (noGUIMode)
                    throw;

                MessageBox.Show(ex.Message, "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        private void Run(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            // Diagnostic: wrap each phase in its own try/catch so the error message
            // shows exactly which step threw, with the full stack trace.
            Dialog dialog = null;
            try
            {
                dialog = new Dialog();
            }
            catch (Exception ex)
            {
                if (noGUIMode) throw;
                MessageBox.Show($"Failed to create the EasyEDA dialog:\n\n{ex}\n\nType: {ex.GetType().Name}",
                    "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            DialogResult result;
            try
            {
                result = dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                if (noGUIMode) throw;
                MessageBox.Show($"Failed to show the EasyEDA dialog:\n\n{ex}\n\nType: {ex.GetType().Name}",
                    "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            if (result != DialogResult.OK || dialog.SelectedComponents == null || dialog.SelectedComponents.Count == 0)
                return;

            var client = AltiumApi.GlobalVars.Client;
            if (client == null)
            {
                MessageBox.Show("Altium Client is not available.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            var currentView = client.GetCurrentView();
            if (currentView == null)
            {
                MessageBox.Show("Open a schematic document before using the EasyEDA Component Loader.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            var currentDoc = currentView.GetOwnerDocument();
            if (currentDoc == null)
            {
                MessageBox.Show("Must be in a schematic document before running", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            var ctx = new CancellationTokenSource();
            var api = new EasyedaApi();

            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string libraryPath = Path.Combine(documentsPath, "AltiumEE");
            Directory.CreateDirectory(libraryPath);
            string pcbLibraryPath = Path.Combine(libraryPath, "EasyEDA.pcblib");
            string schLibraryPath = Path.Combine(libraryPath, "EasyEDA.schlib");

            var pcbDocument = client.OpenDocument("PcbLib", pcbLibraryPath);
            if (pcbDocument == null)
            {
                MessageBox.Show($"Could not open the PCB library:\n{pcbLibraryPath}", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
            client.ShowDocument(pcbDocument);
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null)
            {
                MessageBox.Show("Could not get the PCB library editor.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            var schDocument = client.OpenDocument("SchLib", schLibraryPath);
            if (schDocument == null)
            {
                MessageBox.Show($"Could not open the schematic library:\n{schLibraryPath}", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
            client.ShowDocument(schDocument);
            var schLib = EESCH.GetCurrentSchLibrary();
            if (schLib == null)
            {
                MessageBox.Show("Could not get the schematic library editor.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }

            // Build a lookup of BOM-resolved annotations keyed by LCSC number so that
            // when a component is created from the BOM Builder flow, the JLCPCB part
            // number, chosen package, and Basic/Extended type are stamped as schematic
            // parameters (visible in the component's properties / BOM annotation).
            var bomAnnotations = new Dictionary<string, List<(string Name, string Value)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var bp in dialog.BomResolvedParts)
            {
                if (string.IsNullOrWhiteSpace(bp.Lcsc))
                    continue;
                // Normalise so the parameter always reads the bare LCSC C... number,
                // e.g. "C25744" rather than "LCSC C25744" or " C25744 ".
                string lcsc = System.Text.RegularExpressions.Regex.Match(bp.Lcsc, @"C\d{3,9}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
                if (string.IsNullOrEmpty(lcsc))
                    lcsc = bp.Lcsc.Trim();
                var list = new List<(string, string)>
                {
                    ("JLCPCB Part", lcsc),
                    ("JLCPCB Package", bp.Package ?? ""),
                    ("JLCPCB Type", bp.IsBasic ? "Basic" : "Extended"),
                };
                if (!string.IsNullOrWhiteSpace(bp.Value))
                    list.Add(("Value", bp.Value));
                bomAnnotations[bp.Lcsc] = list;
            }

            // Process each selected component
            foreach (var selection in dialog.SelectedComponents)
            {
                try
                {
                    var root = selection?.Root;
                    if (root?.Component == null)
                    {
                        MessageBox.Show($"Part '{selection?.PartInfo?.Name ?? "?"}' has no component data (Root or Component is null).", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        continue;
                    }
                    var ee_symbol = root.Component.Symbol;
                    var ee_footprint = root.Component.PackageDetail?.Footprint;
                    if (ee_symbol?.Head?.Parameters == null)
                    {
                        MessageBox.Show($"Part '{selection?.PartInfo?.Name ?? "?"}' has no schematic symbol data.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        continue;
                    }
                    if (ee_footprint?.Head?.Parameters == null)
                    {
                        MessageBox.Show($"Part '{selection?.PartInfo?.Name ?? "?"}' has no footprint data.", "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        continue;
                    }
                    var owner_id = root.Component.Owner?.Uuid;
                    string package = ee_footprint.Head.Parameters.Package;
                    EeFootprint3dModel model = selection.Include3dModel ? ee_footprint.GetModel() : null;

                    // Prefetch model if we can
                    Task<byte[]> modelTask = model != null ? Task.Run(() => api.LoadModelAsync(model.Uuid, ctx.Token)) : null;
                    Task<byte[]> rawModelTask = model != null ? Task.Run(() => api.LoadRawModelAsync(model.Uuid, ctx.Token)) : null;

                    // Get product info (use cached from search if available)
                    EasyedaApi.ProductInfo productInfo = selection.PartInfo?.Info;

                    // Create PCB footprint if requested
                    if (selection.IncludeFootprint)
                    {
                        AltiumApi.GlobalVars.Client.ShowDocument(pcbDocument);
                        var libComp = pcbLib.GetComponentByName(package);
                        bool createdFootprint = false;
                        if (libComp == null)
                        {
                            libComp = EEPCB.CreateFootprintInLib(package, root.Component.PackageDetail.Title);
                            createdFootprint = libComp != null;
                        }

                        if (createdFootprint)
                        {
                            AltiumApi.GlobalVars.PCBServer.PreProcess();
                            try
                            {
                                var footprintContext = new EeFootprintContext
                                {
                                    Box = ee_footprint.BoundingBox,
                                    Layers = ee_footprint.Layers,
                                    CancelToken = ctx.Token,
                                    Exception = (Exception ex) => true,
                                    ModelTask = modelTask,
                                    RawModelTask = rawModelTask,
                                };
                                ee_footprint.AddToComponent(libComp, footprintContext);
                            }
                            finally
                            {
                                AltiumApi.GlobalVars.PCBServer.PostProcess();
                            }
                            pcbDocument.DoFileSave("PcbLib");
                        }
                    }

                    // Create schematic symbol
                    string partName = ee_symbol.Head.Parameters.Name;
                    string description = productInfo?.Description ?? partName;

                    // Always rebuild the schematic symbol. Skipping when the part already
                    // exists left broken EasyEDA thin-line caps/resistors stuck in EasyEDA.schlib.
                    var existingComponent = schLib.GetState_SchComponentByLibRef(partName);
                    if (existingComponent != null)
                    {
                        if (!EESCH.TryRemoveComponent(schLib, existingComponent))
                        {
                            MessageBox.Show(
                                $"Could not replace existing library symbol '{partName}'.\n" +
                                "Delete it manually from EasyEDA.schlib, then re-import.",
                                "Altium MCP Placement",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        else
                        {
                            schDocument.DoFileSave("SchLib");
                        }
                    }

                    var component = EESCH.CreateComponent(partName, description, ee_symbol.Head.Parameters.Pre);
                    if (schLib != null && component != null)
                    {
                        AltiumApi.GlobalVars.PCBServer.PreProcess();
                        try
                        {
                            SymbolDrawing.CreateComponent(
                                schLib,
                                component,
                                pcbLibraryPath,
                                package,
                                ee_symbol);

                            // Stamp the EasyEDA API parameters onto the component,
                            // renaming its LCSC-number field to JLCPCB Part.
                            bool stampedJlcpcbPart = false;
                            if (productInfo?.Parameters != null)
                            {
                                foreach (var kvp in productInfo.Parameters)
                                {
                                    if (IsLcscPartKey(kvp.Key))
                                    {
                                        EESCH.AddParameter(
                                            component,
                                            "JLCPCB Part",
                                            kvp.Value,
                                            visible: true,
                                            showName: false);
                                        stampedJlcpcbPart = true;
                                        continue;
                                    }
                                    EESCH.AddParameter(component, kvp.Key, kvp.Value);
                                }
                            }

                            if (!stampedJlcpcbPart)
                            {
                                string singlePartLcsc =
                                    selection.PartInfo?.Part ??
                                    selection.PartInfo?.Name ??
                                    "";
                                if (!string.IsNullOrWhiteSpace(singlePartLcsc))
                                {
                                    var match =
                                        System.Text.RegularExpressions.Regex.Match(
                                            singlePartLcsc,
                                            @"C\d{3,9}",
                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (match.Success)
                                        singlePartLcsc = match.Value;
                                    EESCH.AddParameter(
                                        component,
                                        "JLCPCB Part",
                                        singlePartLcsc,
                                        visible: true,
                                        showName: false);
                                }
                            }

                            if (bomAnnotations.TryGetValue(
                                    selection.PartInfo?.Part ?? "",
                                    out var annotations))
                            {
                                foreach (var annotation in annotations)
                                {
                                    bool isVisible = string.Equals(
                                        annotation.Name,
                                        "JLCPCB Part",
                                        StringComparison.OrdinalIgnoreCase);
                                    EESCH.AddParameter(
                                        component,
                                        annotation.Name,
                                        annotation.Value,
                                        visible: isVisible,
                                        showName: false);
                                }
                            }
                        }
                        finally
                        {
                            AltiumApi.GlobalVars.PCBServer.PostProcess();
                        }
                        schLib.SetState_Current_SchComponent(component);
                        schLib.GraphicallyInvalidate();
                        schDocument.DoFileSave("SchLib");
                    }

                    // Place component in schematic if requested (only the last one)
                    if (dialog.PlaceInSchematic && selection == dialog.SelectedComponents[dialog.SelectedComponents.Count - 1])
                    {
                        // Return to the original document before placing
                        AltiumApi.GlobalVars.Client.ShowDocument(currentDoc);
                        PlaceComponent(schLibraryPath, partName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to process component {selection?.PartInfo?.Name ?? "?"}:\n\n{ex}\n\nType: {ex.GetType().Name}",
                        "Altium MCP Placement Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }

            // Return to the original document we started in
            AltiumApi.GlobalVars.Client.ShowDocument(currentDoc);

            // Close the library documents if requested
            if (dialog.CloseDocuments)
            {
                AltiumApi.GlobalVars.Client.CloseDocument(pcbDocument);
                AltiumApi.GlobalVars.Client.CloseDocument(schDocument);
            }
        }
    }
}
