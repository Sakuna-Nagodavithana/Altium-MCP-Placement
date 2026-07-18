using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Expert-aligned schematic→fab coach: checklist, how experts work, route priority.
    /// </summary>
    internal sealed class DesignWorkflowWindow : Window
    {
        public DesignWorkflowWindow()
        {
            Title = "Expert Design Process (Schematic → Fab)";
            Width = 900;
            Height = 680;
            MinWidth = 700;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
        }

        private void BuildUi()
        {
            var steps = DesignWorkflow.Evaluate();
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = DesignWorkflow.FormatSummary(steps),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            header.Children.Add(new TextBlock
            {
                Text = "Experts: schematic for layout → stackup/rules → floorplan → planes → route RF→HS→PWR→Logic → DRC → fab. The plugin automates the mechanical parts; you still own RF placement and interactive routing.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "1. Checklist", Content = BuildChecklistTab(steps) });
            tabs.Items.Add(new TabItem { Header = "2. How experts work", Content = BuildExpertTab() });
            tabs.Items.Add(new TabItem { Header = "3. Route priority", Content = BuildRouteTab() });
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            buttons.Children.Add(MakeButton("Stackup Advisor", 140, () =>
            {
                new StackupAdvisorWindow { Owner = this }.Show();
            }));
            buttons.Children.Add(MakeButton("Setup Rules", 110, RunSetupRules));
            buttons.Children.Add(MakeButton("Route Priority", 120, () =>
            {
                new RoutingPriorityWindow { Owner = this }.Show();
            }));
            buttons.Children.Add(MakeButton("Full DRC", 90, () =>
            {
                try { PcbFullDrc.RunAndShowResults(this); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Full DRC", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, bold: true));
            buttons.Children.Add(MakeButton("Refresh", 90, () =>
            {
                Close();
                new DesignWorkflowWindow { Owner = Owner }.Show();
            }));
            var close = new Button { Content = "Close", Width = 90, Height = 30, IsCancel = true, Margin = new Thickness(0, 0, 0, 0) };
            close.Click += (_, __) => Close();
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
        }

        private static Button MakeButton(string text, double width, Action click, bool bold = false)
        {
            var b = new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            b.Click += (_, __) => click();
            return b;
        }

        private void RunSetupRules()
        {
            try
            {
                var classify = PcbDesignRulesSetup.Classify(useConnectivityHints: true);
                var preview = new NetClassPreviewWindow(
                    classify.NetClassAssignments,
                    PcbDesignRulesSetup.ManagedNetClassOrder) { Owner = this };
                if (preview.ShowDialog() == true && preview.ResultAssignments != null)
                {
                    var applied = PcbDesignRulesSetup.Apply(preview.ResultAssignments);
                    MessageBox.Show(this, applied.Summary, "Net Classes & Rules", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Net Classes & Rules", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static ScrollViewer BuildChecklistTab(System.Collections.Generic.List<DesignWorkflow.Step> steps)
        {
            var list = new StackPanel();
            foreach (var step in steps)
            {
                var brush = step.Status == "done" ? Brushes.DarkGreen
                    : step.Status == "warn" ? Brushes.DarkOrange
                    : Brushes.DimGray;
                list.Children.Add(new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 8, 0, 8),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{step.Number}. {step.Title}  [{step.Status.ToUpperInvariant()}]",
                                FontWeight = FontWeights.SemiBold,
                                Foreground = brush,
                            },
                            new TextBlock { Text = step.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
                            new TextBlock
                            {
                                Text = "Expert: " + step.ExpertHow,
                                TextWrapping = TextWrapping.Wrap,
                                Opacity = 0.9,
                                Margin = new Thickness(0, 2, 0, 0),
                            },
                            new TextBlock
                            {
                                Text = "Action: " + step.PluginAction,
                                Opacity = 0.85,
                                Margin = new Thickness(0, 2, 0, 0),
                            },
                            new TextBlock
                            {
                                Text = step.StatusNote,
                                FontStyle = FontStyles.Italic,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 2, 0, 0),
                            },
                        },
                    },
                });
            }
            return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static ScrollViewer BuildExpertTab()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new TextBlock
            {
                Text = "How a human expert actually designs (not auto-route-everything)",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
            });
            foreach (var phase in ExpertDesignPlaybook.Phases)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = phase.Title,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2),
                    Foreground = Brushes.DarkSlateBlue,
                });
                panel.Children.Add(new TextBlock { Text = "What they do: " + phase.WhatExpertsDo, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "Why: " + phase.WhyItMatters, TextWrapping = TextWrapping.Wrap, Opacity = 0.9, Margin = new Thickness(0, 2, 0, 0) });
                panel.Children.Add(new TextBlock { Text = "Plugin helps: " + phase.HowPluginHelps, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                panel.Children.Add(new TextBlock { Text = "You in Altium: " + phase.YouStillDoInAltium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
            }
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static ScrollViewer BuildRouteTab()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Open the Route Priority window for a live net list. Summary:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new TextBlock
            {
                Text = RoutingPriorityGuide.FormatText(),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            });
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }

    /// <summary>Live RF→HS→PWR→Logic net list for interactive routing.</summary>
    internal sealed class RoutingPriorityWindow : Window
    {
        private TextBox _box;

        public RoutingPriorityWindow()
        {
            Title = "Route Priority — How Experts Route";
            Width = 640;
            Height = 560;
            MinWidth = 480;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                Text = "Experts route in this order. Keep this window open beside Altium Interactive Routing. Tick nets off mentally as you finish each class.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            _box = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                IsReadOnly = true,
                Text = RoutingPriorityGuide.FormatText(),
            };
            Grid.SetRow(_box, 1);
            root.Children.Add(_box);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var refresh = new Button { Content = "Refresh from Net Classes", Width = 180, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            refresh.Click += (_, __) => _box.Text = RoutingPriorityGuide.FormatText();
            var rules = new Button { Content = "Setup Rules", Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            rules.Click += (_, __) =>
            {
                try
                {
                    var classify = PcbDesignRulesSetup.Classify(useConnectivityHints: true);
                    var preview = new NetClassPreviewWindow(
                        classify.NetClassAssignments,
                        PcbDesignRulesSetup.ManagedNetClassOrder) { Owner = this };
                    if (preview.ShowDialog() == true && preview.ResultAssignments != null)
                    {
                        PcbDesignRulesSetup.Apply(preview.ResultAssignments);
                        _box.Text = RoutingPriorityGuide.FormatText();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Setup Rules", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            var close = new Button { Content = "Close", Width = 90, Height = 30, IsCancel = true };
            close.Click += (_, __) => Close();
            row.Children.Add(refresh);
            row.Children.Add(rules);
            row.Children.Add(close);
            Grid.SetRow(row, 2);
            root.Children.Add(row);

            Content = root;
        }
    }

    internal sealed class StackupAdvisorWindow : Window
    {
        private ComboBox _combo;
        private TextBlock _detail;
        private TextBlock _validate;

        public StackupAdvisorWindow()
        {
            Title = "JLCPCB Stackup Advisor";
            Width = 760;
            Height = 560;
            MinWidth = 600;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            intro.Children.Add(new TextBlock
            {
                Text = "Pick a JLCPCB stackup that matches your board. Data comes from the GitHub JLCPCB stackup libraries under fab/.",
                TextWrapping = TextWrapping.Wrap,
            });
            intro.Children.Add(new TextBlock
            {
                Text = PcbStackupAdvisor.ValidateLibrarySummary(),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Margin = new Thickness(0, 6, 0, 0),
            });
            intro.Children.Add(new TextBlock
            {
                Text = "ESP + LoRa default: 4L 1.6mm JLC04161H-7628 — Mid1 GND, Mid2 3v3. Auto-picks from your MCU/RF/USB/power parts.",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            var needsBanner = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brushes.DarkGreen,
            };
            intro.Children.Add(needsBanner);
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = "Recommendation", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var defaultIdx = Math.Max(0, PcbStackupAdvisor.Catalog.ToList().FindIndex(c => c.RecommendedDefault));
            BoardNeedsReport needs = null;
            try
            {
                needs = BoardNeedsAdvisor.Analyze();
                var autoIdx = PcbStackupAdvisor.Catalog.ToList().FindIndex(c =>
                    string.Equals(c.Id, needs.RecommendedStackup?.Id, StringComparison.OrdinalIgnoreCase));
                if (autoIdx >= 0)
                    defaultIdx = autoIdx;
                needsBanner.Text = needs.SummaryLine + "\n" + needs.LayerPlanSummary;
            }
            catch (Exception ex)
            {
                needsBanner.Text = "Auto-detect: " + ex.Message;
                needsBanner.Foreground = Brushes.DarkSlateGray;
            }

            _combo = new ComboBox
            {
                ItemsSource = PcbStackupAdvisor.Catalog.Select(c => c.Title).ToList(),
                SelectedIndex = defaultIdx,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _combo.SelectionChanged += (_, __) => RefreshDetail();
            body.Children.Add(_combo);

            _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(_detail);
            if (needs != null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Impedance / vias / heat (from parts)",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 4),
                });
                body.Children.Add(new TextBlock
                {
                    Text =
                        string.Join("\n", needs.ImpedanceHints.Select(z =>
                            $"• {z.Name}: {z.TargetOhms:0}Ω ≈ {z.SuggestedWidthMils:0.#} mil — {z.Notes}")) +
                        "\n" +
                        string.Join("\n", needs.ViaHints.Select(v =>
                            $"• {v.NetClass} via {v.DiameterMils:0}/{v.HoleMils:0} mil — {v.Why}")) +
                        (needs.ThermalHotspots.Count == 0
                            ? "\n• No strong heat sources tagged."
                            : "\n" + string.Join("\n", needs.ThermalHotspots.Take(4).Select(t =>
                                $"• Heat {t.Designator} ({t.Kind}): {t.WhatToDo}"))),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            _validate = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(_validate);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var sync = new Button { Content = "Copy Files to Documents", Width = 170, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            sync.Click += Sync_Click;
            var open = new Button { Content = "Open Stackup File", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            open.Click += Open_Click;
            var how = new Button { Content = "Show Load Steps", Width = 130, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            how.Click += How_Click;
            var check = new Button { Content = "Check Current PCB", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
            check.Click += Check_Click;
            var use = new Button { Content = "Use This (Save + Open)", Width = 160, Height = 30, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
            use.Click += Use_Click;
            var needsBtn = new Button { Content = "Full Board Needs", Width = 130, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            needsBtn.Click += (_, __) => new BoardNeedsWindow { Owner = this }.Show();
            actions.Children.Add(sync);
            actions.Children.Add(open);
            actions.Children.Add(how);
            actions.Children.Add(check);
            actions.Children.Add(use);
            actions.Children.Add(needsBtn);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            var close = new Button { Content = "Close", Width = 90, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            close.Click += (_, __) => Close();
            Grid.SetRow(close, 3);
            root.Children.Add(close);

            Content = root;
            RefreshDetail();
        }

        private StackupRecommendation Selected()
        {
            var idx = _combo.SelectedIndex;
            if (idx < 0 || idx >= PcbStackupAdvisor.Catalog.Count)
                return PcbStackupAdvisor.GetDefaultRecommendation();
            return PcbStackupAdvisor.Catalog[idx];
        }

        private void RefreshDetail()
        {
            var rec = Selected();
            _detail.Text =
                $"When: {rec.WhenToUse}\n" +
                $"Layers: {rec.LayerCount} · {rec.ThicknessMm} mm · outer {rec.OuterOz} oz / inner {rec.InnerOz} oz · {rec.Template}\n" +
                $"Fab mins: {rec.MinTraceMils} mil trace / {rec.MinClearanceMils} mil clearance\n" +
                $"Plan: {rec.LayerPlan}\n" +
                $"Files: {rec.StackupFile}" +
                (string.IsNullOrEmpty(rec.RulesFile) ? "" : $" + {rec.RulesFile}") + "\n" +
                "Caveats:\n • " + string.Join("\n • ", rec.Caveats);
        }

        private void Sync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = PcbStackupAdvisor.SyncRecommendedToDocuments();
                _validate.Text = "Copied recommended stackups to:\n" + dir;
                PcbStackupAdvisor.OpenInExplorer(dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stackup Advisor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = PcbStackupAdvisor.GetFilePath(Selected());
                PcbStackupAdvisor.OpenInExplorer(path);
                _validate.Text = "Selected in Explorer:\n" + path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stackup Advisor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void How_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this, PcbStackupAdvisor.FormatHowToLoad(Selected()), "How to load stackup", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = PcbStackupAdvisor.ValidateCurrentBoard(Selected());
                _validate.Text = report["summary"]?.ToString() + "\n\n" + report["howToLoad"];
                _validate.Foreground = report.TryGetValue("pass", out var p) && p is true ? Brushes.DarkGreen : Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stackup Advisor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rec = Selected();
                PcbStackupAdvisor.SyncRecommendedToDocuments();
                var path = PcbStackupAdvisor.GetFilePath(rec);
                PcbStackupAdvisor.SavePreference(rec);
                try
                {
                    var needs = BoardNeedsAdvisor.Analyze();
                    BoardNeedsAdvisor.ApplyRecommendedViaSizesToProfile(needs);
                }
                catch { }
                PcbStackupAdvisor.OpenInExplorer(path);
                _validate.Text =
                    $"Saved fab preference '{rec.Id}' (DRC mins {rec.MinTraceMils}/{rec.MinClearanceMils} mil).\n" +
                    $"Open in Layer Stack Manager → File → Load Stackup From File:\n{path}\n\n" +
                    (string.IsNullOrEmpty(rec.RulesFile)
                        ? ""
                        : $"Also load Design Rules from Documents\\AltiumEE\\recommended-stackups\\{rec.RulesFile}");
                _validate.Foreground = Brushes.DarkGreen;
                MessageBox.Show(this, PcbStackupAdvisor.FormatHowToLoad(rec), "Stackup ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stackup Advisor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Full stackup / impedance / via / thermal report from board parts.
    /// </summary>
    internal sealed class BoardNeedsWindow : Window
    {
        public BoardNeedsWindow()
        {
            Title = "Board Needs — Stackup, Impedance, Vias, Heat";
            Width = 820;
            Height = 640;
            MinWidth = 640;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = "How pros decide: parts → layers/stackup → impedance widths → via/fanout sizes → copper under heat. " +
                       "Heat is found from datasheet power × thermal resistance, EPAD, and IR on prototypes — we flag likely hot parts from the BOM.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var box = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                AcceptsReturn = true,
            };
            try
            {
                var report = BoardNeedsAdvisor.Analyze();
                box.Text = report.FormatFullText();
            }
            catch (Exception ex)
            {
                box.Text = ex.Message;
            }

            Grid.SetRow(box, 1);
            root.Children.Add(box);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var apply = new Button
            {
                Content = "Apply stackup + via sizes",
                Width = 180,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
            };
            apply.Click += (_, __) =>
            {
                try
                {
                    var report = BoardNeedsAdvisor.Analyze();
                    if (report.RecommendedStackup != null)
                    {
                        PcbStackupAdvisor.SyncRecommendedToDocuments();
                        PcbStackupAdvisor.SavePreference(report.RecommendedStackup);
                        BoardNeedsAdvisor.ApplyRecommendedViaSizesToProfile(report);
                        var path = PcbStackupAdvisor.GetFilePath(report.RecommendedStackup);
                        PcbStackupAdvisor.OpenInExplorer(path);
                        MessageBox.Show(
                            this,
                            PcbStackupAdvisor.FormatHowToLoad(report.RecommendedStackup) +
                            "\n\nVia sizes written to MCP rules profile for Fanout.",
                            "Applied",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Board Needs", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            var stackup = new Button { Content = "Stackup Advisor", Width = 120, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            stackup.Click += (_, __) => new StackupAdvisorWindow { Owner = this }.Show();
            var close = new Button { Content = "Close", Width = 90, Height = 30, IsCancel = true };
            close.Click += (_, __) => Close();
            row.Children.Add(apply);
            row.Children.Add(stackup);
            row.Children.Add(close);
            Grid.SetRow(row, 2);
            root.Children.Add(row);

            Content = root;
        }
    }
}
