using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace EasyEDA_Loader
{
    public static class McpServerManager
    {
        public static string SettingsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AltiumEE");

        public static string SettingsPath => Path.Combine(SettingsDirectory, "mcp-settings.json");
        public static string PidPath => Path.Combine(SettingsDirectory, "mcp-server.pid");
        public static string LogPath => Path.Combine(SettingsDirectory, "mcp-server.log");

        public static McpSettings LoadSettings()
        {
            Directory.CreateDirectory(SettingsDirectory);
            if (!File.Exists(SettingsPath))
            {
                var defaults = McpSettings.CreateDefaults();
                SaveSettings(defaults);
                return defaults;
            }

            return JsonConvert.DeserializeObject<McpSettings>(File.ReadAllText(SettingsPath)) ?? McpSettings.CreateDefaults();
        }

        public static void SaveSettings(McpSettings settings)
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }

        public static bool IsRunning()
        {
            if (!File.Exists(PidPath))
                return false;

            if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out var pid))
                return false;

            try
            {
                var process = Process.GetProcessById(pid);
                return process != null && !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public static string StartServer()
        {
            if (IsRunning())
                return "MCP server is already running.";

            McpAutoExport.ExportBeforeServerStart();

            var settings = LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.PythonPath) || !File.Exists(settings.PythonPath))
                throw new InvalidOperationException("Python was not found. Set pythonPath in mcp-settings.json.");

            if (string.IsNullOrWhiteSpace(settings.ServerScriptPath) || !File.Exists(settings.ServerScriptPath))
                throw new InvalidOperationException("MCP server script was not found. Set serverScriptPath in mcp-settings.json.");

            Directory.CreateDirectory(SettingsDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.PythonPath,
                Arguments = $"\"{settings.ServerScriptPath}\"",
                WorkingDirectory = Path.GetDirectoryName(settings.ServerScriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.EnvironmentVariables["MCP_TRANSPORT"] = settings.Transport ?? "streamable-http";
            startInfo.EnvironmentVariables["MCP_HOST"] = settings.Host ?? "127.0.0.1";
            startInfo.EnvironmentVariables["MCP_PORT"] = (settings.Port > 0 ? settings.Port : 8787).ToString();
            startInfo.EnvironmentVariables["MCP_PUBLIC_URL"] = settings.PublicUrl ?? $"http://127.0.0.1:{settings.Port}";
            startInfo.EnvironmentVariables["MCP_API_KEY"] = settings.ApiKey ?? string.Empty;
            startInfo.EnvironmentVariables["ALTIUM_CONNECTIVITY_FILE"] = settings.ConnectivityFile ?? DesignExporter.DefaultExportPath;

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendLog(e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(e.Data);

            if (!process.Start())
                throw new InvalidOperationException("Failed to start MCP server process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            File.WriteAllText(PidPath, process.Id.ToString());
            return $"MCP server started (PID {process.Id}) on http://{settings.Host}:{settings.Port}/mcp. Project data was exported automatically.";
        }

        public static string StopServer()
        {
            if (!File.Exists(PidPath))
                return "MCP server is not running.";

            if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out var pid))
            {
                File.Delete(PidPath);
                return "Removed stale MCP server PID file.";
            }

            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Process already exited.
            }

            File.Delete(PidPath);
            return "MCP server stopped.";
        }

        private static void AppendLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {line}{Environment.NewLine}");
        }

        public static void AppendExportLog(string line) => AppendLog(line);
    }

    public class McpSettings
    {
        public string PythonPath { get; set; }
        public string ServerScriptPath { get; set; }
        public string ApiKey { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string PublicUrl { get; set; }
        public string Transport { get; set; }
        public string ConnectivityFile { get; set; }
        public bool AutoExportBeforeStart { get; set; } = true;
        public bool AutoExportOnPanelOpen { get; set; } = true;
        public bool AutoExportWhileRunning { get; set; } = true;
        public bool AutoExportOnlyWhenServerRunning { get; set; } = true;
        public int AutoExportIntervalSeconds { get; set; } = 45;
        public bool AutoStartServerOnPanelOpen { get; set; } = true;

        public static McpSettings CreateDefaults()
        {
            var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AltiumEE");
            var repoRoot = FindRepoRoot();
            return new McpSettings
            {
                PythonPath = FindPythonPath(),
                ServerScriptPath = repoRoot == null ? string.Empty : Path.Combine(repoRoot, "altium-mcp", "server.py"),
                ApiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                Host = "127.0.0.1",
                Port = 8787,
                PublicUrl = "http://127.0.0.1:8787",
                Transport = "streamable-http",
                ConnectivityFile = Path.Combine(documents, "connectivity.json"),
                AutoExportBeforeStart = true,
                AutoExportOnPanelOpen = true,
                AutoExportWhileRunning = true,
                AutoExportOnlyWhenServerRunning = true,
                AutoExportIntervalSeconds = 45,
                AutoStartServerOnPanelOpen = true,
            };
        }

        private static string FindPythonPath()
        {
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
                @"C:\Python313\python.exe",
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "python";
        }

        private static string FindRepoRoot()
        {
            var probe = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(probe, "altium-mcp", "server.py");
                if (File.Exists(candidate))
                    return probe;

                var parent = Directory.GetParent(probe);
                if (parent == null)
                    break;
                probe = parent.FullName;
            }

            return null;
        }
    }
}
