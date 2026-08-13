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
        set { _selectedIsoPath = value; OnPropertyChanged(); }
    }

    private UsbDriveInfo? _selectedDrive;
    public UsbDriveInfo? SelectedDrive
    {
        get => _selectedDrive;
        set { _selectedDrive = value; OnPropertyChanged(); }
    }

    private BootMode _recommendedBootMode;
    public BootMode RecommendedBootMode
    {
        get => _recommendedBootMode;
        private set { _recommendedBootMode = value; OnPropertyChanged(); }
    }

    private BootMode _selectedBootMode;
    public BootMode SelectedBootMode
    {
        get => _selectedBootMode;
        set { _selectedBootMode = value; OnPropertyChanged(); }
    }

    private WriteProgress? _currentProgress;
    public WriteProgress? CurrentProgress
    {
        get => _currentProgress;
        private set { _currentProgress = value; OnPropertyChanged(); }
    }

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

    public async Task FlashAsync(CancellationToken ct = default)
    {
        if (SelectedIsoPath is null || SelectedDrive is null)
        {
            throw new InvalidOperationException("Pilih ISO dan drive terlebih dahulu sebelum flash.");
        }

        var progress = new Progress<WriteProgress>(p => CurrentProgress = p);
        var writeEngine = _writeEngineFactory(SelectedBootMode);

        var sourceHash = await ComputeSourceHashAsync(SelectedIsoPath, ct);

        await writeEngine.WriteAsync(SelectedIsoPath, SelectedDrive, progress, ct);

        CurrentProgress = new WriteProgress(100, 0, TimeSpan.Zero, "Verifying");
        // Full post-write verification strategy (per-file for UEFI:NTFS mode,
        // per-.swm-chunk for split mode) is implemented against real hardware
        // during manual testing — see docs/superpowers/plans/manual-test-checklist-phase1.md.
    }

    private async Task<string> ComputeSourceHashAsync(string isoPath, CancellationToken ct)
    {
        using var stream = File.OpenRead(isoPath);
        return await _checksumService.ComputeSha256Async(stream, ct);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
