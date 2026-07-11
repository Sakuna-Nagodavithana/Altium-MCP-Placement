using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EasyEDA_Loader
{
    public partial class DialogWindow
    {
        private TextBox searchTextBox;
        private Button searchButton;
        private DataGrid resultsGrid;
        private CheckBox closeDocumentsCheckBox;
        private CheckBox placeInSchematicCheckBox;
        private Button saveModelButton;
        private Button addToLibraryButton;
        private Button cancelButton;
        private Viewbox thumbnailViewbox;
        private Image thumbnailImage;
        private ScrollViewer symbolCanvasView;
        private Canvas symbolCanvas;
        private ScrollViewer footprintCanvasView;
        private Canvas footprintCanvas;

        // BOM Builder tab controls
        private TabControl mainTabControl;
        private Button loadPdfButton;
        private Button loadBomFileButton;
        private Button loadFromSchematicButton;
        private Button fetchFromJlcpcbButton;
        private Button exportBomButton;
        private Button addResolvedToLibraryButton;
        private Button addBomRowButton;
        private Button deleteBomRowButton;
        private Button resolveLcscButton;
        private Button chooseAllButton;
        private DataGrid bomGrid;
        private TextBlock bomStatusText;
        private CheckBox showOnlyIncludedCheckBox;

        private static void StyleReadableDataGrid(DataGrid grid)
        {
            // Force high-contrast grid: Altium's dark theme leaves white cells with
            // near-white headers, which is unreadable.
            var headerBg = new SolidColorBrush(Color.FromRgb(55, 55, 58));
            var headerFg = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            var rowBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
            var altRowBg = new SolidColorBrush(Color.FromRgb(45, 45, 48));
            var textFg = new SolidColorBrush(Color.FromRgb(241, 241, 241));
            var gridLine = new SolidColorBrush(Color.FromRgb(70, 70, 74));
            var selectBg = new SolidColorBrush(Color.FromRgb(0, 122, 204));

            grid.Background = rowBg;
            grid.Foreground = textFg;
            grid.RowBackground = rowBg;
            grid.AlternatingRowBackground = altRowBg;
            grid.HorizontalGridLinesBrush = gridLine;
            grid.VerticalGridLinesBrush = gridLine;
            grid.BorderBrush = gridLine;
            grid.BorderThickness = new Thickness(1);
            grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            grid.RowHeight = 28;

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, headerBg));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, headerFg));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, gridLine));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            grid.ColumnHeaderStyle = headerStyle;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, textFg));
            var selectedTrigger = new Trigger
            {
                Property = DataGridCell.IsSelectedProperty,
                Value = true,
            };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selectBg));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            cellStyle.Triggers.Add(selectedTrigger);
            grid.CellStyle = cellStyle;
        }

        private void BuildDialogUi()
        {
            Title = "JLCPCB / EasyEDA Component Loader";
            Height = 640;
            Width = 1180;
            MinHeight = 520;
            MinWidth = 960;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Loaded += Window_Loaded;

            // Window root is now a TabControl with two tabs: Single Part (legacy) and BOM Builder.
            mainTabControl = new TabControl { Padding = new Thickness(0) };
            Content = mainTabControl;

            // ----- Tab 1: Single Part (existing behaviour) -----
            mainGrid = new Grid();
            var singlePartTab = new TabItem { Header = "Single Part", Content = mainGrid };
            mainTabControl.Items.Add(singlePartTab);

            mainGrid.ColumnDefinitions.Add(new ColumnDefinition());
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition());
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var searchPanel = new Border { Padding = new Thickness(12, 15, 12, 10) };
            Grid.SetRow(searchPanel, 0);
            Grid.SetColumn(searchPanel, 0);

            var searchGrid = new Grid();
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchLabel = new TextBlock
            {
                Text = "Search JLCPCB / LCSC part number (e.g. C1525, C2073616):",
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(searchLabel, 0);
            Grid.SetColumnSpan(searchLabel, 2);
            searchGrid.Children.Add(searchLabel);

            searchTextBox = new TextBox
            {
                Margin = new Thickness(0, 0, 8, 0),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };
            searchTextBox.KeyDown += SearchTextBox_KeyDown;
            Grid.SetRow(searchTextBox, 1);
            Grid.SetColumn(searchTextBox, 0);
            searchGrid.Children.Add(searchTextBox);

            searchButton = new Button
            {
                Content = "Search",
                Width = 90,
                Height = 28,
                FontWeight = FontWeights.SemiBold,
            };
            searchButton.Click += SearchButton_Click;
            Grid.SetRow(searchButton, 1);
            Grid.SetColumn(searchButton, 1);
            searchGrid.Children.Add(searchButton);
            searchPanel.Child = searchGrid;
            mainGrid.Children.Add(searchPanel);

            resultsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                Margin = new Thickness(12, 0, 12, 10),
            };
            StyleReadableDataGrid(resultsGrid);
            resultsGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Add",
                Binding = new System.Windows.Data.Binding("AddToLibrary") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = 44,
            });
            resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "LCSC",
                Binding = new System.Windows.Data.Binding("PartNumber"),
                Width = 90,
            });
            resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Type",
                Binding = new System.Windows.Data.Binding("LibraryType"),
                Width = 75,
            });
            resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Stock",
                Binding = new System.Windows.Data.Binding("StockDisplay"),
                Width = 80,
            });
            resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Package",
                Binding = new System.Windows.Data.Binding("Package"),
                Width = 80,
            });
            resultsGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "FP",
                Binding = new System.Windows.Data.Binding("HasFootprint") { Mode = System.Windows.Data.BindingMode.OneWay },
                IsReadOnly = true,
                Width = 36,
            });
            resultsGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "3D",
                Binding = new System.Windows.Data.Binding("Has3d") { Mode = System.Windows.Data.BindingMode.OneWay },
                IsReadOnly = true,
                Width = 36,
            });
            resultsGrid.Columns.Add(new DataGridTextColumn { Header = "MPN", Binding = new System.Windows.Data.Binding("Name"), Width = 150 });
            resultsGrid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new System.Windows.Data.Binding("Description"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            resultsGrid.SelectionChanged += ResultsGrid_SelectionChanged;

            Grid.SetRow(resultsGrid, 1);
            Grid.SetColumn(resultsGrid, 0);
            mainGrid.Children.Add(resultsGrid);

            var bottomGrid = new Grid { Margin = new Thickness(12, 0, 12, 12) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(bottomGrid, 2);
            Grid.SetColumn(bottomGrid, 0);

            var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            closeDocumentsCheckBox = new CheckBox
            {
                Content = "Close library documents after adding",
                Margin = new Thickness(0, 0, 20, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeInSchematicCheckBox = new CheckBox
            {
                Content = "Place in schematic",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            optionsPanel.Children.Add(closeDocumentsCheckBox);
            optionsPanel.Children.Add(placeInSchematicCheckBox);
            Grid.SetColumn(optionsPanel, 0);
            bottomGrid.Children.Add(optionsPanel);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            saveModelButton = new Button { Content = "Save Model...", Width = 100, Height = 24, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            saveModelButton.Click += SaveModelButton_Click;
            addToLibraryButton = new Button { Content = "Add to Library", Width = 120, Height = 24, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            addToLibraryButton.Click += AddToLibraryButton_Click;
            cancelButton = new Button { Content = "Cancel", Width = 75, Height = 24, IsCancel = true };
            cancelButton.Click += CancelButton_Click;
            buttonPanel.Children.Add(saveModelButton);
            buttonPanel.Children.Add(addToLibraryButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetColumn(buttonPanel, 1);
            bottomGrid.Children.Add(buttonPanel);
            mainGrid.Children.Add(bottomGrid);

            var sideBorder = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(10),
            };
            sideBorder.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
            Grid.SetRow(sideBorder, 0);
            Grid.SetRowSpan(sideBorder, 3);
            Grid.SetColumn(sideBorder, 1);

            var sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideGrid.RowDefinitions.Add(new RowDefinition());
            sideGrid.RowDefinitions.Add(new RowDefinition());
            sideGrid.RowDefinitions.Add(new RowDefinition());

            sideGrid.Children.Add(new TextBlock
            {
                Text = "Preview",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            thumbnailViewbox = new Viewbox
            {
                MinHeight = 50,
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 10),
            };
            thumbnailImage = new Image { Height = 50, Width = 50 };
            thumbnailViewbox.Child = thumbnailImage;
            Grid.SetRow(thumbnailViewbox, 1);
            sideGrid.Children.Add(thumbnailViewbox);

            symbolCanvasView = new ScrollViewer
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = System.Windows.Media.Brushes.Black,
            };
            symbolCanvas = new Canvas { Height = 100, Width = 100, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            symbolCanvasView.Content = symbolCanvas;
            Grid.SetRow(symbolCanvasView, 2);
            sideGrid.Children.Add(symbolCanvasView);

            footprintCanvasView = new ScrollViewer
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = System.Windows.Media.Brushes.Black,
            };
            footprintCanvas = new Canvas { Height = 100, Width = 100, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            footprintCanvasView.Content = footprintCanvas;
            Grid.SetRow(footprintCanvasView, 3);
            sideGrid.Children.Add(footprintCanvasView);

            sideBorder.Child = sideGrid;
            mainGrid.Children.Add(sideBorder);

            BuildBomBuilderUi();
        }

        private void BuildBomBuilderUi()
        {
            var bomGrid2 = new Grid { Margin = new Thickness(8) };
            bomGrid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bomGrid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bomGrid2.RowDefinitions.Add(new RowDefinition());
            bomGrid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var sourcePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            sourcePanel.Children.Add(new TextBlock
            {
                Text = "Load components from:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold,
            });

            loadPdfButton = new Button { Content = "Schematic PDF...", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0) };
            loadPdfButton.Click += LoadPdfButton_Click;
            sourcePanel.Children.Add(loadPdfButton);

            loadBomFileButton = new Button { Content = "BOM file (CSV)...", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0) };
            loadBomFileButton.Click += LoadBomFileButton_Click;
            sourcePanel.Children.Add(loadBomFileButton);

            loadFromSchematicButton = new Button { Content = "Current schematic", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0) };
            loadFromSchematicButton.Click += LoadFromSchematicButton_Click;
            sourcePanel.Children.Add(loadFromSchematicButton);

            Grid.SetRow(sourcePanel, 0);
            bomGrid2.Children.Add(sourcePanel);

            var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            fetchFromJlcpcbButton = new Button { Content = "Fetch from JLCPCB", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false };
            fetchFromJlcpcbButton.Click += FetchFromJlcpcbButton_Click;
            actionsPanel.Children.Add(fetchFromJlcpcbButton);

            exportBomButton = new Button { Content = "Export BOM (JLCPCB CSV)...", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false };
            exportBomButton.Click += ExportBomButton_Click;
            actionsPanel.Children.Add(exportBomButton);

            addResolvedToLibraryButton = new Button { Content = "Add to Library", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false };
            addResolvedToLibraryButton.Click += AddResolvedToLibraryButton_Click;
            actionsPanel.Children.Add(addResolvedToLibraryButton);

            var editSeparator = new Separator
            {
                Width = 1,
                Margin = new Thickness(8, 4, 8, 4),
            };
            // Guard the style lookup -- Application.Current?.TryFindResource can throw
            // NRE in some Altium hosting contexts where the WPF Application is not
            // fully initialized. The separator works fine with the default style.
            try
            {
                var style = (Style)System.Windows.Application.Current?.TryFindResource(System.Windows.Controls.ToolBar.SeparatorStyleKey);
                if (style != null) editSeparator.Style = style;
            }
            catch { /* use default separator style */ }
            actionsPanel.Children.Add(editSeparator);

            addBomRowButton = new Button { Content = "Add Row", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), ToolTip = "Add a blank component row you can fill in by hand (e.g. paste an LCSC C... number)." };
            addBomRowButton.Click += AddBomRowButton_Click;
            actionsPanel.Children.Add(addBomRowButton);

            deleteBomRowButton = new Button { Content = "Delete Row", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false, ToolTip = "Remove the currently selected row from the BOM Builder list." };
            deleteBomRowButton.Click += DeleteBomRowButton_Click;
            actionsPanel.Children.Add(deleteBomRowButton);

            resolveLcscButton = new Button { Content = "Resolve LCSC by number", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false, ToolTip = "Look up the LCSC number in the selected row directly on JLCPCB (Basic/Extended, package, stock, datasheet)." };
            resolveLcscButton.Click += ResolveLcscButton_Click;
            actionsPanel.Children.Add(resolveLcscButton);

            chooseAllButton = new Button { Content = "Choose All", Height = 24, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), IsEnabled = false, ToolTip = "Tick every row, fetch all from JLCPCB, and load every resolved part into the schematic in one go." };
            chooseAllButton.Click += ChooseAllButton_Click;
            actionsPanel.Children.Add(chooseAllButton);

            // NOTE: bomGrid.SelectionChanged is wired AFTER bomGrid is created below,
            // not here -- wiring it before creation was the NRE cause.

            showOnlyIncludedCheckBox = new CheckBox
            {
                Content = "Show only ticked rows",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                IsChecked = false,
            };
            showOnlyIncludedCheckBox.Click += (s, e) => ApplyBomFilter();
            actionsPanel.Children.Add(showOnlyIncludedCheckBox);

            Grid.SetRow(actionsPanel, 1);
            bomGrid2.Children.Add(actionsPanel);

            bomGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                Margin = new Thickness(0, 4, 0, 4),
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HeadersVisibility = DataGridHeadersVisibility.Column,
            };
            StyleReadableDataGrid(bomGrid);

            // Wire SelectionChanged AFTER bomGrid is created (was previously before
            // creation, causing a NullReferenceException in the constructor).
            bomGrid.SelectionChanged += (s, e) =>
            {
                bool hasSelection = bomGrid.SelectedItem != null;
                deleteBomRowButton.IsEnabled = hasSelection;
                resolveLcscButton.IsEnabled = hasSelection;
            };

            bomGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Get",
                Binding = new System.Windows.Data.Binding("Include") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = 45,
            });
            bomGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Designator",
                Binding = new System.Windows.Data.Binding("Designator") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
                Width = 90,
            });
            bomGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new System.Windows.Data.Binding("OriginalValue") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
                Width = 110,
            });
            bomGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Power (0603)",
                Binding = new System.Windows.Data.Binding("IsPower") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = 95,
            });
            bomGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Pkg",
                Binding = new System.Windows.Data.Binding("Package") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
                Width = 60,
            });
            bomGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "JLCPCB Part",
                Binding = new System.Windows.Data.Binding("ResolvedLcsc") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
                Width = 90,
            });
            bomGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Basic",
                Binding = new System.Windows.Data.Binding("IsBasic") { Mode = System.Windows.Data.BindingMode.OneWay },
                IsReadOnly = true,
                Width = 55,
            });
            bomGrid.Columns.Add(new DataGridTextColumn { Header = "Matched Pkg", Binding = new System.Windows.Data.Binding("ResolvedPackage"), Width = 80, IsReadOnly = true });
            bomGrid.Columns.Add(new DataGridTextColumn { Header = "Stock", Binding = new System.Windows.Data.Binding("ResolvedStock"), Width = 60, IsReadOnly = true });
            bomGrid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new System.Windows.Data.Binding("ResolvedDescription"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
            bomGrid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new System.Windows.Data.Binding("ResolutionNote"), Width = 110, IsReadOnly = true });

            Grid.SetRow(bomGrid, 2);
            bomGrid2.Children.Add(bomGrid);

            bomStatusText = new TextBlock
            {
                Text = "No components loaded yet. Use the buttons above to load a schematic PDF, BOM CSV, or the current schematic.",
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(bomStatusText, 3);
            bomGrid2.Children.Add(bomStatusText);

            var bomTab = new TabItem { Header = "BOM Builder", Content = bomGrid2 };
            mainTabControl.Items.Add(bomTab);
        }
    }
}
