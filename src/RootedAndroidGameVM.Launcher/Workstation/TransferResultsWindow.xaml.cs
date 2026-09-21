using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Launcher.Workstation;

public partial class TransferResultsWindow : Window
{
    private readonly FileWorkspaceViewModel _model;
    private readonly string _planId;
    private readonly ObservableCollection<TransferResultRow> _rows = [];
    private int? _nextOffset = 0;
    public TransferResultsWindow(FileWorkspaceViewModel model, string planId)
    {
        InitializeComponent(); _model = model; _planId = planId; ResultsTable.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadPage();
    }
    private async Task LoadPage()
    {
        if (_nextOffset is not { } offset) return;
        MoreButton.IsEnabled = false;
        try
        {
            if (await _model.InspectAsync(_planId, offset) is not { } data) { SummaryText.Text = "读取结果失败，可重试加载。"; return; }
            var page = new TransferResultPage(data);
            foreach (var row in page.Rows) _rows.Add(row);
            _nextOffset = page.NextOffset; SummaryText.Text = page.Summary;
            CountText.Text = $"已显示 {_rows.Count} 项 · 任务 {_planId}";
            if (ResultsTable.SelectedItem is null && _rows.Count > 0) ResultsTable.SelectedIndex = 0;
        }
        catch (Exception error) { SummaryText.Text = error.Message; }
        finally { MoreButton.IsEnabled = _nextOffset is not null; }
    }
    private async void More_Click(object sender, RoutedEventArgs e) => await LoadPage();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { if (ResultsTable.SelectedItem is TransferResultRow row) DetailsText.Text = row.Detail; }
}
