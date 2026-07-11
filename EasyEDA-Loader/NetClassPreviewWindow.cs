using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Preview dialog for net-class assignments before applying them to the PCB.
    /// Shows every net with a class dropdown; user can reassign per-net, then Apply.
    /// </summary>
    public sealed class NetClassPreviewWindow : Window
    {
        private readonly ObservableCollection<NetRow> _rows = new ObservableCollection<NetRow>();
        private readonly List<string> _classOrder;
        private TextBlock _countsText;
        private TextBox _filterBox;
        private DataGrid _grid;

        /// <summary>The assignments the user accepted. Null if cancelled.</summary>
        public Dictionary<string, List<string>> ResultAssignments { get; private set; }

        public NetClassPreviewWindow(Dictionary<string, List<string>> initialAssignments, IEnumerable<string> classOrder)
        {
            _classOrder = classOrder.ToList();

            foreach (var cls in _classOrder)
            {
                if (!initialAssignments.TryGetValue(cls, out var nets))
                    continue;
                foreach (var net in nets)
                    _rows.Add(new NetRow { NetName = net, ClassName = cls });
            }

            Title = "Net Class Preview";
            Height = 560;
            Width = 540;
            MinHeight = 360;
            MinWidth = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            BuildUi();
            UpdateCounts();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // filter
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

            var header = new TextBlock
            {
                Text = "Review net-class assignments before applying. Change any net's class with the dropdown.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _countsText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            };
            // Build the header stack first, then add it to the grid. Do NOT add the
            // individual children to the grid and then move them — WPF throws
            // "Specified element is already the logical child of another element".
            var headerStack = new StackPanel();
            headerStack.Children.Add(header);
            headerStack.Children.Add(_countsText);
            Grid.SetRow(headerStack, 0);
            root.Children.Add(headerStack);

            var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            filterPanel.Children.Add(new TextBlock
            {
                Text = "Filter: ",
                VerticalAlignment = VerticalAlignment.Center,
            });
            _filterBox = new TextBox { Width = 240 };
            _filterBox.TextChanged += (s, e) => ApplyFilter();
            filterPanel.Children.Add(_filterBox);
            Grid.SetRow(filterPanel, 1);
            root.Children.Add(filterPanel);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                IsReadOnly = false,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = CollectionViewSource.GetDefaultView(_rows),
            };

            var netCol = new DataGridTextColumn
            {
                Header = "Net",
                Binding = new Binding("NetName"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            };

            // Single always-visible ComboBox template (no need to enter edit mode).
            var classCol = new DataGridTemplateColumn { Header = "Class", Width = new DataGridLength(120) };
            classCol.CellTemplate = MakeClassCellTemplate();

            _grid.Columns.Add(netCol);
            _grid.Columns.Add(classCol);
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            footer.Children.Add(new TextBlock
            {
                Text = "Empty classes are skipped on Apply. Edit pcb-rules-profile.json to tune tokens.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
            });
            var applyButton = new Button { Content = "Apply", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            applyButton.Click += ApplyButton_Click;
            var cancelButton = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            footer.Children.Add(applyButton);
            footer.Children.Add(cancelButton);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
        }

        private DataTemplate MakeClassCellTemplate()
        {
            // Always-visible ComboBox bound two-way to ClassName; updates counts on selection change.
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            factory.SetValue(ComboBox.ItemsSourceProperty, _classOrder);
            factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("ClassName") { Mode = BindingMode.TwoWay });
            factory.SetValue(ComboBox.MarginProperty, new Thickness(2));
            factory.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(ClassCombo_SelectionChanged));
            template.VisualTree = factory;
            template.Seal();
            return template;
        }

        private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ComboBox raises this during template construction with null sender args; ignore those.
            if (e.AddedItems == null || e.AddedItems.Count == 0)
                return;
            UpdateCounts();
        }

        private void ApplyFilter()
        {
            var view = CollectionViewSource.GetDefaultView(_rows);
            var filter = (_filterBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj =>
                {
                    var row = obj as NetRow;
                    if (row == null)
                        return false;
                    return row.NetName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || row.ClassName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            }
        }

        private void UpdateCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cls in _classOrder)
                counts[cls] = 0;
            foreach (var row in _rows)
            {
                if (counts.ContainsKey(row.ClassName))
                    counts[row.ClassName]++;
            }
            _countsText.Text = string.Join("    ", _classOrder.Select(c => $"{c}: {counts[c]}"));
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var assignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cls in _classOrder)
                assignments[cls] = new List<string>();

            foreach (var row in _rows)
            {
                if (string.IsNullOrWhiteSpace(row.NetName))
                    continue;
                if (assignments.ContainsKey(row.ClassName))
                    assignments[row.ClassName].Add(row.NetName);
            }

            foreach (var pair in assignments)
                pair.Value.Sort(StringComparer.OrdinalIgnoreCase);

            ResultAssignments = assignments;
            DialogResult = true;
            Close();
        }
    }

    public sealed class NetRow : INotifyPropertyChanged
    {
        private string _className;

        public string NetName { get; set; }

        public string ClassName
        {
            get => _className;
            set
            {
                if (!string.Equals(_className, value, StringComparison.OrdinalIgnoreCase))
                {
                    _className = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
