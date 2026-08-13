using KangBooting.Core;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
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

        ViewModel = new FlashViewModel(
            isoInspector: new IsoInspector(),
            driveService: driveService,
            checksumService: new ChecksumService(),
            writeEngineFactory: mode => mode == BootMode.UefiNtfs
                ? new UefiNtfsWriter(driveService, partitioner)
                : new LegacySplitWriter(driveService, partitioner, dismRunner));

        ViewModel.RefreshDrives();
    }

    private async void PickIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".iso");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        IsoPathTextBox.Text = file.Path;
        await ViewModel.LoadIsoAsync(file.Path);

        UefiNtfsRadio.IsChecked = ViewModel.SelectedBootMode == BootMode.UefiNtfs;
        LegacySplitRadio.IsChecked = ViewModel.SelectedBootMode == BootMode.LegacySplitFat32;
    }

    private void BootModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedBootMode = ReferenceEquals(sender, UefiNtfsRadio)
            ? BootMode.UefiNtfs
            : BootMode.LegacySplitFat32;
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        FlashButton.IsEnabled = false;
        try
        {
            await ViewModel.FlashAsync();
        }
        finally
        {
            FlashButton.IsEnabled = true;
        }
    }
}
