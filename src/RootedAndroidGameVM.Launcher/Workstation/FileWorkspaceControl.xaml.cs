using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Launcher.Workstation;

public partial class FileWorkspaceControl : UserControl
{
    private FileWorkspaceViewModel? Model => DataContext as FileWorkspaceViewModel;
    private Window? OwnerWindow => Window.GetWindow(this);
    public FileWorkspaceControl() => InitializeComponent();
    private async Task Guard(Func<FileWorkspaceViewModel, Task> action)
    { if (Model is not { } model) return; try { await action(model); } catch (Exception error) { model.Message = error.Message; } }
    private async void User_Changed(object sender, SelectionChangedEventArgs e)
    { if (Model?.CanChangeScope == true) await Guard(async model => { await model.RefreshApplicationsAsync(); await model.RefreshRootsAsync(); }); }
    private async void Application_Changed(object sender, SelectionChangedEventArgs e)
    { if (Model?.CanChangeScope == true) await Guard(model => model.RefreshRootsAsync()); }
    private async void SystemApps_Click(object sender, RoutedEventArgs e) => await Guard(async model => { await model.RefreshApplicationsAsync(); await model.RefreshRootsAsync(); });
    private async void Shared_Click(object sender, RoutedEventArgs e) => await Guard(async model => { model.SelectedApplication = null; await model.RefreshRootsAsync(); });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Guard(model => model.InitializeAsync());
    private async void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is FileTreeNode node) await Guard(model => model.OpenNodeAsync(node)); }
    private async void Tree_Expanded(object sender, RoutedEventArgs e) { if (e.OriginalSource is TreeViewItem { DataContext: FileTreeNode node }) await Guard(model => model.ExpandAsync(node)); }
    private async void Back_Click(object sender, RoutedEventArgs e) => await Guard(model => model.BackAsync());
    private async void Up_Click(object sender, RoutedEventArgs e) => await Guard(model => model.UpAsync());
    private async void More_Click(object sender, RoutedEventArgs e) => await Guard(model => model.LoadMoreAsync());
    private async void Address_Click(object sender, RoutedEventArgs e) => await Guard(model => model.OpenAddressAsync());
    private async void Address_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Guard(model => model.OpenAddressAsync()); } }
    private async void Breadcrumb_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is FileBreadcrumb crumb) await Guard(model => model.OpenBreadcrumbAsync(crumb)); }
    private void Files_Selected(object sender, SelectionChangedEventArgs e) => Model?.SetSelection(FileTable.SelectedItems.OfType<FileEntryRow>());
    private async void Files_DoubleClick(object sender, MouseButtonEventArgs e) { if (FileTable.SelectedItem is FileEntryRow row) await Guard(model => model.OpenEntryAsync(row)); }
    private async void Files_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && FileTable.SelectedItem is FileEntryRow row) { e.Handled = true; await Guard(model => model.OpenEntryAsync(row)); } }
    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择上传到当前目录的文件", Multiselect = true };
        if (picker.ShowDialog(OwnerWindow) == true && Model?.CanUpload == true) await Prepare(Model.UploadRequest(picker.FileNames), Model.Address);
    }
    private async void UploadFolders_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择上传的文件夹", Multiselect = true };
        if (picker.ShowDialog(OwnerWindow) == true && Model?.CanUpload == true) await Prepare(Model.UploadRequest(picker.FolderNames), Model.Address);
    }
    private async void Download_Click(object sender, RoutedEventArgs e) => await Download(false);
    private async void ExportDirectory_Click(object sender, RoutedEventArgs e) => await Download(true);
    private async Task Download(bool current)
    {
        if (Model is not { } model) return;
        var picker = new OpenFolderDialog { Title = "选择电脑上的保存位置" };
        if (picker.ShowDialog(OwnerWindow) == true) await Prepare(model.DownloadRequest(picker.FolderName, current), picker.FolderName);
    }
    private async void ExportApp_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var choices = model.Roots.Where(root => root.Kind != "shared").Select(root => new ExportRootChoice(root)).ToArray();
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "选择导出的应用数据目录", FontSize = 20, Margin = new Thickness(0, 0, 0, 15) });
        foreach (var choice in choices)
        {
            var checkbox = new CheckBox { Content = choice.Title, IsChecked = choice.Selected, IsEnabled = choice.Root.Accessible, Margin = new Thickness(0, 5, 0, 5), Tag = choice };
            checkbox.Click += (_, _) => choice.Selected = checkbox.IsChecked == true; panel.Children.Add(checkbox);
        }
        var window = new Window { Owner = OwnerWindow, Title = "导出应用数据", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        var next = new Button { Content = "选择保存位置…", Margin = new Thickness(0, 15, 0, 0), Padding = new Thickness(12, 8, 12, 8), IsDefault = true };
        next.Click += (_, _) => { if (choices.Any(choice => choice.Selected)) window.DialogResult = true; }; panel.Children.Add(next);
        if (window.ShowDialog() != true) return;
        var picker = new OpenFolderDialog { Title = "选择应用数据的保存位置" };
        if (picker.ShowDialog(OwnerWindow) == true) await Prepare(model.DownloadRequest(picker.FolderName, roots: choices.Where(choice => choice.Selected).Select(choice => choice.Root)), picker.FolderName);
    }
    private async Task Prepare(DebugRequest request, string destination)
    {
        await Guard(async model =>
        {
            while (true)
            {
                var result = await model.PrepareAsync(request); if (result is not { } summary) return;
                var review = new TransferReviewModel(summary, destination, model.Applications);
                if (review.NeedsReview)
                {
                    var dialog = new TransferReviewWindow(review) { Owner = OwnerWindow };
                    if (dialog.ShowDialog() != true) return;
                    if (dialog.UseArchive)
                    {
                        var arguments = request.Arguments is null ? new Dictionary<string, JsonElement>() : new(request.Arguments);
                        arguments["format"] = JsonSerializer.SerializeToElement("tar"); request = request with { Arguments = arguments }; continue;
                    }
                }
                await model.ExecuteAsync(review.PlanId, review.NeedsReview ? review.Policy.Value : "fail", review.StopApplications, "gui-" + Guid.NewGuid().ToString("N"));
                return;
            }
        });
    }
    private void Files_DragOver(object sender, DragEventArgs e) { e.Handled = true; e.Effects = Model?.CanUpload == true && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None; }
    private async void Files_Drop(object sender, DragEventArgs e)
    { e.Handled = true; if (Model?.CanUpload == true && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await Prepare(Model.UploadRequest(paths), Model.Address); }
    private async void History_Click(object sender, RoutedEventArgs e) => await Guard(model => model.RefreshTransfersAsync());
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (Model?.SelectedTransfer is { } transfer) await Guard(model => model.ResumeAsync(transfer)); }
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Guard(model => model.CancelAsync());
    private void OpenRecords_Click(object sender, RoutedEventArgs e)
    { if (Model?.SelectedTransfer is { } row && Directory.Exists(row.ArtifactDirectory)) Process.Start(new ProcessStartInfo(row.ArtifactDirectory) { UseShellExecute = true }); }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await Guard(async model =>
    {
        if (await model.InspectSelectedAsync() is not { } data) return;
        var text = new TextBox { Text = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), IsReadOnly = true, AcceptsReturn = true, Margin = new Thickness(15), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        new Window { Owner = OwnerWindow, Title = "传输结果与备份记录", Width = 850, Height = 600, Content = text }.Show();
    });
}
