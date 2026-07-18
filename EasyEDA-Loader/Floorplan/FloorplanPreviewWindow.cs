using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EasyEDA_Loader.Floorplan;
using Microsoft.Win32;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Preview multiple rule-based floorplan layouts, pick board size / DXF / auto, then Apply.
    /// </summary>
    internal sealed class FloorplanPreviewWindow : Window
    {
        private List<FloorplanPart> _parts = new List<FloorplanPart>();
        private List<FloorplanVariant> _variants = new List<FloorplanVariant>();
        private FloorplanVariant _selected;
        private BoardOutlineSpec _board;

        private TextBlock _needsBanner;
        private BoardNeedsReport _needs;

        private RadioButton _autoSizeRadio;
        private RadioButton _manualSizeRadio;
        private RadioButton _dxfRadio;
        private TextBox _widthMmBox;
        private TextBox _heightMmBox;
        private TextBlock _boardStatus;
        private TextBlock _dxfPathText;
        private TextBlock _statusText;
        private CheckBox _drawOutlineCheck;
        private CheckBox _autoPlaceCheck;
        private ListBox _variantList;
        private Canvas _canvas;
        private ScrollViewer _scroll;
        private CanvasZoomPanHelper _zoom;
        private string _dxfPath;

        public FloorplanPreviewWindow()
        {
            Title = "Board Floorplan Preview";
            Width = 1100;
            Height = 720;
            MinWidth = 860;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
            Loaded += (_, __) => ReloadEverything();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Floorplan layouts (rule-based — no AI)",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
            });
            header.Children.Add(new TextBlock
            {
                Text = "Pick board size (auto / mm / DXF outline), compare layouts, then Apply. " +
                       "ICs + connectors move into zones; passives follow with Auto-Place.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Margin = new Thickness(0, 4, 0, 0),
            });
            _needsBanner = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20)),
            };
            header.Children.Add(_needsBanner);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var left = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 10, 0) };
            var leftStack = new StackPanel();

            leftStack.Children.Add(new TextBlock { Text = "Board outline", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

            _autoSizeRadio = new RadioButton { Content = "Auto size (from parts)", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
            _manualSizeRadio = new RadioButton { Content = "Manual size (mm)", Margin = new Thickness(0, 0, 0, 4) };
            _dxfRadio = new RadioButton { Content = "DXF board shape", Margin = new Thickness(0, 0, 0, 6) };
            _autoSizeRadio.Checked += (_, __) => Regenerate();
            _manualSizeRadio.Checked += (_, __) => Regenerate();
            _dxfRadio.Checked += (_, __) => Regenerate();
            leftStack.Children.Add(_autoSizeRadio);
            leftStack.Children.Add(_manualSizeRadio);

            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 6) };
            _widthMmBox = new TextBox { Text = "80", Width = 60, Margin = new Thickness(0, 0, 6, 0) };
            _heightMmBox = new TextBox { Text = "50", Width = 60 };
            _widthMmBox.LostFocus += (_, __) => { if (_manualSizeRadio.IsChecked == true) Regenerate(); };
            _heightMmBox.LostFocus += (_, __) => { if (_manualSizeRadio.IsChecked == true) Regenerate(); };
            sizeRow.Children.Add(new TextBlock { Text = "W", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sizeRow.Children.Add(_widthMmBox);
            sizeRow.Children.Add(new TextBlock { Text = "× H", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            sizeRow.Children.Add(_heightMmBox);
            sizeRow.Children.Add(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            leftStack.Children.Add(sizeRow);

            leftStack.Children.Add(_dxfRadio);
            var dxfRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 4) };
            var browse = new Button { Content = "Browse DXF…", Width = 110, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
            browse.Click += BrowseDxf_Click;
            dxfRow.Children.Add(browse);
            leftStack.Children.Add(dxfRow);
            _dxfPathText = new TextBlock
            {
                Text = "No DXF loaded",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 0, 0, 8),
                Opacity = 0.85,
                FontSize = 11,
            };
            leftStack.Children.Add(_dxfPathText);

            _boardStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            leftStack.Children.Add(_boardStatus);

            leftStack.Children.Add(new TextBlock { Text = "Layouts (10 options)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
            _variantList = new ListBox { Height = 280, Margin = new Thickness(0, 0, 0, 8) };
            _variantList.SelectionChanged += VariantList_SelectionChanged;
            leftStack.Children.Add(_variantList);

            _drawOutlineCheck = new CheckBox
            {
                Content = "Draw board outline on Mech 1",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 4),
            };
            _autoPlaceCheck = new CheckBox
            {
                Content = "Auto-Place passives after Apply",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 8),
            };
            leftStack.Children.Add(_drawOutlineCheck);
            leftStack.Children.Add(_autoPlaceCheck);

            var needsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var needsBtn = new Button { Content = "Board Needs…", Width = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Stackup, layers, impedance, via sizes, heat" };
            needsBtn.Click += (_, __) => new BoardNeedsWindow { Owner = this }.Show();
            var stackBtn = new Button { Content = "Stackup Advisor", Width = 120, Height = 28 };
            stackBtn.Click += (_, __) => new StackupAdvisorWindow { Owner = this }.Show();
            needsRow.Children.Add(needsBtn);
            needsRow.Children.Add(stackBtn);
            leftStack.Children.Add(needsRow);

            var regen = new Button { Content = "Refresh export & layouts", Height = 28, Margin = new Thickness(0, 0, 0, 8) };
            regen.Click += (_, __) => ReloadEverything();
            leftStack.Children.Add(regen);

            _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            leftStack.Children.Add(_statusText);

            left.Content = leftStack;
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            var right = new DockPanel();
            var legend = new TextBlock
            {
                Text = "Zones: Connectors · Power · MCU · RF · Other    (scroll wheel zoom, drag pan, right-click fit)",
                Margin = new Thickness(0, 0, 0, 6),
                Opacity = 0.85,
            };
            DockPanel.SetDock(legend, Dock.Top);
            right.Children.Add(legend);

            _scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)),
            };
            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                Width = 2000,
                Height = 1600,
            };
            _scroll.Content = _canvas;
            right.Children.Add(_scroll);
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var apply = new Button
            {
                Content = "Apply selected layout",
                Width = 180,
                Height = 34,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 10, 0),
            };
            apply.Click += Apply_Click;
            var close = new Button { Content = "Close", Width = 90, Height = 34, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(apply);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
        }

        private void ReloadEverything()
        {
            try
            {
                _statusText.Text = "Exporting connectivity…";
                DesignExporter.ExportForPlacementPlanning();
                _parts = FloorplanGenerator.LoadPartsFromConnectivity();
                var targets = _parts.Count(p => p.Role != FloorplanRole.Passive && p.Role != FloorplanRole.Skip);
                try
                {
                    _needs = BoardNeedsAdvisor.Analyze();
                    _needsBanner.Text =
                        _needs.SummaryLine + "\n" +
                        string.Join(" · ", _needs.ViaHints.Take(4).Select(v => $"{v.NetClass} {v.DiameterMils:0}/{v.HoleMils:0}")) +
                        (_needs.ThermalHotspots.Count > 0
                            ? $" · Heat×{_needs.ThermalHotspots.Count}"
                            : "");
                }
                catch (Exception nex)
                {
                    _needsBanner.Text = "Needs: " + nex.Message;
                }

                _statusText.Text = $"Loaded {_parts.Count} parts ({targets} floorplan targets).";
                Regenerate();
            }
            catch (Exception ex)
            {
                _statusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Floorplan", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Regenerate()
        {
            try
            {
                _board = ResolveBoard();
                _boardStatus.Text = _board.Describe();
                _variants = FloorplanGenerator.GenerateVariants(_board, _parts);
                FloorplanVariant recommended = null;
                try
                {
                    recommended = FloorplanScorer.PickBest(_variants, DesignExporter.DefaultExportPath);
                }
                catch { }

                _variantList.Items.Clear();
                var selectIdx = 0;
                for (var i = 0; i < _variants.Count; i++)
                {
                    var v = _variants[i];
                    var isBest = recommended != null && ReferenceEquals(v, recommended);
                    if (isBest)
                        selectIdx = i;
                    _variantList.Items.Add(new VariantListItem
                    {
                        Variant = v,
                        Display = (isBest ? "★ " : "") + v.Title + " — " + v.Description,
                    });
                }

                if (_variantList.Items.Count > 0)
                    _variantList.SelectedIndex = selectIdx;
            }
            catch (Exception ex)
            {
                _statusText.Text = ex.Message;
            }
        }

        private BoardOutlineSpec ResolveBoard()
        {
            if (_dxfRadio.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(_dxfPath))
                    throw new InvalidOperationException("Browse to a DXF outline first, or choose Auto / Manual size.");
                return DxfBoardOutline.Load(_dxfPath);
            }

            if (_manualSizeRadio.IsChecked == true)
            {
                double wMm, hMm;
                if (!double.TryParse(_widthMmBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out wMm) ||
                    !double.TryParse(_heightMmBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hMm))
                {
                    // also try current culture
                    if (!double.TryParse(_widthMmBox.Text.Trim(), out wMm) ||
                        !double.TryParse(_heightMmBox.Text.Trim(), out hMm))
                        throw new InvalidOperationException("Enter valid width and height in mm.");
                }

                return BoardOutlineSpec.FromRectangle(
                    wMm / 0.0254,
                    hMm / 0.0254,
                    BoardOutlineSource.Manual,
                    "Manual size");
            }

            return FloorplanGenerator.BuildAutoBoard(_parts);
        }

        private void BrowseDxf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "DXF outline (*.dxf)|*.dxf|All files (*.*)|*.*",
                Title = "Select board outline DXF",
            };
            if (dlg.ShowDialog(this) != true)
                return;

            _dxfPath = dlg.FileName;
            _dxfPathText.Text = _dxfPath;
            _dxfRadio.IsChecked = true;
            try
            {
                Regenerate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "DXF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void VariantList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = _variantList.SelectedItem as VariantListItem;
            _selected = item?.Variant;
            DrawPreview(_selected);
        }

        private void DrawPreview(FloorplanVariant variant)
        {
            _canvas.Children.Clear();
            if (variant == null)
                return;

            const double scale = 0.35; // mils → px
            const double pad = 40;

            // Board fill
            var boardPoly = variant.Board.PolygonMils ?? new List<BoardPoint>();
            if (boardPoly.Count >= 2)
            {
                var boardGeom = new Polygon
                {
                    Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF4)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)),
                    StrokeThickness = 2,
                };
                foreach (var p in boardPoly)
                    boardGeom.Points.Add(new Point(pad + p.X * scale, pad + (variant.Board.HeightMils - p.Y) * scale));
                _canvas.Children.Add(boardGeom);
            }
            else
            {
                var rect = new Rectangle
                {
                    Width = variant.Board.WidthMils * scale,
                    Height = variant.Board.HeightMils * scale,
                    Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF4)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)),
                    StrokeThickness = 2,
                };
                Canvas.SetLeft(rect, pad);
                Canvas.SetTop(rect, pad);
                _canvas.Children.Add(rect);
            }

            foreach (var z in variant.Zones)
            {
                var brush = ZoneBrush(z.BrushKey);
                var zr = new Rectangle
                {
                    Width = Math.Max(4, (z.Right - z.Left) * scale),
                    Height = Math.Max(4, (z.Top - z.Bottom) * scale),
                    Fill = brush,
                    Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                };
                Canvas.SetLeft(zr, pad + z.Left * scale);
                Canvas.SetTop(zr, pad + (variant.Board.HeightMils - z.Top) * scale);
                _canvas.Children.Add(zr);

                var label = new TextBlock
                {
                    Text = z.Name,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, 20, 20, 20)),
                };
                Canvas.SetLeft(label, pad + z.Left * scale + 4);
                Canvas.SetTop(label, pad + (variant.Board.HeightMils - z.Top) * scale + 2);
                _canvas.Children.Add(label);
            }

            foreach (var p in variant.Parts)
            {
                var w = Math.Max(18, p.WidthMils * scale * 0.85);
                var h = Math.Max(14, p.HeightMils * scale * 0.85);
                var fill = RoleBrush(p.Role);
                var box = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Fill = fill,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    RadiusX = 2,
                    RadiusY = 2,
                };
                var left = pad + p.TargetXMils * scale - w * 0.5;
                var top = pad + (variant.Board.HeightMils - p.TargetYMils) * scale - h * 0.5;
                Canvas.SetLeft(box, left);
                Canvas.SetTop(box, top);
                _canvas.Children.Add(box);

                var txt = new TextBlock
                {
                    Text = p.Designator,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Black,
                };
                Canvas.SetLeft(txt, left + 2);
                Canvas.SetTop(txt, top + 1);
                _canvas.Children.Add(txt);
            }

            _canvas.Width = Math.Max(400, pad * 2 + variant.Board.WidthMils * scale + 80);
            _canvas.Height = Math.Max(300, pad * 2 + variant.Board.HeightMils * scale + 80);

            if (_zoom == null)
            {
                // Attach after ScrollViewer is parented
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _zoom = new CanvasZoomPanHelper(_canvas);
                        _zoom.FitToBoundingBox();
                    }
                    catch { }
                }));
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { _zoom.FitToBoundingBox(); } catch { }
                }));
            }
        }

        private static Brush ZoneBrush(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "conn": return new SolidColorBrush(Color.FromArgb(70, 70, 130, 180));
                case "pwr": return new SolidColorBrush(Color.FromArgb(70, 200, 90, 50));
                case "mcu": return new SolidColorBrush(Color.FromArgb(70, 60, 160, 90));
                case "rf": return new SolidColorBrush(Color.FromArgb(70, 160, 60, 160));
                default: return new SolidColorBrush(Color.FromArgb(50, 120, 120, 120));
            }
        }

        private static Brush RoleBrush(FloorplanRole role)
        {
            switch (role)
            {
                case FloorplanRole.Connector: return new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5));
                case FloorplanRole.Power: return new SolidColorBrush(Color.FromRgb(0xED, 0x7D, 0x31));
                case FloorplanRole.Mcu: return new SolidColorBrush(Color.FromRgb(0x70, 0xAD, 0x47));
                case FloorplanRole.Rf: return new SolidColorBrush(Color.FromRgb(0xC0, 0x5B, 0xC0));
                case FloorplanRole.Crystal: return new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x00));
                default: return new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                MessageBox.Show(this, "Select a layout first.", "Floorplan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _statusText.Text = "Applying…";
                PcbDocumentHelper.PumpUi();
                try
                {
                    _needs = BoardNeedsAdvisor.Analyze();
                    if (_needs.RecommendedStackup != null)
                        PcbStackupAdvisor.SavePreference(_needs.RecommendedStackup);
                    BoardNeedsAdvisor.ApplyRecommendedViaSizesToProfile(_needs);
                }
                catch { }

                var message = FloorplanApplier.Apply(
                    _selected,
                    drawBoardOutline: _drawOutlineCheck.IsChecked == true,
                    autoPlacePassivesAfter: _autoPlaceCheck.IsChecked == true);
                if (_needs != null)
                {
                    message += "\n\n--- Board needs ---\n" + _needs.SummaryLine + "\n" +
                               "Load stackup in Altium: " + (_needs.RecommendedStackup?.Template ?? "?") +
                               "\nOpen Board Needs for heat / impedance details.";
                }

                _statusText.Text = "Applied " + _selected.Title;
                MessageBox.Show(this, message, "Floorplan Applied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _statusText.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Floorplan", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private sealed class VariantListItem
        {
            public FloorplanVariant Variant { get; set; }
            public string Display { get; set; }
            public override string ToString() => Display;
        }
    }
}
