using System;
using System.IO;
using System.Linq;
using EasyEDA_Loader.Placement;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlacementPlannerParity
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var inputPath = args.Length > 0
                    ? args[0]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "altium-mcp", "sample", "connectivity.json");
                inputPath = Path.GetFullPath(inputPath);
                if (!File.Exists(inputPath))
                {
                    Console.Error.WriteLine("Input JSON not found: " + inputPath);
                    return 2;
                }

                var mode = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "all";
                var anchor = args.Length > 2 ? args[2].Trim() : "IC1";
                var data = JObject.Parse(File.ReadAllText(inputPath));
                var planner = new PlacementPlannerService();
                planner.ValidateConnectivitySchema(data);

                JObject plan;
                if (mode == "ic")
                    plan = planner.BuildIcPlacementPlan(data, anchor);
                else
                    plan = planner.BuildAllIcClusterPlan(data);

                var outputPath = args.Length > 3 ? args[3] : null;
                if (!string.IsNullOrWhiteSpace(outputPath))
                    planner.WritePlacementPlan(plan, outputPath);

                var summary = new JObject
                {
                    ["found"] = plan["found"],
                    ["error"] = plan["error"],
                    ["anchor"] = plan["anchor"],
                    ["anchors"] = plan["anchors"],
                    ["cluster_count"] = plan["cluster_count"],
                    ["move_count"] = plan["move_count"],
                    ["schemaVersion"] = plan["schemaVersion"],
                    ["layoutMode"] = plan["layoutMode"],
                    ["methods"] = new JArray(
                        (plan["moves"] as JArray ?? new JArray())
                            .Select(m => m["method"])
                            .Where(m => m != null)
                            .GroupBy(m => m.ToString())
                            .Select(g => new JObject { ["method"] = g.Key, ["count"] = g.Count() })),
                };
                Console.WriteLine(summary.ToString(Formatting.Indented));
                return plan.Value<bool?>("found") == true ? 0 : 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
