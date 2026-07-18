using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Shows full DRC results with filter + Jump-to-error so the user can fix issues.
    /// </summary>
    internal sealed class DrcResultsWindow : Window
    {
        private readonly ObservableCollection<DrcIssueRow> _rows = new ObservableCollection<DrcIssueRow>();
        private ICollectionView _view;
        private TextBlock _summaryText;
        private TextBox _filterBox;
        private ComboBox _severityBox;
        private DataGrid _grid;
        private TextBlock _detailText;

        public DrcResultsWindow(Dictionary<string, object> report, IEnumerable<DrcIssue> issues)
        {
            Title = "MCP Full PCB DRC";
            Width = 980;
            Height = 640;
            MinWidth = 760;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            foreach (var issue in issues ?? Enumerable.Empty<DrcIssue>())
            {
                _rows.Add(new DrcIssueRow
                {
                    Severity = issue.Severity ?? "warning",
                    Source = issue.Source ?? "",
                    Kind = issue.Kind ?? "",
                    Message = issue.Message ?? "",
                    NetA = issue.NetA ?? "",
                    NetB = issue.NetB ?? "",
                    Layer = issue.Layer ?? "",
                    XMils = Math.Round(issue.XMils, 1),
                    YMils = Math.Round(issue.YMils, 1),
                    Issue = issue,
                });
            }

            BuildUi(report);
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = FilterRow;
            _grid.ItemsSource = _view;
            UpdateFilterStats();
        }

        private void BuildUi(Dictionary<string, object> report)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var pass = report != null && report.TryGetValue("pass", out var p) && p is true;
            var summary = report != null && report.TryGetValue("summary", out var s) ? s?.ToString() : "DRC complete.";
            _summaryText = new TextBlock
            {
                Text = summary,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = pass ? Brushes.DarkGreen : Brushes.DarkRed,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var hint = new TextBlock
            {
                Text = "Altium native batch DRC + MCP extras (power clearance, pad↔track, fab width/neckdown, via↔pad). Select a row and Jump to fix it on the PCB.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var header = new StackPanel();
            header.Children.Add(_summaryText);
            header.Children.Add(hint);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            filterRow.Children.Add(new TextBlock { Text = "Severity:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _severityBox = new ComboBox
            {
                Width = 120,
                ItemsSource = new[] { "All", "critical", "error", "warning" },
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 12, 0),
            };
            _severityBox.SelectionChanged += (_, __) => { _view?.Refresh(); UpdateFilterStats(); };
            filterRow.Children.Add(_severityBox);
            filterRow.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _filterBox = new TextBox { Width = 280, Margin = new Thickness(0, 0, 12, 0) };
            _filterBox.TextChanged += (_, __) => { _view?.Refresh(); UpdateFilterStats(); };
            filterRow.Children.Add(_filterBox);
            var stats = new TextBlock { Name = "stats", VerticalAlignment = VerticalAlignment.Center };
            // keep reference via Tag
            filterRow.Tag = stats;
            filterRow.Children.Add(stats);
            Grid.SetRow(filterRow, 1);
            root.Children.Add(filterRow);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "Sev", Binding = new Binding("Severity"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding("Source"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Kind", Binding = new Binding("Kind"), Width = 120 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Message", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Net A", Binding = new Binding("NetA"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Net B", Binding = new Binding("NetB"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Layer", Binding = new Binding("Layer"), Width = 100 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "X", Binding = new Binding("XMils"), Width = 60 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Y", Binding = new Binding("YMils"), Width = 60 });
            _grid.MouseDoubleClick += (_, __) => JumpSelected();
            _grid.SelectionChanged += (_, __) => ShowDetail();
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            _detailText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
                MinHeight = 36,
            };
            Grid.SetRow(_detailText, 3);
            root.Children.Add(_detailText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var jump = new Button { Content = "Jump to Error", Width = 120, Height = 30, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
            jump.Click += (_, __) => JumpSelected();
            var reRun = new Button { Content = "Re-Run DRC", Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            reRun.Click += ReRun_Click;
            var close = new Button { Content = "Close", Width = 90, Height = 30, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(jump);
            buttons.Children.Add(reRun);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
        }

        private bool FilterRow(object obj)
        {
            if (obj is not DrcIssueRow row)
                return false;
            var sev = (_severityBox?.SelectedItem as string) ?? "All";
            if (!string.Equals(sev, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(row.Severity, sev, StringComparison.OrdinalIgnoreCase))
                return false;

            var q = (_filterBox?.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(q))
                return true;
            return (row.Message ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (row.NetA ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (row.NetB ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (row.Kind ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (row.Layer ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateFilterStats()
        {
            if (_view == null)
                return;
            var visible = 0;
            foreach (var _ in _view)
                visible++;
            if (Content is Grid root &&
                root.Children.OfType<StackPanel>().FirstOrDefault(p => p.Tag is TextBlock) is StackPanel filterRow &&
                filterRow.Tag is TextBlock stats)
            {
                stats.Text = $"Showing {visible} of {_rows.Count}";
            }
        }

        private void ShowDetail()
        {
            if (_grid.SelectedItem is not DrcIssueRow row)
            {
                _detailText.Text = string.Empty;
                return;
            }

            _detailText.Text =
                $"{row.Severity.ToUpperInvariant()} [{row.Source}/{row.Kind}]  " +
                $"@ ({row.XMils}, {row.YMils}) mil  {row.Layer}\n{row.Message}";
        }

        private void JumpSelected()
        {
            if (_grid.SelectedItem is not DrcIssueRow row || row.Issue == null)
            {
                MessageBox.Show(this, "Select an error row first.", "MCP DRC", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                PcbFullDrc.JumpToIssue(row.Issue);
                _detailText.Text = "Jumped to PCB location. Fix the copper, then click Re-Run DRC.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "MCP DRC Jump", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReRun_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = PcbFullDrc.RunFullCheck(runAltiumBatch: true);
                var issues = PcbFullDrc.GetIssuesFromReport(report);
                _rows.Clear();
                foreach (var issue in issues)
                {
                    _rows.Add(new DrcIssueRow
                    {
                        Severity = issue.Severity ?? "warning",
                        Source = issue.Source ?? "",
                        Kind = issue.Kind ?? "",
                        Message = issue.Message ?? "",
                        NetA = issue.NetA ?? "",
                        NetB = issue.NetB ?? "",
                        Layer = issue.Layer ?? "",
                        XMils = Math.Round(issue.XMils, 1),
                        YMils = Math.Round(issue.YMils, 1),
                        Issue = issue,
                    });
                }

                var pass = report.TryGetValue("pass", out var p) && p is true;
                _summaryText.Text = report.TryGetValue("summary", out var s) ? s?.ToString() : "DRC complete.";
                _summaryText.Foreground = pass ? Brushes.DarkGreen : Brushes.DarkRed;
                _view?.Refresh();
                UpdateFilterStats();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "MCP DRC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private sealed class DrcIssueRow : INotifyPropertyChanged
        {
            public string Severity { get; set; }
            public string Source { get; set; }
            public string Kind { get; set; }
            public string Message { get; set; }
            public string NetA { get; set; }
            public string NetB { get; set; }
            public string Layer { get; set; }
            public double XMils { get; set; }
            public double YMils { get; set; }
            public DrcIssue Issue { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
