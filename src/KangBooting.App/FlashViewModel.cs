using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KangBooting.Core;

namespace KangBooting.App;

public class FlashViewModel : INotifyPropertyChanged
{
    private readonly IIsoInspector _isoInspector;
    private readonly IDriveService _driveService;
    private readonly IChecksumService _checksumService;
    private readonly Func<BootMode, IWriteEngine> _writeEngineFactory;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UsbDriveInfo> AvailableDrives { get; } = new();

    private string? _selectedIsoPath;
    public string? SelectedIsoPath
    {
        get => _selectedIsoPath;
        set => SetField(ref _selectedIsoPath, value);
    }

    private UsbDriveInfo? _selectedDrive;
    public UsbDriveInfo? SelectedDrive
    {
        get => _selectedDrive;
        set => SetField(ref _selectedDrive, value);
    }

    private BootMode _recommendedBootMode;
    public BootMode RecommendedBootMode
    {
        get => _recommendedBootMode;
        private set => SetField(ref _recommendedBootMode, value);
    }

    private BootMode _selectedBootMode;
    public BootMode SelectedBootMode
    {
        get => _selectedBootMode;
        set => SetField(ref _selectedBootMode, value);
    }

    private WriteProgress? _currentProgress;
    public WriteProgress? CurrentProgress
    {
        get => _currentProgress;
        private set
        {
            if (SetField(ref _currentProgress, value))
            {
                OnPropertyChanged(nameof(ProgressLabel));
            }
        }
    }

    // Combines the operation name and percent into one bindable string, since the UI
    // previously bound only the ProgressBar's numeric Value — the operation label and
    // percent text were never actually shown anywhere.
    public string ProgressLabel => CurrentProgress is null
        ? ""
        : $"{CurrentProgress.CurrentOperation} - {CurrentProgress.PercentComplete:F0}%";

    public FlashViewModel(
        IIsoInspector isoInspector,
        IDriveService driveService,
        IChecksumService checksumService,
        Func<BootMode, IWriteEngine> writeEngineFactory)
    {
        _isoInspector = isoInspector;
        _driveService = driveService;
        _checksumService = checksumService;
        _writeEngineFactory = writeEngineFactory;
    }

    public void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var drive in _driveService.EnumerateUsbDrives())
        {
            AvailableDrives.Add(drive);
        }
    }

    public async Task LoadIsoAsync(string isoPath, CancellationToken ct = default)
    {
        SelectedIsoPath = isoPath;
        var analysis = await _isoInspector.AnalyzeAsync(isoPath, ct);
        RecommendedBootMode = BootModeRecommender.Recommend(analysis);
        SelectedBootMode = RecommendedBootMode;
    }

    private CancellationTokenSource? _flashCts;

    public void CancelFlash()
    {
        _flashCts?.Cancel();
    }

    public async Task FlashAsync(CancellationToken ct = default)
    {
        _flashCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = _flashCts.Token;
        if (SelectedIsoPath is null || SelectedDrive is null)
        {
            throw new InvalidOperationException("Pilih ISO dan drive terlebih dahulu sebelum flash.");
        }

        // I6: fail fast, before wiping the drive, if it's clearly too small for the ISO.
        // +2MB margin covers Legacy mode's boot partition overhead; UEFI:NTFS overhead
        // is smaller still, so the same margin is safe for both modes.
        var isoSizeBytes = new FileInfo(SelectedIsoPath).Length;
        if (SelectedDrive.SizeBytes < isoSizeBytes + 2_000_000)
        {
            throw new InvalidOperationException("Ukuran drive terlalu kecil untuk ISO ini.");
        }

        var progress = new Progress<WriteProgress>(p => CurrentProgress = p);
        var writeEngine = _writeEngineFactory(SelectedBootMode);
        var isoPath = SelectedIsoPath;
        var drive = SelectedDrive;

        try
        {
            // ponytail: write pipeline (Partitioner + engine copy loops) is synchronous I/O
            // under the hood; Task.Run moves it off the caller's (UI) thread. IProgress<T>
            // still marshals back to that thread automatically.
            await Task.Run(() => writeEngine.WriteAsync(isoPath, drive, progress, ct), ct);

            // Post-write verification is not implemented in Phase 1 (full per-file/
            // per-chunk checksum verification is a larger feature, out of scope here).
            // _checksumService/ComputeSourceHashAsync are kept as infrastructure for
            // when that's built; nothing currently calls them.
        }
        finally
        {
            _flashCts?.Dispose();
            _flashCts = null;
        }
    }

    private async Task<string> ComputeSourceHashAsync(string isoPath, CancellationToken ct)
    {
        using var stream = File.OpenRead(isoPath);
        return await _checksumService.ComputeSha256Async(stream, ct);
    }

    // Guards against re-entrant set->PropertyChanged->binding-writes-back->set loops
    // (e.g. a ComboBox two-way-bound to SelectedDrive recursing into a
    // StackOverflowException when the setter unconditionally re-raised
    // PropertyChanged even for an unchanged value).
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
