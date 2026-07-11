using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EasyEDA_Loader
{
    public partial class McpControlWindow : Window
    {
        private TextBlock serverStatusText;
        private TextBlock exportStatusText;
        private Button startServerButton;
        private Button stopServerButton;
        private TextBlock exportPathText;
        private TextBlock serverUrlText;
        private TextBox apiKeyText;
        private TextBlock pythonPathText;
        private TextBlock serverScriptText;
        private TextBlock summaryText;
        private Button exportButton;
        private Button openFolderButton;
        private Button openSettingsButton;
        private TextBox anchorIcText;
        private TextBox spacingMilsText;
        private TextBox maxRadiusText;
        private TextBox maxSchDistanceText;
        private Button clusterAllIcPartsButton;
        private Button optimizeBoardButton;
        private Button clusterIcPartsButton;
        private TextBlock placementStatusText;
        private Button generatePlanButton;
        private Button applyPlanButton;
        private Button anchorClustersButton;
        private Button resizeDesignatorsButton;
        private TextBox designatorHeightText;
        private TextBox designatorWidthText;
        private Button setupPcbRulesButton;
        private Button openPcbRulesProfileButton;
        private TextBlock pcbRulesStatusText;
        private McpSettings settings;

        public McpControlWindow()
        {
            Title = "Altium MCP Control Panel";
            Height = 620;
            Width = 760;
            MinHeight = 480;
            MinWidth = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
            Loaded += Window_Loaded;
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock { Text = "Altium MCP Placement", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "Place passives around ICs, optimize the board, set net classes, and export data for Cursor.",
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            serverStatusText = new TextBlock { Text = "Checking...", Margin = new Thickness(0, 6, 0, 0) };
            exportStatusText = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            startServerButton = new Button { Content = "Start Server", Width = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            startServerButton.Click += StartServerButton_Click;
            stopServerButton = new Button { Content = "Stop Server", Width = 110, Height = 28 };
            stopServerButton.Click += StopServerButton_Click;

            var statusBorder = new Border
            {
                Padding = new Thickness(12),
                BorderBrush = System.Windows.Media.Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new Grid
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "Server status", FontWeight = FontWeights.SemiBold },
                                serverStatusText,
                                exportStatusText,
                            },
                        },
                    },
                },
            };
            var statusGrid = (Grid)statusBorder.Child;
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition());
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttonRow.Children.Add(startServerButton);
            buttonRow.Children.Add(stopServerButton);
            Grid.SetColumn(buttonRow, 1);
            statusGrid.Children.Add(buttonRow);
            Grid.SetRow(statusBorder, 1);
            root.Children.Add(statusBorder);

            exportPathText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            serverUrlText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            apiKeyText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            pythonPathText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            serverScriptText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            summaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            exportButton = new Button { Content = "Export Now", Width = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            exportButton.Click += ExportButton_Click;
            openFolderButton = new Button { Content = "Open AltiumEE Folder", Width = 150, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            openFolderButton.Click += OpenFolderButton_Click;
            openSettingsButton = new Button { Content = "Open Settings", Width = 110, Height = 28 };
            openSettingsButton.Click += OpenSettingsButton_Click;
            anchorIcText = new TextBox { Text = "IC1", Margin = new Thickness(0, 0, 12, 0) };
            spacingMilsText = new TextBox { Text = "55" };
            maxRadiusText = new TextBox { Text = "900", Margin = new Thickness(0, 0, 12, 0) };
            maxSchDistanceText = new TextBox { Text = "2500" };
            clusterAllIcPartsButton = new Button { Content = "Auto-Place All Components", Width = 260, Height = 36, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold, ToolTip = "One click: generates the plan for every IC, applies pin-accurate placement (decoupling caps on bottom, chains in signal order), keeps every part independently moveable, and resizes designators." };
            clusterAllIcPartsButton.Click += ClusterAllIcPartsButton_Click;
            optimizeBoardButton = new Button { Content = "Optimize Board (Force + Anneal)", Width = 260, Height = 32, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Two-phase optimizer: force-directed HPWL springs, then simulated annealing that translates, rotates (±90°), and swaps passives to pack tighter with ~28 mil clearance. Locks connectors/ICs. Run after Auto-Place." };
            optimizeBoardButton.Click += OptimizeBoardButton_Click;
            clusterIcPartsButton = new Button { Content = "Place One IC", Width = 220, Height = 32, Margin = new Thickness(0, 0, 0, 10) };
            clusterIcPartsButton.Click += ClusterIcPartsButton_Click;
            placementStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            generatePlanButton = new Button { Content = "Generate Plan Only", Width = 140, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            generatePlanButton.Click += GeneratePlanButton_Click;
            applyPlanButton = new Button { Content = "Apply Plan Only", Width = 130, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            applyPlanButton.Click += ApplyPlanButton_Click;
            anchorClustersButton = new Button { Content = "Anchor Clusters (Union)", Width = 160, Height = 28, ToolTip = "Group each IC + its support parts into an Altium Union so you can drag the whole cluster as one unit. Re-run any time after a plan is applied." };
            anchorClustersButton.Click += AnchorClustersButton_Click;
            resizeDesignatorsButton = new Button { Content = "Resize Designators", Width = 140, Height = 28, ToolTip = "Shrink every component designator (R1, C2, ...) to a uniform small size so the board isn't cluttered." };
            resizeDesignatorsButton.Click += ResizeDesignatorsButton_Click;
            designatorHeightText = new TextBox { Text = "20", Width = 40, ToolTip = "Designator text height in mils" };
            designatorWidthText = new TextBox { Text = "5", Width = 40, ToolTip = "Designator stroke width in mils" };
            setupPcbRulesButton = new Button { Content = "Setup Net Classes & Rules", Width = 180, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Classify nets into RF / PWR / HighSpeed / Logic, preview assignments, then create Altium net classes and width/clearance rules." };
            setupPcbRulesButton.Click += SetupPcbRulesButton_Click;
            openPcbRulesProfileButton = new Button { Content = "Edit Rules Profile", Width = 140, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            openPcbRulesProfileButton.Click += OpenPcbRulesProfileButton_Click;
            pcbRulesStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

            var bodyPanel = new StackPanel();

            // --- Workflow: Placement (primary) ---
            bodyPanel.Children.Add(MakeSectionTitle("1. Place components"));
            bodyPanel.Children.Add(MakeHint(
                "Recommended: Auto-Place first (IC clusters + rotation). Then Optimize Board (annealing + 90° turns) for denser packing. Ctrl+Z undoes."));

            var primaryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            clusterAllIcPartsButton.Width = 240;
            clusterAllIcPartsButton.Height = 40;
            clusterAllIcPartsButton.Margin = new Thickness(0, 0, 10, 0);
            optimizeBoardButton.Width = 240;
            optimizeBoardButton.Height = 40;
            optimizeBoardButton.Margin = new Thickness(0, 0, 0, 0);
            primaryRow.Children.Add(clusterAllIcPartsButton);
            primaryRow.Children.Add(optimizeBoardButton);
            bodyPanel.Children.Add(primaryRow);

            var secondaryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            clusterIcPartsButton.Width = 140;
            clusterIcPartsButton.Height = 30;
            clusterIcPartsButton.Margin = new Thickness(0, 0, 8, 0);
            setupPcbRulesButton.Width = 180;
            setupPcbRulesButton.Height = 30;
            setupPcbRulesButton.Margin = new Thickness(0, 0, 8, 0);
            secondaryRow.Children.Add(clusterIcPartsButton);
            secondaryRow.Children.Add(setupPcbRulesButton);
            secondaryRow.Children.Add(openPcbRulesProfileButton);
            bodyPanel.Children.Add(secondaryRow);
            bodyPanel.Children.Add(placementStatusText);
            bodyPanel.Children.Add(pcbRulesStatusText);

            // --- Options expander ---
            var optionsPanel = new StackPanel();
            optionsPanel.Children.Add(CreatePlacementRow("Anchor IC", anchorIcText, "Spacing (mils)", spacingMilsText));
            optionsPanel.Children.Add(CreatePlacementRow("Max radius", maxRadiusText, "Sch distance", maxSchDistanceText));
            var resizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            resizeRow.Children.Add(resizeDesignatorsButton);
            resizeRow.Children.Add(new TextBlock { Text = " H:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) });
            resizeRow.Children.Add(designatorHeightText);
            resizeRow.Children.Add(new TextBlock { Text = "mil  W:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
            resizeRow.Children.Add(designatorWidthText);
            resizeRow.Children.Add(new TextBlock { Text = "mil", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
            optionsPanel.Children.Add(resizeRow);
            bodyPanel.Children.Add(MakeExpander("Placement options", optionsPanel, isExpanded: false));

            // --- Advanced expander ---
            var advancedPanel = new StackPanel();
            advancedPanel.Children.Add(MakeHint("Manual steps for debugging. Prefer Auto-Place / Optimize above."));
            var planButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            planButtons.Children.Add(generatePlanButton);
            planButtons.Children.Add(applyPlanButton);
            planButtons.Children.Add(anchorClustersButton);
            advancedPanel.Children.Add(planButtons);
            bodyPanel.Children.Add(MakeExpander("Advanced (manual steps)", advancedPanel, isExpanded: false));

            // --- Connection / export expander ---
            var connectPanel = new StackPanel();
            connectPanel.Children.Add(MakeHint("Opens with the panel. Cursor uses this export for MCP tools."));
            connectPanel.Children.Add(CreateFieldGrid("Export file", exportPathText));
            connectPanel.Children.Add(CreateFieldGrid("MCP URL", serverUrlText, 8));
            connectPanel.Children.Add(CreateFieldGrid("API key", apiKeyText, 8));
            connectPanel.Children.Add(CreateFieldGrid("Python", pythonPathText, 8));
            connectPanel.Children.Add(CreateFieldGrid("Server script", serverScriptText, 8));
            connectPanel.Children.Add(summaryText);
            var exportButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            exportButtons.Children.Add(exportButton);
            exportButtons.Children.Add(openFolderButton);
            exportButtons.Children.Add(openSettingsButton);
            connectPanel.Children.Add(exportButtons);
            bodyPanel.Children.Add(MakeExpander("2. Export & MCP connection", connectPanel, isExpanded: false));

            var bodyBorder = new Border
            {
                Padding = new Thickness(14),
                BorderBrush = System.Windows.Media.Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = bodyPanel },
            };
            Grid.SetRow(bodyBorder, 2);
            root.Children.Add(bodyBorder);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.75,
                Text = "JLCPCB parts: use EasyEDA Component Loader. Placement: Auto-Place → Optimize.",
            });
            var closeButton = new Button { Content = "Close", Width = 80, Height = 28, IsCancel = true };
            closeButton.Click += CloseButton_Click;
            footer.Children.Add(closeButton);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
        }

        private static TextBlock MakeSectionTitle(string text) =>
            new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6),
            };

        private static TextBlock MakeHint(string text) =>
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                Margin = new Thickness(0, 0, 0, 10),
            };

        private static Expander MakeExpander(string header, UIElement content, bool isExpanded)
        {
            return new Expander
            {
                Header = header,
                IsExpanded = isExpanded,
                Margin = new Thickness(0, 10, 0, 0),
                Content = new Border
                {
                    Padding = new Thickness(8, 8, 0, 4),
                    Child = content,
                },
            };
        }

        private static Grid CreateFieldGrid(string label, FrameworkElement value, double topMargin = 0)
        {
            var grid = new Grid { Margin = new Thickness(0, topMargin, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(value);
            return grid;
        }

        private static Grid CreatePlacementRow(string label1, TextBox box1, string label2, TextBox box2)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.Children.Add(new TextBlock { Text = label1, VerticalAlignment = VerticalAlignment.Center });
            box1.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(box1, 1);
            grid.Children.Add(box1);
            var labelBlock2 = new TextBlock { Text = label2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(labelBlock2, 2);
            grid.Children.Add(labelBlock2);
            Grid.SetColumn(box2, 3);
            grid.Children.Add(box2);
            return grid;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            settings = McpServerManager.LoadSettings();
            McpAutoExport.ExportOnPanelOpen();
            // Start the MCP server (after a short delay) and schedule background
            // connectivity re-exports. Without this call, auto-start was dead code.
            try { McpAutoExport.Initialize(AltiumApi.GlobalVars.Client); } catch { }
            RefreshUi();
        }

        private void RefreshUi()
        {
            settings = McpServerManager.LoadSettings();
            var running = McpServerManager.IsRunning();

            serverStatusText.Text = running ? "Running" : "Stopped";
            serverStatusText.Foreground = running
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.OrangeRed;

            startServerButton.IsEnabled = !running;
            startServerButton.Content = running ? "Restart Server" : "Start Server";
            stopServerButton.IsEnabled = running;

            exportPathText.Text = settings.ConnectivityFile ?? DesignExporter.DefaultExportPath;
            serverUrlText.Text = $"http://{settings.Host}:{settings.Port}/mcp";
            apiKeyText.Text = settings.ApiKey ?? string.Empty;
            pythonPathText.Text = settings.PythonPath ?? string.Empty;
            serverScriptText.Text = settings.ServerScriptPath ?? string.Empty;

            if (File.Exists(settings.ConnectivityFile))
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(settings.ConnectivityFile));
                    var summary = root["summary"];
                    if (summary != null)
                    {
                        exportStatusText.Text =
                            $"Last export: {root.Value<string>("exportedAt")} | " +
                            $"Sheets: {summary.Value<int?>("sheetCount") ?? 0} | " +
                            $"SCH parts: {summary.Value<int?>("schComponentCount") ?? 0} | " +
                            $"PCB parts: {summary.Value<int?>("pcbComponentCount") ?? 0}";
                    }
                    else
                    {
                        exportStatusText.Text = $"Last export file updated: {File.GetLastWriteTime(settings.ConnectivityFile)}";
                    }
                }
                catch
                {
                    exportStatusText.Text = $"Export file exists: {settings.ConnectivityFile}";
                }
            }
            else
            {
                exportStatusText.Text = "Waiting for automatic export. Open a project if none is focused.";
            }

            summaryText.Text =
                "Automatic mode: opening this panel exports the project and starts the MCP server. " +
                "While the server runs, exports refresh about every 45 seconds when the project changes. " +
                "Cursor can query connectivity without manual export clicks.";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!McpAutoExport.TryExportSilently(settings.ConnectivityFile, out var path))
                    throw new InvalidOperationException(path);

                MessageBox.Show(this, $"Project data exported to:\n{path}", "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = McpServerManager.StartServer();
                MessageBox.Show(this, message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var message = McpServerManager.StopServer();
                MessageBox.Show(this, message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(McpServerManager.SettingsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = McpServerManager.SettingsDirectory,
                UseShellExecute = true,
            });
        }

        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            McpServerManager.SaveSettings(settings);
            if (File.Exists(McpServerManager.SettingsPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = McpServerManager.SettingsPath,
                    UseShellExecute = true,
                });
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ClusterAllIcPartsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadPlacementInputs(out var spacingMils, out var maxRadiusMils, out var maxSchDistance, out _))
                return;

            if (!double.TryParse(designatorHeightText?.Text, out var desH)) desH = 20.0;
            if (!double.TryParse(designatorWidthText?.Text, out var desW)) desW = 5.0;

            RunClusterAction(
                clusterAllIcPartsButton,
                "Auto-placing all components: plan + deconflict + apply + resize designators...",
                () => IcClusterRunner.RunFullAutoPlacement(spacingMils, maxRadiusMils, maxSchDistance, desH, desW),
                "Altium MCP Placement - Auto-Place All");
        }

        private void OptimizeBoardButton_Click(object sender, RoutedEventArgs e)
        {
            RunClusterAction(
                optimizeBoardButton,
                "Running force-directed + annealing optimizer (HPWL, rotation, packing)...",
                () => IcClusterRunner.RunForceDirectedOptimize(),
                "Altium MCP Placement - Optimize Board");
        }

        private void ClusterIcPartsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadPlacementInputs(out var spacingMils, out var maxRadiusMils, out var maxSchDistance, out var anchor))
                return;

            RunClusterAction(
                clusterIcPartsButton,
                $"Placing passives near {anchor} on the PCB...",
                () => IcClusterRunner.RunOneClick(anchor, spacingMils, maxRadiusMils, maxSchDistance),
                "Altium MCP Placement - Place Passives");
        }

        private bool TryReadPlacementInputs(
            out double spacingMils,
            out double maxRadiusMils,
            out double maxSchDistance,
            out string anchor)
        {
            anchor = (anchorIcText.Text ?? string.Empty).Trim();
            if (!double.TryParse(spacingMilsText.Text, out spacingMils)) spacingMils = 55.0;
            if (!double.TryParse(maxRadiusText.Text, out maxRadiusMils)) maxRadiusMils = 900.0;
            if (!double.TryParse(maxSchDistanceText.Text, out maxSchDistance)) maxSchDistance = 2500.0;
            return true;
        }

        private void RunClusterAction(
            Button triggerButton,
            string workingMessage,
            Func<string> action,
            string resultTitle)
        {
            triggerButton.IsEnabled = false;
            placementStatusText.Text = workingMessage;
            PcbDocumentHelper.PumpUi();

            try
            {
                var summary = action();
                placementStatusText.Text = summary;
                MessageBox.Show(
                    this,
                    summary + "\n\nPress Ctrl+Z in Altium to undo the placement batch.",
                    resultTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, resultTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                triggerButton.IsEnabled = true;
            }
        }

        private void GeneratePlanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var anchor = (anchorIcText.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(anchor))
                    throw new InvalidOperationException("Enter an anchor IC designator (e.g. IC1).");

                if (!double.TryParse(spacingMilsText.Text, out var spacingMils)) spacingMils = 55.0;
                if (!double.TryParse(maxRadiusText.Text, out var maxRadiusMils)) maxRadiusMils = 900.0;
                if (!double.TryParse(maxSchDistanceText.Text, out var maxSchDistance)) maxSchDistance = 2500.0;

                var message = PlacementPlanGenerator.Generate(anchor, spacingMils, maxRadiusMils, maxSchDistance);
                placementStatusText.Text = message;
                MessageBox.Show(
                    this,
                    message + "\n\nClick Apply Plan Only to move parts on the PCB.",
                    "Altium MCP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyPlanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PcbDocumentHelper.EnsureProjectPcbBoard();
                var message = PlacementPlanApplier.ApplyPlan();
                placementStatusText.Text = message;
                MessageBox.Show(
                    this,
                    message + "\n\nPress Ctrl+Z in Altium to undo the placement batch.",
                    "Altium MCP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AnchorClustersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PcbDocumentHelper.EnsureProjectPcbBoard();
                int count = PlacementPlanApplier.AnchorClustersFromPlan();
                var message = count > 0
                    ? $"Anchored {count} cluster(s) as Unions. Drag any part in a cluster to move the whole group together."
                    : "No clusters to anchor. Generate + Apply a plan first.";
                placementStatusText.Text = message;
                MessageBox.Show(this, message, "Altium MCP - Anchor Clusters", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP - Anchor Clusters", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResizeDesignatorsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PcbDocumentHelper.EnsureProjectPcbBoard();
                if (!double.TryParse(designatorHeightText.Text, out double h)) h = 20.0;
                if (!double.TryParse(designatorWidthText.Text, out double w)) w = 5.0;
                var message = PlacementPlanApplier.ResizeAllDesignators(h, w);
                placementStatusText.Text = message.Replace("\n", " ");
                MessageBox.Show(this, message, "Altium MCP - Resize Designators", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP - Resize Designators", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetupPcbRulesButton_Click(object sender, RoutedEventArgs e)
        {
            setupPcbRulesButton.IsEnabled = false;
            pcbRulesStatusText.Text = "Classifying nets...";
            PcbDocumentHelper.PumpUi();
            try
            {
                var classified = PcbDesignRulesSetup.Classify(useConnectivityHints: true);

                var preview = new NetClassPreviewWindow(
                    classified.NetClassAssignments,
                    PcbDesignRulesSetup.ManagedNetClassOrder);
                preview.Owner = this;
                if (preview.ShowDialog() != true || preview.ResultAssignments == null)
                {
                    pcbRulesStatusText.Text = "Cancelled. No changes applied.";
                    return;
                }

                pcbRulesStatusText.Text = "Applying net classes and design rules...";
                PcbDocumentHelper.PumpUi();

                var result = PcbDesignRulesSetup.Apply(preview.ResultAssignments);
                pcbRulesStatusText.Text = result.Summary.Replace("\r\n", " ").Replace("\n", " ");
                MessageBox.Show(
                    this,
                    result.Summary + "\n\nPress Ctrl+Z in Altium to undo this rules batch.",
                    "Altium MCP - PCB Rules",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                pcbRulesStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP - PCB Rules", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                setupPcbRulesButton.IsEnabled = true;
            }
        }

        private void OpenPcbRulesProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PcbRulesProfile.LoadOrCreateDefault();
                Process.Start(new ProcessStartInfo
                {
                    FileName = PcbRulesProfile.ProfilePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Altium MCP - PCB Rules", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
