using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyEDA_Loader.Placement
{
    public class PlacementPlannerService
    {
        public JObject BuildIcPlacementPlan(
            JObject data,
            string icDesignator,
            double spacingMils = 80.0,
            double maxRadiusMils = 900.0,
            double schematicScale = 0.12,
            double maxSchematicDistanceMils = 2500.0,
            string layoutMode = "pin_accurate",
            bool sameSheetOnly = true,
            bool excludeGlobalNets = true)
        {
            ValidateConnectivitySchema(data);
            return PlacementPlanBuilder.BuildIcPlacementPlan(
                data,
                icDesignator,
                spacingMils,
                layoutMode,
                maxRadiusMils,
                schematicScale,
                sameSheetOnly,
                maxSchematicDistanceMils,
                excludeGlobalNets);
        }

        public JObject BuildAllIcClusterPlan(
            JObject data,
            double spacingMils = 80.0,
            double maxRadiusMils = 900.0,
            double schematicScale = 0.12,
            double maxSchematicDistanceMils = 2500.0,
            string layoutMode = "pin_accurate",
            bool sameSheetOnly = true,
            bool excludeGlobalNets = true)
        {
            ValidateConnectivitySchema(data);
            return PlacementPlanBuilder.BuildAllIcClusterPlan(
                data,
                spacingMils,
                layoutMode,
                maxRadiusMils,
                schematicScale,
                sameSheetOnly,
                maxSchematicDistanceMils,
                excludeGlobalNets);
        }

        public void WritePlacementPlan(JObject plan, string path)
        {
            var targetPath = string.IsNullOrWhiteSpace(path)
                ? PlacementConstants.DefaultPlanPath
                : path;
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(targetPath, plan.ToString(Formatting.Indented));
        }

        public void ValidateConnectivitySchema(JObject data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var version = PlacementConstants.NormalizeSchemaVersion(data["schemaVersion"]);
            if (!version.HasValue)
                throw new InvalidOperationException("Connectivity export is missing schemaVersion.");

            if (!PlacementConstants.SupportedConnectivitySchemaVersions.Contains(version.Value))
            {
                var supported = string.Join(
                    ", ",
                    PlacementConstants.SupportedConnectivitySchemaVersions
                        .OrderBy(v => v)
                        .Select(v => v.ToString(CultureInfo.InvariantCulture)));
                throw new InvalidOperationException(
                    $"Unsupported connectivity schemaVersion {data["schemaVersion"]}. Supported versions: {supported}.");
            }
        }
    }
}
