using System.Windows;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Launcher.Workstation;

public partial class TransferReviewWindow : Window
{
    public bool UseArchive { get; private set; }
    public TransferReviewWindow(TransferReviewModel model) { InitializeComponent(); DataContext = model; }
    private void Execute_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Archive_Click(object sender, RoutedEventArgs e) { UseArchive = true; DialogResult = true; }
}
