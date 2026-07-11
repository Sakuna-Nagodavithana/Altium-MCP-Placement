using DXP;
using EDP;
using System;
using System.IO;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    public static class McpAutoExport
    {
        private static readonly object Gate = new object();
        private static Timer refreshTimer;
        private static Timer startupTimer;

        public static void Initialize(IClient altiumClient)
        {
            var settings = McpServerManager.LoadSettings();
            ScheduleStartup(settings);
            ScheduleRefresh(settings);
        }

        private static void ScheduleStartup(McpSettings settings)
        {
            if (!settings.AutoStartServerOnPanelOpen)
                return;

            startupTimer?.Stop();
            startupTimer?.Dispose();
            startupTimer = new Timer { Interval = 3000 };
            startupTimer.Tick += (_, __) =>
            {
                startupTimer.Stop();
                TryStartupSequence(settings);
            };
            startupTimer.Start();
        }

        private static void TryStartupSequence(McpSettings settings)
        {
            if (McpServerManager.IsRunning())
                return;

            if (settings.AutoExportOnPanelOpen)
                TryExportSilently(settings.ConnectivityFile, out _);

            try
            {
                McpServerManager.StartServer();
            }
            catch (Exception ex)
            {
                McpServerManager.AppendExportLog($"Automatic MCP startup failed: {ex.Message}");
            }
        }

        private static void ScheduleRefresh(McpSettings settings)
        {
            if (!settings.AutoExportWhileRunning)
                return;

            var intervalSeconds = settings.AutoExportIntervalSeconds > 0
                ? settings.AutoExportIntervalSeconds
                : 45;

            refreshTimer?.Stop();
            refreshTimer?.Dispose();
            refreshTimer = new Timer { Interval = intervalSeconds * 1000 };
            refreshTimer.Tick += (_, __) => OnRefreshTimer();
            refreshTimer.Start();
        }

        public static string ExportForMcp(string outputPath = null)
        {
            outputPath ??= McpServerManager.LoadSettings().ConnectivityFile ?? DesignExporter.DefaultExportPath;
            return DesignExporter.ExportFullProject(outputPath);
        }

        public static bool TryExportSilently(string outputPath, out string message)
        {
            lock (Gate)
            {
                message = string.Empty;
                try
                {
                    if (!IsProjectAvailable())
                    {
                        message = "No focused Altium project is open.";
                        return false;
                    }

                    var path = ExportForMcp(outputPath);
                    message = path;
                    McpServerManager.AppendExportLog($"Exported project to {path}");
                    return true;
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    McpServerManager.AppendExportLog($"Export failed: {ex.Message}");
                    return false;
                }
            }
        }

        public static void ExportBeforeServerStart()
        {
            var settings = McpServerManager.LoadSettings();
            if (settings.AutoExportBeforeStart == false)
                return;

            if (!TryExportSilently(settings.ConnectivityFile, out var message))
                throw new InvalidOperationException($"Automatic export failed: {message}");
        }

        public static void ExportOnPanelOpen()
        {
            var settings = McpServerManager.LoadSettings();
            if (settings.AutoExportOnPanelOpen == false)
                return;

            TryExportSilently(settings.ConnectivityFile, out _);
        }

        private static void OnRefreshTimer()
        {
            try
            {
                var settings = McpServerManager.LoadSettings();
                if (!settings.AutoExportWhileRunning)
                    return;

                if (settings.AutoExportOnlyWhenServerRunning && !McpServerManager.IsRunning())
                    return;

                if (!IsProjectAvailable())
                    return;

                if (ShouldSkipRefresh(settings))
                    return;

                TryExportSilently(settings.ConnectivityFile, out _);
            }
            catch (Exception ex)
            {
                McpServerManager.AppendExportLog($"Background export failed: {ex.Message}");
            }
        }

        private static bool ShouldSkipRefresh(McpSettings settings)
        {
            var exportPath = settings.ConnectivityFile ?? DesignExporter.DefaultExportPath;
            if (!File.Exists(exportPath))
                return false;

            var exportTime = File.GetLastWriteTimeUtc(exportPath);
            var projectTime = GetFocusedProjectTimestampUtc();
            if (projectTime.HasValue && projectTime.Value <= exportTime.AddSeconds(1))
                return true;

            return false;
        }

        private static bool IsProjectAvailable()
        {
            try
            {
                var workspace = AltiumApi.GlobalVars.Workspace;
                return workspace?.Internal_DM_FocusedProject() is IProject;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime? GetFocusedProjectTimestampUtc()
        {
            try
            {
                var project = AltiumApi.GlobalVars.Workspace?.Internal_DM_FocusedProject() as IProject;
                var projectPath = project?.DM_ProjectFileName();
                if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
                    return null;

                return File.GetLastWriteTimeUtc(projectPath);
            }
            catch
            {
                return null;
            }
        }
    }
}
