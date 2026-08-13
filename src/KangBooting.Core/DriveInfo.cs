namespace KangBooting.Core;

public record UsbDriveInfo(
    string DeviceId,
    string DisplayName,
    long SizeBytes,
    string CurrentFileSystem);
