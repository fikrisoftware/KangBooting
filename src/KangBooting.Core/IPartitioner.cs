namespace KangBooting.Core;

public interface IPartitioner
{
    // Returns the OS-assigned drive letters (e.g. "E:") for the boot and data
    // partitions, already formatted and ready to write to — no separate open/format
    // step needed by callers.
    Task<(string bootDriveLetter, string dataDriveLetter)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);

    // Returns the OS-assigned drive letter (e.g. "E:") for the single FAT32 partition,
    // already formatted.
    Task<string> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);
}
