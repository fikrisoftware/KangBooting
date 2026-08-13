using KangBooting.Core;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace KangBooting.App;

public sealed partial class MainWindow : Window
{
    public FlashViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var driveService = new DriveService();
        var partitioner = new Partitioner();
        var dismRunner = new DismRunner();
        var bootsectRunner = new BootsectRunner();

        ViewModel = new FlashViewModel(
            isoInspector: new IsoInspector(),
            driveService: driveService,
            checksumService: new ChecksumService(),
            writeEngineFactory: mode => mode == BootMode.UefiNtfs
                ? new UefiNtfsWriter(driveService, partitioner)
                : new LegacySplitWriter(driveService, partitioner, dismRunner, bootsectRunner));

        try
        {
            ViewModel.RefreshDrives();
        }
        catch (Exception ex)
        {
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
    }

    private async void PickIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var isoPath = Win32FileDialog.PickIsoFile(hwnd);
        if (isoPath is null)
        {
            return;
        }

        ErrorTextBlock.Visibility = Visibility.Collapsed;
        IsoPathTextBox.Text = isoPath;
        try
        {
            await ViewModel.LoadIsoAsync(isoPath);
        }
        catch (Exception ex)
        {
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        UefiNtfsRadio.IsChecked = ViewModel.SelectedBootMode == BootMode.UefiNtfs;
        LegacySplitRadio.IsChecked = ViewModel.SelectedBootMode == BootMode.LegacySplitFat32;
    }

    private void BootModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedBootMode = ReferenceEquals(sender, UefiNtfsRadio)
            ? BootMode.UefiNtfs
            : BootMode.LegacySplitFat32;
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e) => await RunFlashAsync();

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await RunFlashAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelFlash();
    }

    private async Task RunFlashAsync()
    {
        FlashButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        try
        {
            await ViewModel.FlashAsync();
        }
        catch (OperationCanceledException)
        {
            ErrorTextBlock.Text = "Proses dibatalkan.";
            ErrorTextBlock.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            FlashButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }
}
