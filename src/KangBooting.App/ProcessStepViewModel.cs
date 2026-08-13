using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KangBooting.App;

public enum ProcessStepStatus { Pending, Active, Done }

// One row in the flash-progress checklist. Status is derived from the overall percent
// complete crossing this step's threshold - simpler and more robust than matching each
// IWriteEngine's exact operation-label text, which differs between UEFI:NTFS and
// Legacy+Split FAT32 (and between the mount-first and DiscUtils-fallback code paths).
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

    public ProcessStepViewModel(string label, double thresholdEnd)
    {
        Label = label;
        ThresholdEnd = thresholdEnd;
    }

    public void UpdateForPercent(double percent, double thresholdStart)
    {
        Status = percent >= ThresholdEnd
            ? ProcessStepStatus.Done
            : percent >= thresholdStart
                ? ProcessStepStatus.Active
                : ProcessStepStatus.Pending;
    }

    public void Reset() => Status = ProcessStepStatus.Pending;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
