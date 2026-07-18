using DXP;
using EDP;
using PCB;
using SCH;
using SchTObjectId = SCH.TObjectId;
using SchTObjectSet = SCH.TObjectSet;
using PcbTObjectId = PCB.TObjectId;
using PcbTObjectSet = PCB.TObjectSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace EasyEDA_Loader
{
    public static class DesignExporter
    {
        public static string DefaultExportPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE",
                "connectivity.json");


        public static string ExportFullProject(string outputPath = null) =>
            ExportProject(outputPath, includePcbRoutingDetails: true);

        /// <summary>
        /// Faster export for placement/cluster workflows: schematic + PCB parts only (no track/plane scan).
        /// </summary>
        public static string ExportForPlacementPlanning(string outputPath = null) =>
            ExportProject(outputPath, includePcbRoutingDetails: false);

        public static bool IsConnectivityExportFresh(string outputPath = null)
        {
            outputPath ??= DefaultExportPath;
            if (!File.Exists(outputPath))
                return false;

            try
            {
                var project = AltiumApi.GlobalVars.Workspace?.Internal_DM_FocusedProject() as IProject;
                var projectPath = project?.DM_ProjectFileName();
                if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
                    return false;

                return File.GetLastWriteTimeUtc(projectPath) <= File.GetLastWriteTimeUtc(outputPath).AddSeconds(1);
            }
            catch
            {
                return false;
            }
        }

        private static string ExportProject(string outputPath, bool includePcbRoutingDetails)
        {
            outputPath ??= DefaultExportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var workspace = AltiumApi.GlobalVars.Workspace;
            if (workspace == null)
                throw new InvalidOperationException("Altium workspace is not available.");

            var project = workspace.Internal_DM_FocusedProject() as IProject;
            if (project == null)
                throw new InvalidOperationException("Open a project in Altium before exporting design data.");

            project.DM_Compile();
            PcbDocumentHelper.PumpUi();

            var schematicSheets = new List<Dictionary<string, object>>();
            foreach (var document in EnumerateLogicalDocuments(project))
            {
                if (!IsSchematicDocument(document))
                    continue;

                var schDoc = AltiumApi.GlobalVars.SCHServer.Internal_GetSchDocumentByPath(document.DM_FullPath()) as ISch_Document;
                if (schDoc == null)
                    schDoc = AltiumApi.GlobalVars.SCHServer.Internal_LoadSchDocumentByPath(document.DM_FullPath()) as ISch_Document;

                if (schDoc != null)
                {
                    schematicSheets.Add(ExportSchematicSheet(schDoc, document, document.DM_FullPath()));
                    PcbDocumentHelper.PumpUi();
                }
            }

            if (schematicSheets.Count == 0)
            {
                var current = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
                if (current != null && current.GetState_ObjectId() != SchTObjectId.eSchLib)
                    schematicSheets.Add(ExportSchematicSheet(current, null, SafeText(current.GetState_DocumentName())));
            }

            Dictionary<string, object> pcbData = null;
            foreach (var document in EnumeratePhysicalDocuments(project))
            {
                if (!IsPcbDocument(document))
                    continue;

                var board = AltiumApi.GlobalVars.PCBServer.Internal_GetPCBBoardByPath(document.DM_FullPath()) as IPCB_Board;
                if (board == null)
                    board = AltiumApi.GlobalVars.PCBServer.Internal_LoadPCBBoardByPath(document.DM_FullPath()) as IPCB_Board;

                if (board != null)
                {
                    pcbData = ExportPcbBoard(board, document.DM_FullPath(), includePcbRoutingDetails);
                    break;
                }
            }

            if (pcbData == null)
            {
                var focusedBoard = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBBoard();
                if (focusedBoard != null)
                    pcbData = ExportPcbBoard(focusedBoard, SafeText(focusedBoard.GetState_FileName()), includePcbRoutingDetails);
            }

            var projectNets = BuildProjectNets(schematicSheets);
            var payload = BuildPayload(project, schematicSheets, pcbData, projectNets);

            // Full MCP DRC (Altium batch + geometric extras) before fab / MCP handoff.
            if (includePcbRoutingDetails)
            {
                try
                {
                    var boardForDrc = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBBoard()
                        ?? PcbDocumentHelper.ResolveProjectPcbBoard();
                    if (boardForDrc != null)
                    {
                        var drc = PcbFullDrc.RunFullCheck(runAltiumBatch: true);
                        drc.Remove("_issues");
                        payload["mcpDrc"] = drc;
                        if (pcbData != null)
                            pcbData["mcpDrc"] = drc;
                    }
                }
                catch
                {
                    // Export still succeeds even if DRC fails.
                }
            }

            WriteJson(outputPath, payload);
            return outputPath;
        }

        public static string ExportCurrentSheet(string outputPath = null) =>
            ExportFullProject(outputPath);

        private static Dictionary<string, object> BuildPayload(
            IProject project,
            List<Dictionary<string, object>> schematicSheets,
            Dictionary<string, object> pcbData,
            List<Dictionary<string, object>> projectNets)
        {
            var projectPath = SafeText(project.DM_ProjectFileName());
            var projectName = string.IsNullOrWhiteSpace(projectPath)
                ? "Active Project"
                : Path.GetFileNameWithoutExtension(projectPath);

            var flatComponents = schematicSheets
                .SelectMany(sheet => (sheet["components"] as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>())
                    .Select(c =>
                    {
                        var copy = new Dictionary<string, object>(c);
                        copy["sheet"] = sheet["sheet"];
                        return copy;
                    }))
                .ToList();

            var flatNets = schematicSheets
                .SelectMany(sheet => (sheet["nets"] as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>())
                    .Select(n =>
                    {
                        var copy = new Dictionary<string, object>((Dictionary<string, object>)n);
                        copy["sheet"] = sheet["sheet"];
                        return copy;
                    }))
                .ToList();

            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 5.2,
                ["exportFeatures"] = new List<string>
                {
                    "pcbPadCoordinates",
                    "pcbPadLayerRotation",
                    "pcbKeepoutRegions",
                    "mcpClearanceDrc",
                },
                ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                ["project"] = new Dictionary<string, object>
                {
                    ["name"] = projectName,
                    ["path"] = projectPath,
                },
                ["schematics"] = schematicSheets,
                ["pcb"] = pcbData,
                ["components"] = flatComponents,
                ["nets"] = flatNets,
                ["projectNets"] = projectNets,
                ["ercViolations"] = schematicSheets
                    .SelectMany(s => s["ercViolations"] as List<Dictionary<string, string>> ?? new List<Dictionary<string, string>>())
                    .ToList(),
                ["summary"] = new Dictionary<string, object>
                {
                    ["sheetCount"] = schematicSheets.Count,
                    ["schComponentCount"] = flatComponents.Count,
                    ["schNetCount"] = flatNets.Count,
                    ["projectNetCount"] = projectNets.Count,
                    ["pcbComponentCount"] = pcbData?["components"] is List<Dictionary<string, object>> pcbComponents ? pcbComponents.Count : 0,
                    ["pcbNetCount"] = pcbData?["nets"] is List<Dictionary<string, object>> pcbNets ? pcbNets.Count : 0,
                    ["pcbTrackCount"] = pcbData?["routing"] is Dictionary<string, object> routing && routing.TryGetValue("trackCount", out var trackCount) ? trackCount : 0,
                    ["pcbViaCount"] = pcbData?["routing"] is Dictionary<string, object> routing2 && routing2.TryGetValue("viaCount", out var viaCount) ? viaCount : 0,
                    ["pcbPlaneCount"] = pcbData?["planes"] is Dictionary<string, object> planes && planes.TryGetValue("polygonCount", out var planeCount) ? planeCount : 0,
                },
            };
        }

        private static Dictionary<string, object> ExportSchematicSheet(
            ISch_Document schDoc,
            IDocument logicalDoc,
            string fullPath)
        {
            var components = new List<Dictionary<string, object>>();
            var netMap = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var pinNetLookup = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            var netAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectNetNameAliases(schDoc, netAliases);

            if (logicalDoc != null)
                ExportCompiledNets(logicalDoc, netMap, pinNetLookup);

            ApplyNetAliases(netMap, pinNetLookup, netAliases);

            var componentIterator = schDoc.SchIterator_Create();
            componentIterator.AddFilter_ObjectSet(new SchTObjectSet(SchTObjectId.eSchComponent));

            try
            {
                var component = componentIterator.FirstSchObject() as ISch_Component;
                while (component != null)
                {
                    if (component.InSheet())
                    {
                        var designator = SafeText(component.GetState_SchDesignator()?.GetState_Text());
                        if (!string.IsNullOrWhiteSpace(designator))
                        {
                            components.Add(new Dictionary<string, object>
                            {
                                ["designator"] = designator,
                                ["comment"] = SafeText(component.GetState_SchComment()?.GetState_Text()),
                                ["jlcpcb"] = ExtractJlcpcbPartNo(component),
                                ["placement"] = ReadSchematicPlacement(component),
                                ["pins"] = ReadPins(component, designator, pinNetLookup),
                            });
                        }
                    }

                    component = componentIterator.NextSchObject() as ISch_Component;
                }
            }
            finally
            {
                schDoc.SchIterator_Destroy(ref componentIterator);
            }

            return new Dictionary<string, object>
            {
                ["sheet"] = SafeText(schDoc.GetState_DocumentName()),
                ["path"] = SafeText(fullPath),
                ["components"] = components.OrderBy(c => c["designator"]?.ToString(), StringComparer.OrdinalIgnoreCase).ToList(),
                ["nets"] = BuildNetList(netMap),
                ["ercViolations"] = ReadErcViolations(schDoc),
            };
        }

        private static void ExportCompiledNets(
            IDocument document,
            Dictionary<string, List<Dictionary<string, string>>> netMap,
            Dictionary<string, Dictionary<string, string>> pinNetLookup)
        {
            for (var netIndex = 0; netIndex < document.DM_NetCount(); netIndex++)
            {
                var net = document.Internal_DM_Nets(netIndex) as INet;
                if (net == null)
                    continue;

                var netName = ResolveCompiledNetName(net);
                if (string.IsNullOrWhiteSpace(netName))
                    continue;

                if (!netMap.TryGetValue(netName, out var connections))
                {
                    connections = new List<Dictionary<string, string>>();
                    netMap[netName] = connections;
                }

                for (var pinIndex = 0; pinIndex < net.DM_PinCount(); pinIndex++)
                {
                    var item = net.Internal_DM_Pins(pinIndex) as INetItem;
                    if (item == null)
                        continue;

                    var designator = SafeText(item.DM_LogicalPartDesignator());
                    if (string.IsNullOrWhiteSpace(designator))
                        designator = SafeText(item.DM_FullLogicalPartDesignator());
                    var pinNumber = SafeText(item.DM_PinNumber());
                    if (string.IsNullOrWhiteSpace(designator))
                        continue;

                    connections.Add(new Dictionary<string, string>
                    {
                        ["designator"] = designator,
                        ["pin"] = pinNumber,
                    });

                    if (!pinNetLookup.TryGetValue(designator, out var pinMap))
                    {
                        pinMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        pinNetLookup[designator] = pinMap;
                    }

                    pinMap[pinNumber] = netName;
                }
            }
        }

        private static string ExtractJlcpcbPartNo(ISch_Component component)
        {
            var parameters = ReadParameters(component);
            foreach (var key in new[]
            {
                "JLCPCB Part",
                "JLCPCB Part #",
                "JLCPCB Part No",
                "JLCPCB Part No.",
                "LCSC Part",
            })
            {
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }

            foreach (var kvp in parameters)
            {
                if (kvp.Key.IndexOf("JLCPCB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    return kvp.Value;
                }
            }

            return string.Empty;
        }

        private static Dictionary<string, object> ExportPcbBoard(IPCB_Board board, string fullPath, bool includeRoutingDetails = true)
        {
            var components = new List<Dictionary<string, object>>();
            var netMap = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.eComponentObject));
            try
            {
                var obj = iterator.FirstPCBObject();
                while (obj != null)
                {
                    var component = obj as IPCB_Component;
                    if (component != null)
                    {
                        var designator = SafeText(component.GetState_SourceDesignator());
                        if (!string.IsNullOrWhiteSpace(designator))
                        {
                            var pins = ReadPcbComponentPads(component, designator, netMap);
                            var placement = ReadPcbPlacement(component);
                            components.Add(new Dictionary<string, object>
                            {
                                ["designator"] = designator,
                                ["pattern"] = SafeText(component.GetState_Pattern()),
                                ["description"] = SafeText(component.GetState_SourceDescription()),
                                ["layer"] = component.GetState_FlippedOnLayer() ? "Bottom" : "Top",
                                ["placement"] = placement,
                                ["bboxMils"] = ReadComponentBoundingBox(component, placement),
                                ["pins"] = pins,
                            });
                        }
                    }

                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }

            Dictionary<string, object> routing;
            Dictionary<string, object> planes;
            Dictionary<string, object> stackup;
            Dictionary<string, object> validation;
            Dictionary<string, object> keepouts;
            if (includeRoutingDetails)
            {
                routing = ExportPcbRouting(board, 4000);
                planes = ExportPcbPlanes(board, 500);
                stackup = ExportPcbStackup(board);
                keepouts = ExportPcbKeepouts(components);
                validation = BuildPcbValidationSummary(components, routing, planes, stackup);
            }
            else
            {
                routing = new Dictionary<string, object>
                {
                    ["trackCount"] = 0,
                    ["viaCount"] = 0,
                    ["tracks"] = new List<Dictionary<string, object>>(),
                    ["vias"] = new List<Dictionary<string, object>>(),
                };
                planes = new Dictionary<string, object>
                {
                    ["polygonCount"] = 0,
                    ["polygons"] = new List<Dictionary<string, object>>(),
                };
                stackup = new Dictionary<string, object>
                {
                    ["note"] = "Skipped for placement-only export.",
                };
                keepouts = ExportPcbKeepouts(components);
                validation = new Dictionary<string, object>
                {
                    ["skipped"] = true,
                    ["note"] = "Routing/plane validation skipped for placement-only export.",
                };
            }

            return new Dictionary<string, object>
            {
                ["document"] = SafeText(board.GetState_FileName()),
                ["path"] = SafeText(fullPath),
                ["components"] = components.OrderBy(c => c["designator"]?.ToString(), StringComparer.OrdinalIgnoreCase).ToList(),
                ["nets"] = BuildNetList(netMap),
                ["routing"] = routing,
                ["planes"] = planes,
                ["stackup"] = stackup,
                ["keepouts"] = keepouts,
                ["validation"] = validation,
            };
        }

        private static Dictionary<string, object> ExportPcbKeepouts(
            List<Dictionary<string, object>> components)
        {
            var regions = new List<Dictionary<string, object>>();

            foreach (var component in components)
            {
                if (!(component.TryGetValue("placement", out var placementObj)
                    && placementObj is Dictionary<string, object> placement))
                {
                    continue;
                }

                if (!placement.TryGetValue("xMils", out var xObj) ||
                    !placement.TryGetValue("yMils", out var yObj))
                {
                    continue;
                }

                var xMils = Convert.ToDouble(xObj);
                var yMils = Convert.ToDouble(yObj);
                component.TryGetValue("pattern", out var patternObj);
                component.TryGetValue("designator", out var designatorObj);
                component.TryGetValue("layer", out var layerObj);
                var halfSize = EstimateCourtyardHalfSizeMils(SafeText(patternObj?.ToString()));
                regions.Add(new Dictionary<string, object>
                {
                    ["kind"] = "component_courtyard",
                    ["designator"] = SafeText(designatorObj?.ToString()),
                    ["layer"] = SafeText(layerObj?.ToString()),
                    ["xMils"] = Math.Round(xMils, 3),
                    ["yMils"] = Math.Round(yMils, 3),
                    ["radiusMils"] = Math.Round(halfSize, 3),
                    ["bboxMils"] = new List<double>
                    {
                        Math.Round(xMils - halfSize, 3),
                        Math.Round(yMils - halfSize, 3),
                        Math.Round(xMils + halfSize, 3),
                        Math.Round(yMils + halfSize, 3),
                    },
                });
            }

            return new Dictionary<string, object>
            {
                ["regionCount"] = regions.Count,
                ["regions"] = regions,
            };
        }

        private static double EstimateCourtyardHalfSizeMils(string pattern)
        {
            var text = SafeText(pattern).ToUpperInvariant();
            if (text.Contains("0402"))
                return 24.0;
            if (text.Contains("0603"))
                return 32.0;
            if (text.Contains("0805"))
                return 40.0;
            if (text.Contains("1206"))
                return 52.0;
            if (text.Contains("QFN") || text.Contains("QFP") || text.Contains("BGA"))
                return 180.0;
            if (text.Contains("SOT") || text.Contains("SOIC"))
                return 70.0;
            return 48.0;
        }

        private static Dictionary<string, object> ExportPcbRouting(IPCB_Board board, int maxTracks)
        {
            var tracks = new List<Dictionary<string, object>>();
            var vias = new List<Dictionary<string, object>>();
            var electricalLayers = GetElectricalLayerNames(board);
            var signalLayers = GetSignalLayerNames(board);

            ExportPcbObjects(board, PcbTObjectId.eTrackObject, Math.Max(maxTracks, 12000), obj =>
            {
                var track = obj as IPCB_Track;
                if (track == null)
                    return;

                var layerName = ReadLayerName(board, track.GetState_V7Layer());
                var isElectrical = IsNamedElectricalLayer(layerName, electricalLayers, signalLayers);
                tracks.Add(new Dictionary<string, object>
                {
                    ["kind"] = "track",
                    ["net"] = ReadPrimitiveNetName(track as IPCB_Primitive),
                    ["layer"] = layerName,
                    ["electrical"] = isElectrical,
                    ["widthMils"] = Math.Round(CoordUtils.CoordToMils(track.GetState_Width()), 3),
                    ["widthMm"] = Math.Round(CoordUtils.CoordToMm(track.GetState_Width()), 4),
                    ["x1Mils"] = Math.Round(CoordUtils.CoordToMils(track.GetState_X1()), 3),
                    ["y1Mils"] = Math.Round(CoordUtils.CoordToMils(track.GetState_Y1()), 3),
                    ["x2Mils"] = Math.Round(CoordUtils.CoordToMils(track.GetState_X2()), 3),
                    ["y2Mils"] = Math.Round(CoordUtils.CoordToMils(track.GetState_Y2()), 3),
                });
            });

            ExportPcbObjects(board, PcbTObjectId.eViaObject, 5000, obj =>
            {
                var via = obj as IPCB_Via;
                if (via == null)
                    return;

                vias.Add(new Dictionary<string, object>
                {
                    ["kind"] = "via",
                    ["net"] = ReadPrimitiveNetName(via as IPCB_Primitive),
                    ["xMils"] = Math.Round(CoordUtils.CoordToMils(via.GetState_XLocation()), 3),
                    ["yMils"] = Math.Round(CoordUtils.CoordToMils(via.GetState_YLocation()), 3),
                    ["sizeMils"] = Math.Round(CoordUtils.CoordToMils(via.GetState_Size()), 3),
                    ["holeMils"] = Math.Round(CoordUtils.CoordToMils(via.GetState_HoleSize()), 3),
                    ["lowLayer"] = ReadLayerName(board, via.GetState_LowLayer()),
                    ["highLayer"] = ReadLayerName(board, via.GetState_HighLayer()),
                });
            });

            return new Dictionary<string, object>
            {
                ["trackCount"] = tracks.Count,
                ["viaCount"] = vias.Count,
                ["electricalTrackCount"] = tracks.Count(t => t.TryGetValue("electrical", out var e) && e is true),
                ["tracks"] = tracks,
                ["vias"] = vias,
            };
        }

        private static Dictionary<string, object> ExportPcbPlanes(IPCB_Board board, int maxPolygons)
        {
            var polygons = new List<Dictionary<string, object>>();
            var electricalLayers = GetElectricalLayerNames(board);
            var signalLayers = GetSignalLayerNames(board);

            ExportPcbObjects(board, PcbTObjectId.ePolyObject, maxPolygons, obj =>
            {
                var polygon = obj as IPCB_Polygon;
                if (polygon == null)
                    return;

                var layerName = ReadLayerName(board, polygon.GetState_V7Layer());
                polygons.Add(new Dictionary<string, object>
                {
                    ["kind"] = "polygon",
                    ["net"] = ReadPrimitiveNetName(polygon as IPCB_Primitive),
                    ["layer"] = layerName,
                    ["electrical"] = IsNamedElectricalLayer(layerName, electricalLayers, signalLayers),
                });
            });

            return new Dictionary<string, object>
            {
                ["polygonCount"] = polygons.Count,
                ["polygons"] = polygons,
            };
        }

        private static Dictionary<string, object> ExportPcbStackup(IPCB_Board board)
        {
            var layers = new List<Dictionary<string, object>>();
            foreach (var layer in EnumerateBoardLayers(board, board.Internal_ElectricalLayerIterator, it => it.Internal_AddFilter_ElectricalLayers()))
            {
                var name = SafeText(board.LayerName(layer));
                layers.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["kind"] = "electrical",
                });
            }

            foreach (var layer in EnumerateBoardLayers(board, board.Internal_SignalLayerIterator, it => it.Internal_AddFilter_SignalLayers()))
            {
                var name = SafeText(board.LayerName(layer));
                if (layers.Any(l => string.Equals(SafeText(l["name"]?.ToString()), name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                layers.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["kind"] = "signal",
                });
            }

            foreach (var layer in EnumerateBoardLayers(board, board.Internal_InternalPlaneLayerIterator, it => it.Internal_AddFilter_InternalPlaneLayers()))
            {
                var name = SafeText(board.LayerName(layer));
                layers.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["kind"] = "plane",
                });
            }

            return new Dictionary<string, object>
            {
                ["layerCount"] = layers.Count,
                ["layers"] = layers,
            };
        }

        private static Dictionary<string, object> BuildPcbValidationSummary(
            List<Dictionary<string, object>> components,
            Dictionary<string, object> routing,
            Dictionary<string, object> planes,
            Dictionary<string, object> stackup)
        {
            var trackWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (routing.TryGetValue("tracks", out var trackObj) && trackObj is List<Dictionary<string, object>> trackList)
            {
                foreach (var track in trackList)
                {
                    track.TryGetValue("widthMils", out var widthObj);
                    var width = SafeText(widthObj?.ToString());
                    if (string.IsNullOrWhiteSpace(width))
                        width = "unknown";
                    trackWidths[width] = trackWidths.TryGetValue(width, out var count) ? count + 1 : 1;
                }
            }

            var planeNets = new List<string>();
            if (planes.TryGetValue("polygons", out var planeObj) && planeObj is List<Dictionary<string, object>> planeList)
            {
                foreach (var plane in planeList)
                {
                    plane.TryGetValue("net", out var netObj);
                    var net = SafeText(netObj?.ToString());
                    if (!string.IsNullOrWhiteSpace(net))
                        planeNets.Add(net);
                }
            }

            return new Dictionary<string, object>
            {
                ["componentCount"] = components.Count,
                ["trackCount"] = GetObjectValue(routing, "trackCount"),
                ["viaCount"] = GetObjectValue(routing, "viaCount"),
                ["planeCount"] = GetObjectValue(planes, "polygonCount"),
                ["stackupLayerCount"] = GetObjectValue(stackup, "layerCount"),
                ["trackWidthHistogramMils"] = trackWidths,
                ["groundPlaneNets"] = planeNets
                    .Where(net => net.IndexOf("gnd", StringComparison.OrdinalIgnoreCase) >= 0 || net.Equals("VSS", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ["powerPlaneNets"] = planeNets
                    .Where(net => net.IndexOf("3v3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  net.IndexOf("vcc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  net.IndexOf("vdd", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private static void ExportPcbObjects(IPCB_Board board, PcbTObjectId objectId, int maxCount, Action<object> handler)
        {
            if (maxCount <= 0)
                return;

            object iteratorObj = board.Internal_BoardIterator_Create();
            var iterator = (IPCB_AbstractIterator)iteratorObj;
            iterator.AddFilter_ObjectSet(new PcbTObjectSet(objectId));
            try
            {
                var count = 0;
                var obj = iterator.FirstPCBObject();
                while (obj != null && count < maxCount)
                {
                    handler(obj);
                    count++;
                    obj = iterator.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref iteratorObj);
            }
        }

        private static string ReadPrimitiveNetName(IPCB_Primitive primitive)
        {
            if (primitive == null)
                return string.Empty;

            try
            {
                if (primitive.Internal_GetState_Net() is IPCB_Net net && net != null)
                    return SafeText(net.GetState_Name());
            }
            catch
            {
                // Fall through to alternate accessors used by some Altium SDK builds.
            }

            try
            {
                var method = primitive.GetType().GetMethod("GetState_Net");
                if (method?.Invoke(primitive, null) is IPCB_Net net2 && net2 != null)
                    return SafeText(net2.GetState_Name());
            }
            catch
            {
                // Ignore and return empty.
            }

            return string.Empty;
        }

        private static string ReadLayerName(IPCB_Board board, object layer)
        {
            if (layer == null)
                return string.Empty;

            try
            {
                if (layer is IV7_Layer v7Layer)
                {
                    var named = SafeText(board.LayerName(v7Layer));
                    if (!string.IsNullOrWhiteSpace(named) &&
                        !named.StartsWith("PCB.V7_Layer", StringComparison.OrdinalIgnoreCase))
                        return named;
                }
            }
            catch
            {
                // Fall through.
            }

            try
            {
                var method = board.GetType().GetMethod("LayerName", new[] { layer.GetType() });
                if (method != null)
                {
                    var named = SafeText(method.Invoke(board, new[] { layer })?.ToString());
                    if (!string.IsNullOrWhiteSpace(named) &&
                        !named.StartsWith("PCB.V7_Layer", StringComparison.OrdinalIgnoreCase))
                        return named;
                }
            }
            catch
            {
                // Fall through.
            }

            return SafeText(layer.ToString());
        }

        private static HashSet<string> GetElectricalLayerNames(IPCB_Board board)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in EnumerateBoardLayers(board, board.Internal_ElectricalLayerIterator, it => it.Internal_AddFilter_ElectricalLayers()))
            {
                var name = SafeText(board.LayerName(layer));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names;
        }

        private static HashSet<string> GetSignalLayerNames(IPCB_Board board)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in EnumerateBoardLayers(board, board.Internal_SignalLayerIterator, it => it.Internal_AddFilter_SignalLayers()))
            {
                var name = SafeText(board.LayerName(layer));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names;
        }

        private static IEnumerable<IV7_Layer> EnumerateBoardLayers(
            IPCB_Board board,
            Func<object> createIterator,
            Action<IPCB_LayerIterator> configure)
        {
            var layers = new List<IV7_Layer>();
            object iteratorObj = null;
            try
            {
                iteratorObj = createIterator();
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
            }
            catch
            {
                // Some Altium builds expose different iterator helpers; leave empty.
            }

            return layers;
        }

        private static bool IsNamedElectricalLayer(
            string layerName,
            HashSet<string> electricalLayers,
            HashSet<string> signalLayers)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return false;

            if (electricalLayers.Contains(layerName) || signalLayers.Contains(layerName))
                return true;

            var n = layerName.ToLowerInvariant();
            if (n.Contains("mechanical") ||
                n.Contains("overlay") ||
                n.Contains("paste") ||
                n.Contains("solder") ||
                n.Contains("keep") ||
                n.Contains("assembly") ||
                n.Contains("courtyard") ||
                n.Contains("dimension") ||
                n.Contains("drill") ||
                n.Contains("3d") ||
                n.StartsWith("pcb.v7_layer"))
                return false;

            return n.Contains("top") ||
                   n.Contains("bottom") ||
                   n.Contains("signal") ||
                   n.Contains("mid") ||
                   n.Contains("plane") ||
                   n.Contains("power") ||
                   n.Contains("gnd") ||
                   n.Contains("ground");
        }

        private static Dictionary<string, object> ReadSchematicPlacement(ISch_Component component)
        {
            var graphical = component as ISch_GraphicalObject;
            if (graphical == null)
                return new Dictionary<string, object>();

            var location = graphical.Internal_GetState_Location();
            return new Dictionary<string, object>
            {
                ["xMils"] = Math.Round(CoordUtils.CoordToMils(location.GetX()), 3),
                ["yMils"] = Math.Round(CoordUtils.CoordToMils(location.GetY()), 3),
                ["xMm"] = Math.Round(CoordUtils.CoordToMm(location.GetX()), 4),
                ["yMm"] = Math.Round(CoordUtils.CoordToMm(location.GetY()), 4),
                ["rotation"] = component.GetState_Orientation().ToString(),
            };
        }

        private static Dictionary<string, object> ReadPcbPlacement(IPCB_Component component)
        {
            return new Dictionary<string, object>
            {
                ["xMils"] = Math.Round(CoordUtils.CoordToMils(component.GetState_XLocation()), 3),
                ["yMils"] = Math.Round(CoordUtils.CoordToMils(component.GetState_YLocation()), 3),
                ["xMm"] = Math.Round(CoordUtils.CoordToMm(component.GetState_XLocation()), 4),
                ["yMm"] = Math.Round(CoordUtils.CoordToMm(component.GetState_YLocation()), 4),
                ["rotation"] = Math.Round(component.GetState_Rotation(), 3),
                ["layer"] = component.GetState_FlippedOnLayer() ? "Bottom" : "Top",
            };
        }

        /// <summary>
        /// Read the component's actual bounding box (excluding name/comment text) from
        /// the Altium PCB API. Returns [x1, y1, x2, y2] in mils, plus the half-width
        /// and half-height relative to the component origin so the planner can compute
        /// the bounding box at any target position.
        /// </summary>
        private static Dictionary<string, object> ReadComponentBoundingBox(
            IPCB_Component component, Dictionary<string, object> placement)
        {
            try
            {
                var rect = component.Internal_BoundingRectangleNoNameComment();
                if (rect == null) return null;

                var x1 = Math.Round(CoordUtils.CoordToMils(rect.GetLeft()), 3);
                var y1 = Math.Round(CoordUtils.CoordToMils(rect.GetBottom()), 3);
                var x2 = Math.Round(CoordUtils.CoordToMils(rect.GetRight()), 3);
                var y2 = Math.Round(CoordUtils.CoordToMils(rect.GetTop()), 3);

                // Compute half-width/height relative to the component origin so the
                // planner can translate the bounding box to any target position.
                var originX = placement != null && placement.TryGetValue("xMils", out var oxObj)
                    ? Convert.ToDouble(oxObj) : 0.0;
                var originY = placement != null && placement.TryGetValue("yMils", out var oyObj)
                    ? Convert.ToDouble(oyObj) : 0.0;

                return new Dictionary<string, object>
                {
                    ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2,
                    ["halfWidthMils"] = Math.Round(Math.Max(
                        Math.Abs(x2 - originX), Math.Abs(x1 - originX)), 3),
                    ["halfHeightMils"] = Math.Round(Math.Max(
                        Math.Abs(y2 - originY), Math.Abs(y1 - originY)), 3),
                    ["widthMils"] = Math.Round(Math.Abs(x2 - x1), 3),
                    ["heightMils"] = Math.Round(Math.Abs(y2 - y1), 3),
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<Dictionary<string, object>> ReadPcbComponentPads(
            IPCB_Component component,
            string designator,
            Dictionary<string, List<Dictionary<string, string>>> netMap)
        {
            var pads = new List<Dictionary<string, object>>();
            var group = component as IPCB_Group;
            if (group == null)
                return pads;

            object padIteratorObj = group.Internal_GroupIterator_Create();
            var padIterator = (IPCB_AbstractIterator)padIteratorObj;
            padIterator.AddFilter_ObjectSet(new PcbTObjectSet(PcbTObjectId.ePadObject));
            try
            {
                var obj = padIterator.FirstPCBObject();
                while (obj != null)
                {
                    var pad = obj as IPCB_Pad;
                    if (pad != null)
                    {
                        var padName = SafeText(pad.GetState_Name());
                        var netName = SafeText((pad as IPCB_Primitive)?.Internal_GetState_Net() is IPCB_Net net
                            ? net.GetState_Name()
                            : string.Empty);
                        if (string.IsNullOrWhiteSpace(netName))
                            netName = "No Net";

                        var padLayer = component.GetState_FlippedOnLayer() ? "Bottom" : "Top";
                        var padRotation = Math.Round(pad.GetState_Rotation(), 3);
                        var padWidthMils = Math.Round(
                            CoordUtils.CoordToMils(Math.Abs(pad.GetState_TopXSize())), 3);
                        var padHeightMils = Math.Round(
                            CoordUtils.CoordToMils(Math.Abs(pad.GetState_TopYSize())), 3);

                        pads.Add(new Dictionary<string, object>
                        {
                            ["name"] = padName,
                            ["net"] = netName,
                            ["xMils"] = Math.Round(CoordUtils.CoordToMils(pad.GetState_XLocation()), 3),
                            ["yMils"] = Math.Round(CoordUtils.CoordToMils(pad.GetState_YLocation()), 3),
                            ["xMm"] = Math.Round(CoordUtils.CoordToMm(pad.GetState_XLocation()), 4),
                            ["yMm"] = Math.Round(CoordUtils.CoordToMm(pad.GetState_YLocation()), 4),
                            ["layer"] = padLayer,
                            ["rotation"] = padRotation,
                            ["widthMils"] = padWidthMils,
                            ["heightMils"] = padHeightMils,
                        });

                        if (!netName.Equals("No Net", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!netMap.TryGetValue(netName, out var connections))
                            {
                                connections = new List<Dictionary<string, string>>();
                                netMap[netName] = connections;
                            }

                            connections.Add(new Dictionary<string, string>
                            {
                                ["designator"] = designator,
                                ["pin"] = padName,
                            });
                        }
                    }

                    obj = padIterator.NextPCBObject();
                }
            }
            finally
            {
                group.GroupIterator_Destroy(ref padIteratorObj);
            }

            return pads.OrderBy(p => p["name"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, string> ReadParameters(ISch_Component component)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var iterator = component.SchIterator_Create();
            iterator.AddFilter_ObjectSet(new SchTObjectSet(SchTObjectId.eParameter));
            try
            {
                var parameter = iterator.FirstSchObject() as ISch_Parameter;
                while (parameter != null)
                {
                    var name = SafeText(parameter.GetState_Name());
                    if (!string.IsNullOrWhiteSpace(name))
                        parameters[name] = SafeText(parameter.GetState_Text());

                    parameter = iterator.NextSchObject() as ISch_Parameter;
                }
            }
            finally
            {
                component.SchIterator_Destroy(ref iterator);
            }

            return parameters;
        }

        private static List<Dictionary<string, string>> ReadPins(
            ISch_Component component,
            string designator,
            Dictionary<string, Dictionary<string, string>> pinNetLookup)
        {
            pinNetLookup.TryGetValue(designator, out var netsByPin);
            var pins = new List<Dictionary<string, string>>();
            var iterator = component.SchIterator_Create();
            iterator.AddFilter_ObjectSet(new SchTObjectSet(SchTObjectId.ePin));
            try
            {
                var pin = iterator.FirstSchObject() as ISch_Pin;
                while (pin != null)
                {
                    var pinNumber = SafeText(pin.GetState_Designator());
                    var netName = ResolvePinNet(pin, pinNumber, netsByPin);

                    pins.Add(new Dictionary<string, string>
                    {
                        ["number"] = pinNumber,
                        ["name"] = SafeText(pin.GetState_Name()),
                        ["net"] = netName,
                    });

                    pin = iterator.NextSchObject() as ISch_Pin;
                }
            }
            finally
            {
                component.SchIterator_Destroy(ref iterator);
            }

            return pins.OrderBy(p => p["number"], StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<Dictionary<string, object>> BuildNetList(
            Dictionary<string, List<Dictionary<string, string>>> netMap) =>
            netMap
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new Dictionary<string, object>
                {
                    ["name"] = kvp.Key,
                    ["connections"] = kvp.Value
                        .OrderBy(c => c["designator"], StringComparer.OrdinalIgnoreCase)
                        .ThenBy(c => c["pin"], StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                })
                .ToList();

        private static string ResolvePinNet(
            ISch_Pin pin,
            string pinNumber,
            Dictionary<string, string> netsByPin)
        {
            if (netsByPin != null &&
                netsByPin.TryGetValue(pinNumber, out var compiledNet) &&
                !string.IsNullOrWhiteSpace(compiledNet))
            {
                return compiledNet;
            }

            var hiddenNet = SafeText(pin.GetState_HiddenNetName());
            if (!string.IsNullOrWhiteSpace(hiddenNet) && !IsAutoGeneratedNetName(hiddenNet))
                return hiddenNet;

            return string.IsNullOrWhiteSpace(hiddenNet) ? "No Net" : hiddenNet;
        }

        private static string ResolveCompiledNetName(INet net)
        {
            var fullName = SafeText(net.DM_FullNetName());
            var netName = SafeText(net.DM_NetName());
            var calculated = SafeText(net.DM_CalculatedNetName());

            if (!string.IsNullOrWhiteSpace(fullName) && !IsAutoGeneratedNetName(fullName))
                return fullName;

            if (!string.IsNullOrWhiteSpace(netName) && !IsAutoGeneratedNetName(netName))
                return netName;

            if (!string.IsNullOrWhiteSpace(fullName))
                return fullName;

            if (!string.IsNullOrWhiteSpace(netName))
                return netName;

            return calculated;
        }

        private static bool IsAutoGeneratedNetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            if (!name.StartsWith("Net", StringComparison.OrdinalIgnoreCase))
                return false;

            // Altium auto names like NetIC1_6, NetC4_2
            for (var i = 3; i < name.Length; i++)
            {
                var ch = name[i];
                if (char.IsLetterOrDigit(ch) || ch == '_')
                    continue;
                return false;
            }

            return name.Length > 3;
        }

        private static void CollectNetNameAliases(
            ISch_Document schDoc,
            Dictionary<string, string> aliases)
        {
            CollectSchObjectAliases(schDoc, SchTObjectId.eNetLabel, "GetState_Text", aliases);
            CollectSchObjectAliases(schDoc, SchTObjectId.eSheetEntry, "GetState_Name", aliases);
            CollectSchObjectAliases(schDoc, SchTObjectId.ePort, "GetState_Name", aliases);
            CollectSchObjectAliases(schDoc, SchTObjectId.ePowerObject, "GetState_Text", aliases);
        }

        private static void CollectSchObjectAliases(
            ISch_Document schDoc,
            SchTObjectId objectId,
            string nameMethod,
            Dictionary<string, string> aliases)
        {
            var iterator = schDoc.SchIterator_Create();
            iterator.AddFilter_ObjectSet(new SchTObjectSet(objectId));
            try
            {
                var obj = iterator.FirstSchObject();
                while (obj != null)
                {
                    TryRegisterNetAlias(obj, nameMethod, aliases);
                    obj = iterator.NextSchObject();
                }
            }
            finally
            {
                schDoc.SchIterator_Destroy(ref iterator);
            }
        }

        private static void TryRegisterNetAlias(
            object obj,
            string nameMethod,
            Dictionary<string, string> aliases)
        {
            if (obj == null)
                return;

            var type = obj.GetType();
            var textMethod = type.GetMethod(nameMethod);
            var hiddenMethod = type.GetMethod("GetState_HiddenNetName");
            if (textMethod == null || hiddenMethod == null)
                return;

            var userName = SafeText(textMethod.Invoke(obj, null) as string);
            var hiddenNet = SafeText(hiddenMethod.Invoke(obj, null) as string);
            RegisterNetAlias(userName, hiddenNet, aliases);
        }

        private static void RegisterNetAlias(
            string userName,
            string hiddenNet,
            Dictionary<string, string> aliases)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(hiddenNet))
                return;

            if (IsAutoGeneratedNetName(userName))
                return;

            aliases[hiddenNet] = userName;
        }

        private static void ApplyNetAliases(
            Dictionary<string, List<Dictionary<string, string>>> netMap,
            Dictionary<string, Dictionary<string, string>> pinNetLookup,
            Dictionary<string, string> aliases)
        {
            if (aliases.Count == 0)
                return;

            var remapped = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in netMap)
            {
                var targetName = ResolveNetAlias(kvp.Key, aliases);
                if (!remapped.TryGetValue(targetName, out var connections))
                {
                    connections = new List<Dictionary<string, string>>();
                    remapped[targetName] = connections;
                }

                foreach (var connection in kvp.Value)
                    AddConnection(connections, connection);
            }

            netMap.Clear();
            foreach (var kvp in remapped)
                netMap[kvp.Key] = kvp.Value;

            foreach (var designator in pinNetLookup.Keys.ToList())
            {
                var pinMap = pinNetLookup[designator];
                foreach (var pinNumber in pinMap.Keys.ToList())
                    pinMap[pinNumber] = ResolveNetAlias(pinMap[pinNumber], aliases);
            }
        }

        private static string ResolveNetAlias(string netName, Dictionary<string, string> aliases)
        {
            if (string.IsNullOrWhiteSpace(netName))
                return netName;

            if (aliases.TryGetValue(netName, out var alias) && !string.IsNullOrWhiteSpace(alias))
                return alias;

            return netName;
        }

        private static void AddConnection(
            List<Dictionary<string, string>> connections,
            Dictionary<string, string> connection)
        {
            connection.TryGetValue("designator", out var designatorValue);
            connection.TryGetValue("pin", out var pinValue);
            var designator = SafeText(designatorValue);
            var pin = SafeText(pinValue);
            if (string.IsNullOrWhiteSpace(designator))
                return;

            foreach (var existing in connections)
            {
                if (existing["designator"].Equals(designator, StringComparison.OrdinalIgnoreCase) &&
                    existing["pin"].Equals(pin, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            connections.Add(new Dictionary<string, string>
            {
                ["designator"] = designator,
                ["pin"] = pin,
            });
        }

        private static List<Dictionary<string, object>> BuildProjectNets(
            List<Dictionary<string, object>> schematicSheets)
        {
            var merged = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var sheet in schematicSheets)
            {
                foreach (var net in sheet["nets"] as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>())
                {
                    var name = SafeText(net["name"]?.ToString());
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (!merged.TryGetValue(name, out var entry))
                    {
                        entry = new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["connections"] = new List<Dictionary<string, string>>(),
                        };
                        merged[name] = entry;
                    }

                    var connections = (List<Dictionary<string, string>>)entry["connections"];
                    foreach (var connection in net["connections"] as List<Dictionary<string, string>> ?? new List<Dictionary<string, string>>())
                        AddConnection(connections, connection);
                }
            }

            return merged.Values
                .OrderBy(n => n["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<Dictionary<string, string>> ReadErcViolations(ISch_Document schDoc)
        {
            var violations = new List<Dictionary<string, string>>();
            var iterator = schDoc.SchIterator_Create();
            iterator.AddFilter_ObjectSet(new SchTObjectSet(SchTObjectId.eErrorMarker));
            try
            {
                var marker = iterator.FirstSchObject() as ISch_ErrorMarker;
                while (marker != null)
                {
                    violations.Add(new Dictionary<string, string>
                    {
                        ["severity"] = "Error",
                        ["message"] = SafeText(marker.GetState_Text()),
                        ["object"] = string.Empty,
                    });

                    marker = iterator.NextSchObject() as ISch_ErrorMarker;
                }
            }
            finally
            {
                schDoc.SchIterator_Destroy(ref iterator);
            }

            return violations;
        }

        private static IEnumerable<IDocument> EnumerateLogicalDocuments(IProject project)
        {
            for (var i = 0; i < project.DM_LogicalDocumentCount(); i++)
            {
                if (project.Internal_DM_LogicalDocuments(i) is IDocument document)
                    yield return document;
            }
        }

        private static IEnumerable<IDocument> EnumeratePhysicalDocuments(IProject project)
        {
            for (var i = 0; i < project.DM_PhysicalDocumentCount(); i++)
            {
                if (project.Internal_DM_PhysicalDocuments(i) is IDocument document)
                    yield return document;
            }
        }

        private static bool IsSchematicDocument(IDocument document)
        {
            var kind = SafeText(document.DM_DocumentKind());
            if (kind.IndexOf("SchLib", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return kind.IndexOf("Sch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPcbDocument(IDocument document)
        {
            var kind = SafeText(document.DM_DocumentKind());
            return kind.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void WriteJson(string outputPath, Dictionary<string, object> payload)
        {
            File.WriteAllText(
                outputPath,
                JsonConvert.SerializeObject(
                    payload,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        private static object GetObjectValue(Dictionary<string, object> dictionary, string key)
        {
            return dictionary != null && dictionary.TryGetValue(key, out var value) ? value : null;
        }

        private static string SafeText(string value) => value?.Trim() ?? string.Empty;
    }
}
