using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevExpress.LookAndFeel;
using DevExpress.Skins;
using Microsoft.Win32;

namespace EasyEDA_Loader
{
    public partial class DialogWindow : Window
    {
        private Grid mainGrid;
        private EasyedaApi Api;
        private CancellationTokenSource cts;
        private CancellationTokenSource previewCts;
        private ObservableCollection<PartInfoViewModel> searchResults;
        private CanvasZoomPanHelper _footprintHelper;
        private CanvasZoomPanHelper _symbolHelper;
        private ComponentInfo _currentComponent;
        private EeFootprint3dModel _currentModel;
        private Root _currentRoot;

        public List<ComponentSelection> SelectedComponents { get; private set; }
        public bool CloseDocuments => closeDocumentsCheckBox?.IsChecked == true;
        public bool PlaceInSchematic => placeInSchematicCheckBox?.IsChecked == true;

        public List<BomResolvedPart> BomResolvedParts { get; private set; } = new List<BomResolvedPart>();

        private BomBuilderViewModel _bomViewModel;
        private JlcpcbPartsApi _jlcpcbApi;
        private CancellationTokenSource _bomCts;

        public DialogWindow()
        {
            try { BuildDialogUi(); }
            catch (Exception ex) { throw new Exception($"DialogWindow.BuildDialogUi failed: {ex.Message}", ex); }

            try
            {
                Api = new EasyedaApi();
                cts = new CancellationTokenSource();
                previewCts = new CancellationTokenSource();
                searchResults = new ObservableCollection<PartInfoViewModel>();
                SelectedComponents = new List<ComponentSelection>();

                resultsGrid.ItemsSource = searchResults;

                _footprintHelper = new CanvasZoomPanHelper(footprintCanvas);
                footprintCanvasView.ScrollChanged += (s, e) =>
                {
                    if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                        _footprintHelper.FitToBoundingBox();
                };

                _symbolHelper = new CanvasZoomPanHelper(symbolCanvas);
                symbolCanvasView.ScrollChanged += (s, e) =>
                {
                    if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                        _symbolHelper.FitToBoundingBox();
                };
            }
            catch (Exception ex) { throw new Exception($"DialogWindow init failed: {ex.Message}", ex); }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Apply Altium theme colors after window is loaded
            ApplyAltiumTheme();
        }

        private void ApplyAltiumTheme()
        {
            System.Windows.Media.SolidColorBrush bgBrush = null;
            System.Windows.Media.SolidColorBrush textBrush = null;
            try
            {
                // Get Altium's skin colors
                var lookAndFeel = new UserLookAndFeel(this);
                var skin = CommonSkins.GetSkin(lookAndFeel);

                if (skin?.Colors != null)
                {
                    var windowColor = skin.Colors.GetColor("Window");
                    bgBrush = new SolidColorBrush(Color.FromArgb(
                        windowColor.A, windowColor.R, windowColor.G, windowColor.B));

                    var textColor = skin.Colors.GetColor("WindowText");
                    textBrush = new SolidColorBrush(Color.FromArgb(
                        textColor.A, textColor.R, textColor.G, textColor.B));
                }
            }
            catch
            {
                // Theme detection failed -- fall through to dark fallback below.
            }

            if (bgBrush == null)
                bgBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48));
            if (textBrush == null)
                textBrush = new SolidColorBrush(Color.FromRgb(241, 241, 241));

            try
            {
                // Content can be a TabControl or a Grid depending on the build -- handle
                // both instead of assuming Grid (the old cast threw InvalidCastException
                // which surfaced as the "Object reference not set" error).
                if (this.Content is System.Windows.Controls.Panel panel)
                    panel.Background = bgBrush;
                else if (this.Content is System.Windows.Controls.Control control)
                    control.Background = bgBrush;

                this.Resources[SystemColors.WindowTextBrushKey] = textBrush;
                if (this.Content is System.Windows.DependencyObject dep)
                    ApplyColorToTextBlocks(dep, textBrush);
            }
            catch
            {
                // Best-effort theming -- never let it block the dialog.
            }
        }

        private void ApplyColorToTextBlocks(System.Windows.DependencyObject parent, SolidColorBrush brush)
        {
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.Foreground = brush;
                }
                else if (child is System.Windows.Controls.ContentControl contentControl)
                {
                    contentControl.Foreground = brush;
                }
                
                // Recursively apply to children
                ApplyColorToTextBlocks(child, brush);
            }
        }

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAddButtonState();

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();

            if (resultsGrid.SelectedItem is PartInfoViewModel partViewModel)
                await LoadPreviewAsync(partViewModel, previewCts.Token);
            else
                ClearPreview();
        }

        private async Task LoadPreviewAsync(PartInfoViewModel partViewModel, CancellationToken cancellationToken)
        {
            try
            {
                thumbnailImage.Source = null;
                symbolCanvas.Children.Clear();
                footprintCanvas.Children.Clear();
                _currentComponent = null;
                _currentModel = null;
                _currentRoot = null;
                saveModelButton.IsEnabled = false;

                var root = await Task.Run(() => Api.GetComponentJsonAsync(partViewModel.PartInfo.Part, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                    return;

                if (root?.Component != null)
                {
                    _currentComponent = root.Component;
                    _currentRoot = root;

                if (_currentComponent.Symbol?.Shapes != null)
                {
                    SymbolDrawing.DrawComponent(symbolCanvas, _currentComponent.Symbol.Shapes);
                    _ = symbolCanvas.Dispatcher.InvokeAsync(() =>
                    {
                        _symbolHelper.FitToBoundingBox();
                    }, DispatcherPriority.Loaded);
                }

                    if (_currentComponent.PackageDetail?.Footprint != null)
                    {
                        var eeFootprint = _currentComponent.PackageDetail.Footprint;
                        _currentModel = eeFootprint.GetModel();

                        saveModelButton.IsEnabled = _currentModel != null;

                        EeFootprintContext ctx = new EeFootprintContext
                        {
                            Box = eeFootprint.BoundingBox,
                            Layers = eeFootprint.Layers,
                            CancelToken = cancellationToken,
                            Exception = null,
                        };

                        eeFootprint.DrawToCanvas(footprintCanvas, ctx);
                        
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        _ = footprintCanvas.Dispatcher.InvokeAsync(() =>
                        {
                            _footprintHelper.FitToBoundingBox();
                        }, DispatcherPriority.Loaded);
                    }

                    if (!string.IsNullOrEmpty(_currentComponent.Thumb))
                    {
                        try
                        {
                            var thumbnail = await Task.Run(() => Api.LoadPngAsync(_currentComponent.Thumb, cancellationToken));
                            
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            if (thumbnail != null)
                            {
                                thumbnailImage.Source = thumbnail;
                                thumbnailImage.MaxWidth = thumbnail.Width;
                                thumbnailImage.MaxHeight = thumbnail.Height;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    SetBomStatus($"Preview failed: {ex.Message}");
                }
                catch
                {
                    // Status control may not be ready yet.
                }
            }
        }

        private void ClearPreview()
        {
            thumbnailImage.Source = null;
            symbolCanvas.Children.Clear();
            footprintCanvas.Children.Clear();
            _currentComponent = null;
            _currentModel = null;
            _currentRoot = null;
            saveModelButton.IsEnabled = false;
        }

        public void UpdateAddButtonState()
        {
            addToLibraryButton.IsEnabled = searchResults.Any(p => p.AddToLibrary);
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(searchTextBox.Text))
            {
                MessageBox.Show("Please enter a part number to search.", "Search Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            searchButton.IsEnabled = false;
            addToLibraryButton.IsEnabled = false;
            searchResults.Clear();

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                // Run the API call on a background thread
                var searchText = searchTextBox.Text;
                var results = await Task.Run(() => Api.SearchProductInfoAsync(searchText));

                // Enrich each hit with JLCPCB stock + Basic/Extended so the grid shows
                // assembly-relevant info without opening the BOM Builder tab.
                if (results != null && results.Count > 0)
                {
                    _jlcpcbApi ??= new JlcpcbPartsApi();
                    var enrichTasks = results.Select(async part =>
                    {
                        try
                        {
                            var jlc = await _jlcpcbApi.LookupByLcscAsync(part.Part, CancellationToken.None);
                            if (jlc == null)
                                return;
                            part.Stock = jlc.Stock;
                            part.Package = jlc.Package;
                            part.LibraryType = jlc.IsBasic ? "Basic" : "Extended";
                            if (string.IsNullOrWhiteSpace(part.Description) && !string.IsNullOrWhiteSpace(jlc.Description))
                                part.Description = jlc.Description;
                        }
                        catch
                        {
                            // Stock lookup is best-effort — still show the EasyEDA hit.
                        }
                    });
                    await Task.WhenAll(enrichTasks);
                }

                // Add results on the UI thread
                if (results != null && results.Count > 0)
                {
                    foreach (var part in results)
                    {
                        searchResults.Add(new PartInfoViewModel(part, this));
                    }
                }
                else
                {
                    MessageBox.Show("No results found.", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                searchButton.IsEnabled = true;
            }
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SearchButton_Click(sender, e);
            }
        }

        private async void AddToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedParts = searchResults.Where(p => p.AddToLibrary).ToList();
            
            if (selectedParts.Count == 0)
            {
                MessageBox.Show("Please select at least one component to add.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            addToLibraryButton.IsEnabled = false;
            cancelButton.IsEnabled = false;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SelectedComponents.Clear();

                foreach (var partViewModel in selectedParts)
                {
                    var partInfo = partViewModel.PartInfo;

                    // Fetch component data
                    var root = await Task.Run(() => Api.GetComponentJsonAsync(partInfo.Part, cts.Token));

                    if (root?.Component != null)
                    {
                        var component = root.Component;
                        var has3dModel = component.PackageDetail?.Footprint?.GetModel() != null;
                        var hasFootprint = component.PackageDetail?.Footprint != null;

                        // Update the view model with actual data
                        partViewModel.HasFootprint = hasFootprint;
                        partViewModel.Has3d = has3dModel;

                        SelectedComponents.Add(new ComponentSelection
                        {
                            PartInfo = partInfo,
                            Root = root,
                            Include3dModel = has3dModel,
                            IncludeFootprint = hasFootprint
                        });
                    }
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load component data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                addToLibraryButton.IsEnabled = true;
                cancelButton.IsEnabled = true;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void SaveModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
                return;

            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save Model File As",
                Filter = "STEP Files (*.step)|*.step|All Files (*.*)|*.*",
                FileName = $"{_currentModel.Name}.step",
                DefaultExt = "step"
            };

            bool? result = saveFileDialog.ShowDialog();
            if (result == true)
            {
                saveModelButton.IsEnabled = false;
                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;

                    var modelData = await Task.Run(() => Api.LoadModelAsync(_currentModel.Uuid, cts.Token));

                    if (modelData != null && modelData.Length > 0)
                    {
                        File.WriteAllBytes(saveFileDialog.FileName, modelData);
                        MessageBox.Show($"Model saved successfully to {saveFileDialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Failed to download model data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    saveModelButton.IsEnabled = _currentModel != null;
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            cts?.Cancel();
            previewCts?.Cancel();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _bomCts?.Cancel();
            _bomCts?.Dispose();
            cts?.Dispose();
            previewCts?.Dispose();
            base.OnClosed(e);
        }

        private BomBuilderViewModel EnsureBomViewModel()
        {
            if (_bomViewModel == null)
            {
                _jlcpcbApi = new JlcpcbPartsApi();
                _bomViewModel = new BomBuilderViewModel(_jlcpcbApi);
                _bomCts = new CancellationTokenSource();
                bomGrid.ItemsSource = _bomViewModel.Rows;
            }
            return _bomViewModel;
        }

        private void ApplyBomFilter()
        {
            if (_bomViewModel == null)
                return;

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_bomViewModel.Rows);
            if (view == null)
                return;

            bool onlyIncluded = showOnlyIncludedCheckBox?.IsChecked == true;
            view.Filter = obj => !onlyIncluded || ((BomRow)obj).Include;
            view.Refresh();
        }

        private void UpdateBomActionButtons()
        {
            bool hasRows = _bomViewModel != null && _bomViewModel.Rows.Count > 0;
            fetchFromJlcpcbButton.IsEnabled = hasRows;
            chooseAllButton.IsEnabled = hasRows;
            bool hasResolved = _bomViewModel != null && _bomViewModel.ReadyToOrder.Any();
            exportBomButton.IsEnabled = hasResolved;
            addResolvedToLibraryButton.IsEnabled = hasResolved;
        }

        private void SetBomStatus(string text)
        {
            if (bomStatusText != null)
                bomStatusText.Text = text;
        }

        private void LoadPdfButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select schematic PDF",
                Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
                DefaultExt = ".pdf",
            };
            if (ofd.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetBomStatus($"Reading {System.IO.Path.GetFileName(ofd.FileName)} ...");
                var result = SchematicPdfReader.ExtractComponents(ofd.FileName);
                var vm = EnsureBomViewModel();

                var rows = result.Components.Select(c => new BomRow
                {
                    Designator = c.Designator,
                    OriginalValue = c.Value,
                    OriginalLcsc = c.Lcsc,
                    Quantity = 1,
                }).ToList();

                vm.Warnings.Clear();
                foreach (var w in result.Warnings)
                    vm.Warnings.Add(w);
                if (result.UnmatchedLcsc.Count > 0)
                    vm.Warnings.Add($"Unmatched LCSC numbers without a nearby designator: {string.Join(", ", result.UnmatchedLcsc)}");

                vm.LoadRows(rows, $"PDF: {System.IO.Path.GetFileName(ofd.FileName)} ({result.PageCount} pages)");
                ApplyBomFilter();
                UpdateBomActionButtons();

                string warningText = vm.Warnings.Count > 0
                    ? " Warnings: " + string.Join("; ", vm.Warnings)
                    : "";
                SetBomStatus($"Loaded {rows.Count} components from PDF ({result.PageCount} pages). " +
                             $"Tick the ones you need, mark Power for 0603, then click Fetch from JLCPCB.{warningText}");
            }
            catch (Exception ex)
            {
                SetBomStatus($"Failed to read PDF: {ex.Message}");
                MessageBox.Show($"Failed to read PDF:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void LoadBomFileButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select BOM CSV",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
            };
            if (ofd.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var rows = BomLoader.LoadCsv(ofd.FileName);
                var vm = EnsureBomViewModel();
                vm.Warnings.Clear();
                vm.LoadRows(rows, $"BOM file: {System.IO.Path.GetFileName(ofd.FileName)}");
                ApplyBomFilter();
                UpdateBomActionButtons();
                SetBomStatus($"Loaded {rows.Count} rows from {System.IO.Path.GetFileName(ofd.FileName)}. " +
                             "Tick the ones you need and click Fetch from JLCPCB.");
            }
            catch (Exception ex)
            {
                SetBomStatus($"Failed to load BOM: {ex.Message}");
                MessageBox.Show($"Failed to load BOM:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void LoadFromSchematicButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetBomStatus("Exporting current schematic ...");
                string connectivityPath = DesignExporter.ExportFullProject();
                var rows = LoadBomFromConnectivityJson(connectivityPath);
                var vm = EnsureBomViewModel();
                vm.Warnings.Clear();
                vm.LoadRows(rows, $"Current schematic: {System.IO.Path.GetFileName(connectivityPath)}");
                ApplyBomFilter();
                UpdateBomActionButtons();
                SetBomStatus($"Loaded {rows.Count} components from the live schematic. " +
                             "Tick the ones you need and click Fetch from JLCPCB.");
            }
            catch (Exception ex)
            {
                SetBomStatus($"Failed to read schematic: {ex.Message}");
                MessageBox.Show($"Failed to read current schematic:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private static List<BomRow> LoadBomFromConnectivityJson(string path)
        {
            var rows = new List<BomRow>();
            if (!System.IO.File.Exists(path))
                return rows;

            var payload = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
            var sheets = payload["schematics"] as Newtonsoft.Json.Linq.JArray;
            if (sheets != null)
            {
                foreach (var sheet in sheets)
                {
                    string sheetName = (string)sheet["sheet"];
                    foreach (var c in sheet["components"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        rows.Add(new BomRow
                        {
                            Designator = (string)c["designator"],
                            OriginalValue = (string)c["comment"],
                            OriginalLcsc = (string)c["jlcpcb"],
                            Sheet = sheetName,
                            Quantity = 1,
                        });
                    }
                }
            }
            else
            {
                foreach (var c in payload["components"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                {
                    rows.Add(new BomRow
                    {
                        Designator = (string)c["designator"],
                        OriginalValue = (string)c["comment"],
                        OriginalLcsc = (string)c["jlcpcb"],
                        Quantity = 1,
                    });
                }
            }
            return rows;
        }

        private async void FetchFromJlcpcbButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = EnsureBomViewModel();
            var ticked = vm.Rows.Where(r => r.Include).ToList();
            if (ticked.Count == 0)
            {
                MessageBox.Show("Tick at least one row first.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            fetchFromJlcpcbButton.IsEnabled = false;
            addResolvedToLibraryButton.IsEnabled = false;
            exportBomButton.IsEnabled = false;
            _bomCts?.Cancel();
            _bomCts?.Dispose();
            _bomCts = new CancellationTokenSource();

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var progress = new Progress<string>(msg => SetBomStatus(msg));
                int ok = await vm.ResolveAllAsync(progress, _bomCts.Token);
                ApplyBomFilter();
                UpdateBomActionButtons();
                SetBomStatus($"Resolved {ok}/{ticked.Count} rows against JLCPCB. " +
                             "Basic parts are ticked in the Basic column. Review and then Export BOM or Add to Library.");
            }
            catch (OperationCanceledException)
            {
                SetBomStatus("Fetch cancelled.");
            }
            catch (Exception ex)
            {
                SetBomStatus($"Fetch failed: {ex.Message}");
                MessageBox.Show($"Fetch from JLCPCB failed:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                fetchFromJlcpcbButton.IsEnabled = vm.Rows.Count > 0;
                UpdateBomActionButtons();
            }
        }

        private void AddBomRowButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = EnsureBomViewModel();
            var row = vm.AddBlankRow();
            bomGrid.SelectedItem = row;
            bomGrid.ScrollIntoView(row);
            UpdateBomActionButtons();
            bomStatusText.Text = $"Added a blank row. Type the designator (e.g. R5) and either a value or an LCSC C... number, then click 'Resolve LCSC by number'.";
        }

        private void DeleteBomRowButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = EnsureBomViewModel();
            var row = bomGrid.SelectedItem as BomRow;
            if (row == null) return;
            vm.DeleteRow(row);
            UpdateBomActionButtons();
        }

        private async void ResolveLcscButton_Click(object sender, RoutedEventArgs e)
        {
            var row = bomGrid.SelectedItem as BomRow;
            if (row == null) return;
            string lcsc = (row.ResolvedLcsc ?? "").Trim();
            if (string.IsNullOrWhiteSpace(lcsc))
            {
                MessageBox.Show("Type an LCSC part number (e.g. C25744) in the LCSC column first, then click 'Resolve LCSC by number'.",
                    "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var vm = EnsureBomViewModel();
            resolveLcscButton.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                _bomCts?.Cancel();
                _bomCts = new System.Threading.CancellationTokenSource();
                bomStatusText.Text = $"Looking up {lcsc} on JLCPCB...";
                bool ok = await vm.ResolveByLcscAsync(row, _bomCts.Token);
                if (ok)
                    bomStatusText.Text = $"Resolved {row.Designator} -> {row.ResolvedLcsc} ({(row.IsBasic ? "Basic" : "Extended")}, {row.ResolvedPackage}, stock {row.ResolvedStock}).";
                else
                    bomStatusText.Text = row.ResolutionNote ?? $"Could not resolve {lcsc}.";
            }
            catch (Exception ex)
            {
                bomStatusText.Text = ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                resolveLcscButton.IsEnabled = bomGrid.SelectedItem != null;
                UpdateBomActionButtons();
            }
        }

        private void ExportBomButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = EnsureBomViewModel();
            var ready = vm.ReadyToOrder.ToList();
            if (ready.Count == 0)
            {
                MessageBox.Show("No resolved rows to export. Click Fetch from JLCPCB first.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export JLCPCB BOM",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = $"BOM_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            };
            if (sfd.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                string written = BomExporter.Export(vm.Rows, sfd.FileName);
                SetBomStatus($"BOM written to {written} ({ready.Count} ready-to-order rows).");
                MessageBox.Show($"BOM exported to:\n{written}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export BOM:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void ChooseAllButton_Click(object sender, RoutedEventArgs e)
        {
            // One-click flow: tick every row, fetch all from JLCPCB, then load every
            // resolved part into the schematic. Equivalent to pressing
            // "Fetch from JLCPCB" then "Add to Library" with all rows ticked.
            var vm = EnsureBomViewModel();
            if (vm.Rows.Count == 0)
            {
                MessageBox.Show("Load a schematic PDF, BOM CSV, or the current schematic first.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var row in vm.Rows)
                row.Include = true;
            ApplyBomFilter();

            fetchFromJlcpcbButton.IsEnabled = false;
            addResolvedToLibraryButton.IsEnabled = false;
            exportBomButton.IsEnabled = false;
            chooseAllButton.IsEnabled = false;
            _bomCts?.Cancel();
            _bomCts?.Dispose();
            _bomCts = new CancellationTokenSource();

            int ticked = vm.Rows.Count;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetBomStatus($"Choose All: resolving {ticked} rows against JLCPCB...");
                var progress = new Progress<string>(msg => SetBomStatus(msg));
                int ok = await vm.ResolveAllAsync(progress, _bomCts.Token);
                ApplyBomFilter();
                UpdateBomActionButtons();

                var ready = vm.ReadyToOrder.ToList();
                if (ready.Count == 0)
                {
                    SetBomStatus($"Resolved {ok}/{ticked} rows, but none have a usable LCSC part. Check the Note column.");
                    MessageBox.Show("No resolved rows could be loaded. Check the Note column for each row.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SetBomStatus($"Resolved {ok}/{ticked}. Loading {ready.Count} parts into the schematic...");
                await AddResolvedToLibraryAsync(vm, ready);
            }
            catch (OperationCanceledException)
            {
                SetBomStatus("Choose All cancelled.");
            }
            catch (Exception ex)
            {
                SetBomStatus($"Choose All failed: {ex.Message}");
                MessageBox.Show($"Choose All failed:\n{ex.Message}", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                UpdateBomActionButtons();
            }
        }

        /// <summary>
        /// Shared back-half of the BOM "add to library" flow, factored out so both the
        /// manual Add to Library button and the Choose All one-click flow use it.
        /// Loads each resolved row's EasyEDA component, stamps BOM annotations, writes
        /// the JLCPCB CSV, and closes the dialog with DialogResult=true so the caller
        /// places the parts on the schematic.
        /// </summary>
        private async Task AddResolvedToLibraryAsync(BomBuilderViewModel vm, List<BomRow> ready)
        {
            addResolvedToLibraryButton.IsEnabled = false;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SelectedComponents.Clear();
                BomResolvedParts.Clear();

                var api = new EasyedaApi();
                foreach (var row in ready)
                {
                    try
                    {
                        var root = await Task.Run(() => api.GetComponentJsonAsync(row.ResolvedLcsc, _bomCts.Token));
                        if (root?.Component == null)
                        {
                            row.ResolutionNote = "No EasyEDA symbol/footprint for this LCSC number.";
                            continue;
                        }
                        var component = root.Component;
                        bool has3d = component.PackageDetail?.Footprint?.GetModel() != null;
                        bool hasFp = component.PackageDetail?.Footprint != null;

                        var partInfo = new EasyedaApi.PartInfo
                        {
                            Name = row.ResolvedLcsc,
                            Part = row.ResolvedLcsc,
                            Description = row.ResolvedDescription ?? row.OriginalValue,
                            HasSymbol = true,
                            HasFootprint = hasFp,
                            Has3d = has3d,
                        };

                        SelectedComponents.Add(new ComponentSelection
                        {
                            PartInfo = partInfo,
                            Root = root,
                            Include3dModel = has3d,
                            IncludeFootprint = hasFp,
                        });

                        BomResolvedParts.Add(new BomResolvedPart
                        {
                            Designator = row.Designator,
                            Lcsc = row.ResolvedLcsc,
                            Package = row.ResolvedPackage ?? row.Package,
                            IsBasic = row.IsBasic,
                            Value = row.OriginalValue,
                        });
                    }
                    catch (Exception ex)
                    {
                        row.ResolutionNote = $"Load failed: {ex.Message}";
                    }
                }

                try
                {
                    string bomPath = BomExporter.Export(vm.Rows);
                    SetBomStatus($"Added {SelectedComponents.Count} parts to library. JLCPCB BOM CSV written to {bomPath}.");
                }
                catch
                {
                    SetBomStatus($"Added {SelectedComponents.Count} parts to library.");
                }

                if (SelectedComponents.Count > 0)
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("No parts could be loaded into the library from the resolved rows. " +
                                    "Check the Note column for each row.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                UpdateBomActionButtons();
            }
        }

        private async void AddResolvedToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = EnsureBomViewModel();
            var ready = vm.ReadyToOrder.ToList();
            if (ready.Count == 0)
            {
                MessageBox.Show("No resolved rows to add. Click Fetch from JLCPCB first.", "BOM Builder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await AddResolvedToLibraryAsync(vm, ready);
        }
    }

    public class BomResolvedPart
    {
        public string Designator { get; set; }
        public string Lcsc { get; set; }
        public string Package { get; set; }
        public bool IsBasic { get; set; }
        public string Value { get; set; }
    }

    public class PartInfoViewModel : INotifyPropertyChanged
    {
        private bool _addToLibrary;
        private bool _hasFootprint;
        private bool _has3d;
        private readonly DialogWindow _parentWindow;

        public EasyedaApi.PartInfo PartInfo { get; }

        public bool AddToLibrary
        {
            get => _addToLibrary;
            set
            {
                if (_addToLibrary != value)
                {
                    _addToLibrary = value;
                    OnPropertyChanged(nameof(AddToLibrary));
                    _parentWindow?.UpdateAddButtonState();
                }
            }
        }

        public bool HasFootprint
        {
            get => _hasFootprint;
            set
            {
                if (_hasFootprint != value)
                {
                    _hasFootprint = value;
                    OnPropertyChanged(nameof(HasFootprint));
                }
            }
        }

        public bool Has3d
        {
            get => _has3d;
            set
            {
                if (_has3d != value)
                {
                    _has3d = value;
                    OnPropertyChanged(nameof(Has3d));
                }
            }
        }

        public string Name => PartInfo.Name ?? PartInfo.Part;
        public string Description => PartInfo.Description ?? "";
        public string PartNumber => PartInfo.Part ?? "";
        public string Package => PartInfo.Package ?? "";
        public int Stock => PartInfo.Stock;
        public string LibraryType =>
            string.IsNullOrWhiteSpace(PartInfo.LibraryType)
                ? ""
                : (PartInfo.IsBasic ? "Basic" : "Extended");
        public string StockDisplay => PartInfo.Stock > 0 ? PartInfo.Stock.ToString("N0") : "";

        public PartInfoViewModel(EasyedaApi.PartInfo partInfo, DialogWindow parentWindow)
        {
            PartInfo = partInfo;
            _parentWindow = parentWindow;
            _hasFootprint = partInfo.HasFootprint;
            _has3d = partInfo.Has3d;
            _addToLibrary = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
