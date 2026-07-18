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
        private Button createRoomsButton;
        private Button fanoutDecapButton;
        private Button floorplanPreviewButton;
        private Button smartPlaceButton;
        private Button resizeDesignatorsButton;
        private TextBox designatorHeightText;
        private TextBox designatorWidthText;
        private Button setupPcbRulesButton;
        private Button openPcbRulesProfileButton;
        private Button runClearanceDrcButton;
        private Button viaStitchButton;
        private Button designWorkflowButton;
        private Button stackupAdvisorButton;
        private Button boardNeedsButton;
        private Button routePriorityButton;
        private TextBlock pcbRulesStatusText;
        private TextBlock postRouteStatusText;
        private TextBlock workflowStatusText;
        private McpSettings settings;

        public McpControlWindow()
        {
            Title = "Altium MCP Control Panel";
            Height = 720;
            Width = 760;
            MinHeight = 520;
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
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0xA4, 0xAE)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Background = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 12),
                Child = new Grid
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "MCP server", FontWeight = FontWeights.SemiBold },
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
            floorplanPreviewButton = new Button
            {
                Content = "Floorplan Preview…",
                Width = 200,
                Height = 40,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Preview different board layouts (zones for connectors / power / MCU / RF). Choose auto size, mm size, or DXF outline, then Apply.",
            };
            floorplanPreviewButton.Click += FloorplanPreviewButton_Click;
            clusterAllIcPartsButton = new Button { Content = "Auto-Place All Components", Width = 260, Height = 36, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold, ToolTip = "One click: plan for every IC, pin-accurate placement (decoupling caps on TOP for multilayer boards), keeps parts independently moveable, resizes designators." };
            clusterAllIcPartsButton.Click += ClusterAllIcPartsButton_Click;
            optimizeBoardButton = new Button { Content = "Optimize Board (Force + Anneal)", Width = 260, Height = 32, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Two-phase optimizer: force-directed HPWL springs, then simulated annealing that translates, rotates (±90°), and swaps passives to pack tighter with ~28 mil clearance. Locks connectors/ICs. Run after Auto-Place." };
            optimizeBoardButton.Click += OptimizeBoardButton_Click;
            clusterIcPartsButton = new Button { Content = "Place One IC", Width = 120, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            clusterIcPartsButton.Click += ClusterIcPartsButton_Click;
            placementStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            generatePlanButton = new Button { Content = "Generate Plan Only", Width = 140, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            generatePlanButton.Click += GeneratePlanButton_Click;
            applyPlanButton = new Button { Content = "Apply Plan Only", Width = 130, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            applyPlanButton.Click += ApplyPlanButton_Click;
            anchorClustersButton = new Button { Content = "Anchor Clusters (Union)", Width = 160, Height = 28, ToolTip = "Group each IC + its support parts into an Altium Union so you can drag the whole cluster as one unit. Re-run any time after a plan is applied." };
            anchorClustersButton.Click += AnchorClustersButton_Click;
            createRoomsButton = new Button
            {
                Content = "Create Rooms",
                Width = 120,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Create Altium Room Definition (confinement) rules around each IC cluster — how pros lock floorplan blocks.",
            };
            createRoomsButton.Click += CreateRoomsButton_Click;
            fanoutDecapButton = new Button
            {
                Content = "Fanout Decap Vias",
                Width = 140,
                Height = 28,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Place GND/power vias next to decoupling and IC power pads (short plane fanout).",
            };
            fanoutDecapButton.Click += FanoutDecapButton_Click;
            resizeDesignatorsButton = new Button { Content = "Resize Designators", Width = 140, Height = 28, ToolTip = "Shrink every component designator (R1, C2, ...) to a uniform small size so the board isn't cluttered." };
            resizeDesignatorsButton.Click += ResizeDesignatorsButton_Click;
            designatorHeightText = new TextBox { Text = "20", Width = 40, ToolTip = "Designator text height in mils" };
            designatorWidthText = new TextBox { Text = "5", Width = 40, ToolTip = "Designator stroke width in mils" };
            setupPcbRulesButton = new Button { Content = "Setup Net Classes & Rules", Width = 180, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Classify nets into RF / PWR / HighSpeed / Logic, preview assignments, then create Altium net classes and width/clearance rules." };
            setupPcbRulesButton.Click += SetupPcbRulesButton_Click;
            openPcbRulesProfileButton = new Button { Content = "Edit Rules Profile", Width = 140, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            openPcbRulesProfileButton.Click += OpenPcbRulesProfileButton_Click;
            pcbRulesStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            runClearanceDrcButton = new Button
            {
                Content = "Full PCB DRC (Errors)",
                Width = 180,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Runs Altium batch DRC + MCP checks (power clearance, pad↔track, neckdown/fab width, via↔pad). Opens an error list with Jump.",
            };
            runClearanceDrcButton.Click += RunClearanceDrcButton_Click;
            viaStitchButton = new Button
            {
                Content = "Stitch Vias (RF / Clocks)",
                Width = 200,
                Height = 34,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "After routing: place GND fence/stitch vias along RF and HighSpeed (clock) nets. Ctrl+Z to undo.",
            };
            viaStitchButton.Click += ViaStitchButton_Click;
            postRouteStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            designWorkflowButton = new Button
            {
                Content = "Expert Process Guide",
                Width = 170,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "How experts go schematic→fab: checklist, playbook, and RF→HS→PWR→Logic route priority.",
            };
            designWorkflowButton.Click += (_, __) =>
            {
                var w = new DesignWorkflowWindow { Owner = this };
                w.Show();
                RefreshWorkflowStatus();
            };
            stackupAdvisorButton = new Button
            {
                Content = "Stackup Advisor (JLCPCB)",
                Width = 190,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Pick JLCPCB 2/4/6L stackup, copy .stackupx files, check current PCB layer count.",
            };
            stackupAdvisorButton.Click += (_, __) =>
            {
                var w = new StackupAdvisorWindow { Owner = this };
                w.Show();
                RefreshWorkflowStatus();
            };
            boardNeedsButton = new Button
            {
                Content = "Board Needs",
                Width = 120,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "From MCU/RF/USB/power: recommend layers/stackup, impedance, via sizes, and heat actions.",
            };
            boardNeedsButton.Click += (_, __) =>
            {
                new BoardNeedsWindow { Owner = this }.Show();
            };
            routePriorityButton = new Button
            {
                Content = "Route Priority",
                Width = 120,
                Height = 34,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Live net list in expert order: RF → HighSpeed → PWR → Logic. Keep open while Interactive Routing.",
            };
            routePriorityButton.Click += (_, __) =>
            {
                new RoutingPriorityWindow { Owner = this }.Show();
            };
            workflowStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.9 };

            smartPlaceButton = new Button
            {
                Content = "▶  Smart Place (recommended)",
                Height = 44,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Research-backed pipeline: Board Needs → best floorplan (wirelength score) → pin-accurate Auto-Place → Fanout → Rooms. Like Altium rooms + modern force/SA placers.",
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x5E, 0x20)),
                Foreground = System.Windows.Media.Brushes.White,
            };
            smartPlaceButton.Click += SmartPlaceButton_Click;

            // Resize primary tools for card layout
            floorplanPreviewButton.Width = double.NaN;
            floorplanPreviewButton.Height = 34;
            floorplanPreviewButton.Margin = new Thickness(0, 0, 8, 0);
            floorplanPreviewButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            clusterAllIcPartsButton.Width = double.NaN;
            clusterAllIcPartsButton.Height = 34;
            clusterAllIcPartsButton.Margin = new Thickness(0, 0, 8, 0);
            optimizeBoardButton.Width = double.NaN;
            optimizeBoardButton.Height = 34;
            optimizeBoardButton.Margin = new Thickness(0, 0, 0, 0);
            fanoutDecapButton.Width = 150;
            fanoutDecapButton.Height = 32;
            fanoutDecapButton.Margin = new Thickness(0, 0, 8, 0);
            createRoomsButton.Width = 130;
            createRoomsButton.Height = 32;
            setupPcbRulesButton.Width = 180;
            setupPcbRulesButton.Height = 30;
            boardNeedsButton.Width = 110;
            boardNeedsButton.Height = 30;
            boardNeedsButton.Margin = new Thickness(0, 0, 8, 0);
            stackupAdvisorButton.Width = 160;
            stackupAdvisorButton.Height = 30;
            stackupAdvisorButton.Margin = new Thickness(0, 0, 8, 0);
            designWorkflowButton.Width = 150;
            designWorkflowButton.Height = 30;
            designWorkflowButton.Margin = new Thickness(0, 0, 8, 0);
            routePriorityButton.Width = 120;
            routePriorityButton.Height = 30;
            routePriorityButton.Margin = new Thickness(0, 0, 0, 0);

            var bodyPanel = new StackPanel();

            // STEP 1 — Prep
            var prepLinks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            prepLinks.Children.Add(boardNeedsButton);
            prepLinks.Children.Add(stackupAdvisorButton);
            prepLinks.Children.Add(setupPcbRulesButton);
            prepLinks.Children.Add(openPcbRulesProfileButton);
            var prepExtra = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            prepExtra.Children.Add(designWorkflowButton);
            prepExtra.Children.Add(routePriorityButton);
            var prepStack = new StackPanel();
            prepStack.Children.Add(prepLinks);
            prepStack.Children.Add(prepExtra);
            prepStack.Children.Add(workflowStatusText);
            bodyPanel.Children.Add(MakeStepCard(
                "1", "Prep — stackup & rules",
                "Pros fix fab stackup and net classes before moving parts. Board Needs reads MCU/RF/USB/power.",
                prepStack));

            // STEP 2 — Place (hero)
            var placeStack = new StackPanel();
            placeStack.Children.Add(smartPlaceButton);
            placeStack.Children.Add(new TextBlock
            {
                Text = "Or step through manually (same order Altium rooms / academic placers use):",
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });
            var placeRow1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            placeRow1.Children.Add(floorplanPreviewButton);
            placeRow1.Children.Add(clusterAllIcPartsButton);
            placeRow1.Children.Add(optimizeBoardButton);
            placeStack.Children.Add(placeRow1);
            var placeRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            placeRow2.Children.Add(fanoutDecapButton);
            placeRow2.Children.Add(createRoomsButton);
            placeRow2.Children.Add(clusterIcPartsButton);
            placeStack.Children.Add(placeRow2);
            placeStack.Children.Add(placementStatusText);
            placeStack.Children.Add(pcbRulesStatusText);
            bodyPanel.Children.Add(MakeStepCard(
                "2", "Place — floorplan → pins → fanout → rooms",
                "Smart Place runs the full chain. Floorplan Preview lets you pick among scored layouts (★ = shortest estimated nets).",
                placeStack));

            // Options / power user
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
            var planButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            planButtons.Children.Add(generatePlanButton);
            planButtons.Children.Add(applyPlanButton);
            planButtons.Children.Add(anchorClustersButton);
            optionsPanel.Children.Add(planButtons);
            bodyPanel.Children.Add(MakeExpander("Placement options & plan tools", optionsPanel, isExpanded: false));

            // STEP 3 — Verify
            var verifyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            verifyRow.Children.Add(runClearanceDrcButton);
            verifyRow.Children.Add(viaStitchButton);
            var verifyStack = new StackPanel();
            verifyStack.Children.Add(verifyRow);
            verifyStack.Children.Add(postRouteStatusText);
            bodyPanel.Children.Add(MakeStepCard(
                "3", "Verify — DRC & stitch",
                "After routing: Full DRC (Altium + MCP extras). Stitch GND along RF/clocks.",
                verifyStack));

            // MCP / export (collapsed)
            var connectPanel = new StackPanel();
            connectPanel.Children.Add(MakeHint("Auto-exports when this panel opens. Used by Cursor MCP tools."));
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
            bodyPanel.Children.Add(MakeExpander("MCP export & connection", connectPanel, isExpanded: false));

            var bodyBorder = new Border
            {
                Padding = new Thickness(14),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0xA4, 0xAE)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xFB, 0xFC)),
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
                Text = "Still nudge RF/antenna by hand. Smart Place ≈ Altium rooms + pin-accurate clusters.",
            });
            var closeButton = new Button { Content = "Close", Width = 80, Height = 28, IsCancel = true };
            closeButton.Click += CloseButton_Click;
            footer.Children.Add(closeButton);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            // Update header copy
            header.Children.Clear();
            header.Children.Add(new TextBlock { Text = "Altium MCP Placement", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "Professional flow in one panel: prep → Smart Place → verify. Built like Altium rooms + modern force/SA placers.",
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });

            Content = root;
        }

        private static Border MakeStepCard(string step, string title, string hint, UIElement content)
        {
            var stack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            titleRow.Children.Add(new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x37, 0x47, 0x4F)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = step,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                },
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(titleRow);
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 12,
            });
            stack.Children.Add(content);

            return new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCF, 0xD8, 0xDC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = stack,
            };
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
            RefreshWorkflowStatus();

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

        private void RefreshWorkflowStatus()
        {
            try
            {
                if (workflowStatusText == null)
                    return;
                var steps = DesignWorkflow.Evaluate();
                workflowStatusText.Text = DesignWorkflow.FormatSummary(steps);
            }
            catch (Exception ex)
            {
                if (workflowStatusText != null)
                    workflowStatusText.Text = "Workflow status unavailable: " + ex.Message;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!McpAutoExport.TryExportSilently(settings.ConnectivityFile, out var path))
                    throw new InvalidOperationException(path);

                var msg = $"Project data exported to:\n{path}";
                try
                {
                    if (File.Exists(PcbFullDrc.DefaultReportPath))
                    {
                        var reportJson = File.ReadAllText(PcbFullDrc.DefaultReportPath);
                        var report = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(reportJson);
                        if (report != null)
                            msg += "\n\n" + PcbFullDrc.FormatUserMessage(report);
                    }
                    else if (File.Exists(PcbClearanceDrc.DefaultReportPath))
                    {
                        var reportJson = File.ReadAllText(PcbClearanceDrc.DefaultReportPath);
                        var report = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(reportJson);
                        if (report != null)
                            msg += "\n\n" + PcbClearanceDrc.FormatUserMessage(report);
                    }
                }
                catch { }

                MessageBox.Show(this, msg, "Altium MCP", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void FloorplanPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new FloorplanPreviewWindow { Owner = this };
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Floorplan Preview", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SmartPlaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadPlacementInputs(out var spacingMils, out var maxRadiusMils, out var maxSchDistance, out _))
                return;

            RunClusterAction(
                smartPlaceButton,
                "Smart Place: needs → best floorplan → auto-place → fanout → rooms…",
                () => SmartPlacePipeline.Run(spacingMils, maxRadiusMils, maxSchDistance, optimizeAfter: false),
                "Altium MCP — Smart Place");
        }

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

        private void CreateRoomsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                createRoomsButton.IsEnabled = false;
                placementStatusText.Text = "Creating placement rooms from last plan...";
                PcbDocumentHelper.PumpUi();
                var message = PlacementRooms.CreateRoomsFromLastPlan(alsoAnchorUnions: true);
                placementStatusText.Text = message.Replace("\r\n", " ").Replace("\n", " ");
                MessageBox.Show(this, message, "Altium MCP - Rooms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP - Rooms", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                createRoomsButton.IsEnabled = true;
            }
        }

        private void FanoutDecapButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                fanoutDecapButton.IsEnabled = false;
                placementStatusText.Text = "Placing decap/power fanout vias...";
                PcbDocumentHelper.PumpUi();
                var message = DecapFanout.FanoutDecouplingAndPowerPads();
                placementStatusText.Text = message.Replace("\r\n", " ").Replace("\n", " ");
                MessageBox.Show(this, message, "Altium MCP - Fanout", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                placementStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP - Fanout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                fanoutDecapButton.IsEnabled = true;
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

        private void RunClearanceDrcButton_Click(object sender, RoutedEventArgs e)
        {
            runClearanceDrcButton.IsEnabled = false;
            postRouteStatusText.Text = "Running full PCB DRC (Altium + MCP extras)...";
            PcbDocumentHelper.PumpUi();
            try
            {
                var report = PcbFullDrc.RunAndShowResults(this);
                var summary = report.TryGetValue("summary", out var s) ? s?.ToString() : "DRC complete.";
                postRouteStatusText.Text = summary;
            }
            catch (Exception ex)
            {
                postRouteStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP Full PCB DRC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                runClearanceDrcButton.IsEnabled = true;
            }
        }

        private void ViaStitchButton_Click(object sender, RoutedEventArgs e)
        {
            viaStitchButton.IsEnabled = false;
            postRouteStatusText.Text = "Placing GND stitch vias along RF / clock routes...";
            PcbDocumentHelper.PumpUi();
            try
            {
                var message = ViaStitcher.StitchRfAndClocks();
                postRouteStatusText.Text = message.Replace("\r\n", " ").Replace("\n", " ");
                MessageBox.Show(
                    this,
                    message,
                    "Altium MCP Via Stitch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                postRouteStatusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Altium MCP Via Stitch", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                viaStitchButton.IsEnabled = true;
            }
        }
    }
}
