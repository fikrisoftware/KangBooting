using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace KangBooting.Core;

public interface IDriveService
{
    IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives();
    IDisposable LockVolume(string deviceId);
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
        using var searcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId.Replace(@"\", @"\\")}'}} " +
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

    public IDisposable LockVolume(string deviceId)
    {
        var handle = NativeMethods.CreateFile(
            deviceId,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new IOException($"Tidak bisa membuka drive {deviceId}. Drive mungkin sedang dipakai aplikasi lain.");
        }

        bool locked = NativeMethods.DeviceIoControl(
            handle, NativeMethods.FSCTL_LOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        if (!locked)
        {
            handle.Dispose();
            throw new IOException($"Drive {deviceId} sedang digunakan aplikasi lain, tutup dulu aplikasi yang mengakses drive tersebut.");
        }

        return new VolumeLock(handle);
    }

    private sealed class VolumeLock : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public VolumeLock(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
