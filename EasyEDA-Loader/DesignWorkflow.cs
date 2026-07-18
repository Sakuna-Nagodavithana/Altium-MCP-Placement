using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Checklist aligned to how experts run schematic→fab (see ExpertDesignPlaybook).
    /// </summary>
    internal static class DesignWorkflow
    {
        public sealed class Step
        {
            public int Number { get; set; }
            public string Id { get; set; }
            public string PhaseId { get; set; }
            public string Title { get; set; }
            public string Detail { get; set; }
            public string ExpertHow { get; set; }
            public string PluginAction { get; set; }
            public string Status { get; set; } = "todo";
            public string StatusNote { get; set; } = string.Empty;
        }

        public static List<Step> GetTemplateSteps() => new List<Step>
        {
            new Step
            {
                Number = 1,
                Id = "parts",
                PhaseId = "schematic",
                Title = "Import parts that match fab",
                Detail = "JLCPCB/LCSC symbols + footprints into libraries, then place schematic.",
                ExpertHow = "Experts only place parts they can buy/assemble. Clear power net names from day one.",
                PluginAction = "EasyEDA Component Loader / BOM Builder",
            },
            new Step
            {
                Number = 2,
                Id = "erc",
                PhaseId = "schematic",
                Title = "Schematic ERC until clean",
                Detail = "Compile + Electrical Rules Check. Fix floating pins, power flags, duplicate designators.",
                ExpertHow = "Experts never start layout with ERC red. Schematic sheet blocks should preview the floorplan.",
                PluginAction = "Altium: Project → Compile / Schematic ERC",
            },
            new Step
            {
                Number = 3,
                Id = "pcb_create",
                PhaseId = "schematic",
                Title = "Update PCB from schematic",
                Detail = "Design → Update PCB Document. Set board outline next.",
                ExpertHow = "Sync early so footprint issues show before stackup/rules work is wasted.",
                PluginAction = "Altium: Design → Update PCB Document",
            },
            new Step
            {
                Number = 4,
                Id = "stackup",
                PhaseId = "stackup_rules",
                Title = "Load fab stackup (before place/route)",
                Detail = "ESP/LoRa: 4L 1.6mm JLC04161H-7628 — Mid1 GND, Mid2 3v3.",
                ExpertHow = "Experts lock stackup first. Impedance and planes depend on it; changing later rewrites copper.",
                PluginAction = "MCP: Stackup Advisor → Use This → Layer Stack Manager load",
            },
            new Step
            {
                Number = 5,
                Id = "rules",
                PhaseId = "stackup_rules",
                Title = "Net classes + width/clearance",
                Detail = "Classify RF / HighSpeed / PWR / Logic; apply MCP rules (+ fab .RUL mins).",
                ExpertHow = "Rules are the 'brain' of interactive routing. Without classes, every trace is a guess.",
                PluginAction = "MCP: Setup Net Classes & Rules",
            },
            new Step
            {
                Number = 6,
                Id = "place",
                PhaseId = "floorplan",
                Title = "Floorplan → auto-place → hand-fix RF",
                Detail = "Partition RF / MCU / power / connectors. Auto-Place clusters (creates Rooms), Fanout Decap Vias, Optimize, then nudge RF.",
                ExpertHow = "Pros place connectors/power/RF blocks first, pull decaps to pins, lock rooms, fanout power to planes — then route.",
                PluginAction = "MCP: Auto-Place → Fanout Decap Vias → Optimize (manual RF)",
            },
            new Step
            {
                Number = 7,
                Id = "planes",
                PhaseId = "planes",
                Title = "Pour GND/power planes early",
                Detail = "Solid Mid1 GND under RF. Mid2 3v3. Continuous GND — no slots under RF/clocks.",
                ExpertHow = "Pour before routing critical nets so return paths exist while you route.",
                PluginAction = "Altium: Polygon Pour Manager",
            },
            new Step
            {
                Number = 8,
                Id = "route",
                PhaseId = "route",
                Title = "Route by priority (RF → HS → PWR → Logic)",
                Detail = "Use Route Priority list. Interactive Routing with net classes. Never auto-route RF.",
                ExpertHow = "Critical nets own copper real estate first; GPIO fills leftovers.",
                PluginAction = "MCP: Route Priority + Altium Interactive Routing",
            },
            new Step
            {
                Number = 9,
                Id = "stitch",
                PhaseId = "route",
                Title = "Via-stitch RF / clocks",
                Detail = "GND fence vias along RF and HighSpeed after those routes exist.",
                ExpertHow = "Stitching returns RF current to the plane and tightens impedance.",
                PluginAction = "MCP: Stitch Vias (RF / Clocks)",
            },
            new Step
            {
                Number = 10,
                Id = "drc",
                PhaseId = "verify",
                Title = "Full DRC — Jump / fix / Re-Run",
                Detail = "Altium batch + MCP extras (pad↔track, neckdown, power clearance) until PASS.",
                ExpertHow = "Experts treat DRC fail as a stop-ship. Fix copper, don't waive fab shorts.",
                PluginAction = "MCP: Full PCB DRC (Errors)",
            },
            new Step
            {
                Number = 11,
                Id = "export",
                PhaseId = "verify",
                Title = "Export → Gerbers only after PASS",
                Detail = "Refresh connectivity/MCP. Then Altium Fabrication Outputs + JLCPCB BOM.",
                ExpertHow = "Match order form to stackup template (e.g. JLC04161H-7628 + TG155 if impedance).",
                PluginAction = "MCP: Export / panel auto-export",
            },
        };

        public static List<Step> Evaluate()
        {
            var steps = GetTemplateSteps();
            foreach (var step in steps)
                FillStatus(step);
            return steps;
        }

        public static string FormatSummary(IEnumerable<Step> steps)
        {
            var list = steps?.ToList() ?? GetTemplateSteps();
            var done = list.Count(s => s.Status == "done");
            var warn = list.Count(s => s.Status == "warn");
            var todo = list.Count - done - warn;
            var next = list.FirstOrDefault(s => s.Status != "done");
            var nextText = next == null ? "All checklist items look done — still re-run DRC before Gerbers."
                : $"Next: {next.Number}. {next.Title}";
            return $"Expert workflow: {done} done, {warn} need attention, {todo} open. {nextText}";
        }

        private static void FillStatus(Step step)
        {
            try
            {
                switch (step.Id)
                {
                    case "parts":
                    {
                        var schLib = Path.Combine(AltiumEeDir, "EasyEDA.schlib");
                        var pcbLib = Path.Combine(AltiumEeDir, "EasyEDA.pcblib");
                        if (File.Exists(schLib) || File.Exists(pcbLib))
                        {
                            step.Status = "done";
                            step.StatusNote = "EasyEDA libraries present under Documents\\AltiumEE.";
                        }
                        else
                        {
                            step.Status = "todo";
                            step.StatusNote = "No EasyEDA.schlib/pcblib yet — import parts first.";
                        }
                        break;
                    }
                    case "pcb_create":
                    {
                        var board = PcbDocumentHelper.ResolveProjectPcbBoard();
                        if (board != null)
                        {
                            step.Status = "done";
                            step.StatusNote = "PCB document is available.";
                        }
                        else
                        {
                            step.Status = "todo";
                            step.StatusNote = "Open or create a .PcbDoc and update from schematic.";
                        }
                        break;
                    }
                    case "stackup":
                    {
                        var board = PcbDocumentHelper.ResolveProjectPcbBoard();
                        if (board == null)
                        {
                            step.Status = "todo";
                            step.StatusNote = "Open PCB first.";
                            break;
                        }

                        var count = PcbStackupAdvisor.GetElectricalLayerCount(board);
                        var pref = PcbStackupAdvisor.LoadPreference();
                        if (count >= 4)
                        {
                            step.Status = "done";
                            step.StatusNote = $"{count} electrical layers · preferred {pref.Template}. Confirm Mid1=GND Mid2=3v3.";
                        }
                        else if (count == 2)
                        {
                            step.Status = "warn";
                            step.StatusNote = "2-layer — weak for LoRa/WiFi. Prefer 4L JLC7628 via Stackup Advisor.";
                        }
                        else
                        {
                            step.Status = "warn";
                            step.StatusNote = $"Detected {count} layers — open Stackup Advisor.";
                        }
                        break;
                    }
                    case "rules":
                    {
                        if (File.Exists(PcbRulesProfile.ProfilePath))
                        {
                            step.Status = "done";
                            step.StatusNote = "pcb-rules-profile.json exists. Re-run Setup if nets changed.";
                        }
                        else
                        {
                            step.Status = "todo";
                            step.StatusNote = "Run Setup Net Classes & Rules.";
                        }
                        break;
                    }
                    case "place":
                    {
                        var plan = Path.Combine(AltiumEeDir, "placement_plan.json");
                        if (File.Exists(plan))
                        {
                            step.Status = "done";
                            step.StatusNote = "placement_plan.json found — still hand-check RF chain + connectors.";
                        }
                        else
                        {
                            step.Status = "todo";
                            step.StatusNote = "Run Auto-Place, then manually fix RF/antenna.";
                        }
                        break;
                    }
                    case "route":
                    {
                        try
                        {
                            var n = RoutingPriorityGuide.Build().Count;
                            if (n == 0)
                            {
                                step.Status = "todo";
                                step.StatusNote = "Classify nets first, then open Route Priority while routing.";
                            }
                            else
                            {
                                step.Status = "todo";
                                step.StatusNote = $"{n} nets queued RF→HS→PWR→Logic. Route in that order (status stays open until you finish).";
                            }
                        }
                        catch
                        {
                            step.Status = "todo";
                            step.StatusNote = "Open Route Priority after Setup Net Classes.";
                        }
                        break;
                    }
                    case "drc":
                    {
                        var path = PcbFullDrc.DefaultReportPath;
                        if (!File.Exists(path))
                            path = PcbClearanceDrc.DefaultReportPath;
                        if (!File.Exists(path))
                        {
                            step.Status = "todo";
                            step.StatusNote = "No DRC report yet — run Full PCB DRC.";
                            break;
                        }

                        try
                        {
                            var jo = JObject.Parse(File.ReadAllText(path));
                            var pass = jo.Value<bool?>("pass") == true;
                            var issues = jo.Value<int?>("issueCount")
                                         ?? jo.Value<int?>("violationCount")
                                         ?? 0;
                            if (pass)
                            {
                                step.Status = "done";
                                step.StatusNote = $"Last DRC pass ({issues} issues/warnings). Re-run after copper edits.";
                            }
                            else
                            {
                                step.Status = "warn";
                                step.StatusNote = $"Last DRC FAIL ({issues} issues). Jump/fix before Gerbers.";
                            }
                        }
                        catch
                        {
                            step.Status = "warn";
                            step.StatusNote = "DRC report present but unreadable.";
                        }
                        break;
                    }
                    case "export":
                    {
                        var conn = Path.Combine(AltiumEeDir, "connectivity.json");
                        if (File.Exists(conn))
                        {
                            var age = DateTime.Now - File.GetLastWriteTime(conn);
                            step.Status = age.TotalHours < 24 ? "done" : "warn";
                            step.StatusNote = age.TotalHours < 24
                                ? $"connectivity.json updated {File.GetLastWriteTime(conn):g}."
                                : "connectivity.json older than 24h — re-export.";
                        }
                        else
                        {
                            step.Status = "todo";
                            step.StatusNote = "Export from MCP panel.";
                        }
                        break;
                    }
                    case "erc":
                    case "planes":
                    case "stitch":
                        step.Status = "todo";
                        step.StatusNote = "Manual/Altium step — mark done mentally when finished.";
                        break;
                }
            }
            catch (Exception ex)
            {
                step.Status = "warn";
                step.StatusNote = ex.Message;
            }
        }

        private static string AltiumEeDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AltiumEE");
    }
}
