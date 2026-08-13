using System.Management;
using System.Runtime.Versioning;

namespace KangBooting.Core;

public interface IDriveService
{
    IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives();
}

[SupportedOSPlatform("windows")]
public class DriveService : IDriveService
{
    public IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives()
    {
        var drives = new List<UsbDriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Caption, Size, InterfaceType FROM Win32_DiskDrive WHERE InterfaceType='USB'");

        foreach (ManagementObject drive in searcher.Get())
        {
            var deviceId = (string)drive["DeviceID"];
            var caption = (string)drive["Caption"];
            var size = drive["Size"] is not null ? Convert.ToInt64(drive["Size"]) : 0L;

            drives.Add(new UsbDriveInfo(
                DeviceId: deviceId,
                DisplayName: caption,
                SizeBytes: size,
                CurrentFileSystem: GetFileSystem(deviceId)));
        }

        return drives;
    }

    private static string GetFileSystem(string deviceId)
    {
        // Partition/logical-disk association query kept separate so it can fail
        // independently without aborting the whole drive listing.
        // NOTE: deviceId is used verbatim (no backslash-doubling) here. Unlike a WQL
        // WHERE-clause string literal, the WMI object-path syntax inside ASSOCIATORS OF
        // {...} does NOT want backslashes escaped — doubling them breaks the path and
        // WMI throws ManagementException "Not found". Verified empirically against a
        // real USB drive: deviceId as-is resolves correctly, deviceId.Replace(@"\", @"\\")
        // does not.
        using var searcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

        foreach (ManagementObject partition in searcher.Get())
        {
            using var logicalSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject logicalDisk in logicalSearcher.Get())
            {
                return (string?)logicalDisk["FileSystem"] ?? "Unknown";
            }
        }

        return "Unknown";
    }
}
