using System;

namespace EasyEDA_Loader
{
    public static class ConnectivityExporter
    {
        public static string DefaultExportPath => DesignExporter.DefaultExportPath;

        public static string ExportCurrentSheet(string outputPath = null) =>
            DesignExporter.ExportFullProject(outputPath);
    }
}
