using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace KangBooting.App;

public enum ProcessStepStatus { Pending, Active, Done }

// One row in the flash-progress checklist. Status is derived from the overall percent
// complete crossing this step's threshold - simpler and more robust than matching each
// IWriteEngine's exact operation-label text, which differs between UEFI:NTFS and
// Legacy+Split FAT32 (and between the mount-first and DiscUtils-fallback code paths).
// Also tracks how long this specific step took (or has been running so far).
public class ProcessStepViewModel : INotifyPropertyChanged
{
    public string Label { get; }
    public double ThresholdEnd { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private ProcessStepStatus _status = ProcessStepStatus.Pending;
    public ProcessStepStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
        }
    }

    // Plain ASCII markers (not a specific icon font) so they render regardless of
    // FontFamily: [x] done, [>] in progress, [ ] pending.
    public string Icon => Status switch
    {
        ProcessStepStatus.Done => "[x]",
        ProcessStepStatus.Active => "[>]",
        _ => "[ ]"
    };

    private readonly Stopwatch _stopwatch = new();
    private TimeSpan? _finalDuration;

    public string DurationLabel => Status switch
    {
        ProcessStepStatus.Active => TimeFormat.Format(_stopwatch.Elapsed),
        ProcessStepStatus.Done when _finalDuration is { } duration => TimeFormat.Format(duration),
        _ => ""
    };

    public ProcessStepViewModel(string label, double thresholdEnd)
    {
        Label = label;
        ThresholdEnd = thresholdEnd;
    }

    public void UpdateForPercent(double percent, double thresholdStart)
    {
        var newStatus = percent >= ThresholdEnd
            ? ProcessStepStatus.Done
            : percent >= thresholdStart
                ? ProcessStepStatus.Active
                : ProcessStepStatus.Pending;

        if (newStatus == Status)
        {
            return;
        }

        if (newStatus == ProcessStepStatus.Active)
        {
            _stopwatch.Restart();
        }
        else if (newStatus == ProcessStepStatus.Done && Status == ProcessStepStatus.Active)
        {
            _stopwatch.Stop();
            _finalDuration = _stopwatch.Elapsed;
        }

        Status = newStatus;
        OnPropertyChanged(nameof(DurationLabel));
    }

    // Called on the same 1-second UI timer that ticks the overall Elapsed display, so
    // the active step's running duration keeps moving, not just its final value once done.
    public void RefreshDuration()
    {
        if (Status == ProcessStepStatus.Active)
        {
            OnPropertyChanged(nameof(DurationLabel));
        }
    }

    public void Reset()
    {
        _stopwatch.Reset();
        _finalDuration = null;
        Status = ProcessStepStatus.Pending;
        OnPropertyChanged(nameof(DurationLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
