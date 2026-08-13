using KangBooting.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace KangBooting.App;

public sealed partial class MainWindow : Window
{
    public FlashViewModel ViewModel { get; }

    // Ticks ElapsedLabel once a second while flashing, so it keeps moving even during a
    // phase that reports no progress events for a while (e.g. dism.exe splitting a large
    // install.wim, or waiting for Windows to assign a drive letter).
    private readonly DispatcherTimer _elapsedTimer;

    public MainWindow()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ViewModel.RefreshTimeDisplay();

        Title = "KangBooting";
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        var driveService = new DriveService();
        var partitioner = new Partitioner();
        var dismRunner = new DismRunner();
        var bootsectRunner = new BootsectRunner();
        var isoMounter = new IsoMounter();

        ViewModel = new FlashViewModel(
            isoInspector: new IsoInspector(),
            driveService: driveService,
            checksumService: new ChecksumService(),
            prerequisiteChecker: new PrerequisiteChecker(),
            writeEngineFactory: mode => mode == BootMode.UefiNtfs
                ? new UefiNtfsWriter(partitioner, isoMounter)
                : new LegacySplitWriter(partitioner, dismRunner, bootsectRunner, isoMounter));

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
        // Per the design spec's data flow: explicit user confirmation before wiping the
        // drive. Skip the dialog if ISO/drive aren't selected yet — let FlashAsync's
        // own validation surface that clearer error instead.
        if (ViewModel.SelectedIsoPath is not null && ViewModel.SelectedDrive is not null)
        {
            var confirmed = await ConfirmFlashAsync(ViewModel.SelectedDrive.DisplayName, ViewModel.SelectedBootMode);
            if (!confirmed)
            {
                return;
            }
        }

        FlashButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        _elapsedTimer.Start();
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
            _elapsedTimer.Stop();
            ViewModel.RefreshTimeDisplay();
            FlashButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> ConfirmFlashAsync(string driveDisplayName, BootMode bootMode)
    {
        var content = $"Semua data di drive \"{driveDisplayName}\" akan dihapus permanen dan tidak bisa dikembalikan.";
        if (bootMode == BootMode.UefiNtfs)
        {
            // Windows refuses to format NTFS on media it classifies "Removable" — most
            // USB flash drives. If this drive is still Removable, KangBooting will flip
            // its own driver-level "RemovableMedia" setting to make Windows treat it as
            // Fixed. This is a permanent change to that specific USB device (not undone
            // by unplugging it, and not specific to this app), so it needs its own
            // explicit heads-up beyond the generic data-wipe warning above.
            content += " Mode UEFI:NTFS juga mungkin perlu mengubah setting drive ini di registry Windows " +
                "(dari 'Removable' menjadi 'Fixed') agar bisa diformat NTFS — perubahan ini permanen pada drive tersebut.";
        }
        content += " Lanjutkan?";

        var dialog = new ContentDialog
        {
            Title = "Konfirmasi Flash",
            Content = content,
            PrimaryButtonText = "Ya, Flash",
            CloseButtonText = "Batal",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
