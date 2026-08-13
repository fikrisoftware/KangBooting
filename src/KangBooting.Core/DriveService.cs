using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace KangBooting.Core;

public interface IDriveService
{
    IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives();
    IDisposable LockVolume(string deviceId);
    string? GetDriveLetterForPartition(string deviceId, int partitionIndex);
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

    // C2 support: resolves a raw physical-disk partition (DiscUtils' PartitionHandle,
    // which only knows DeviceId+PartitionIndex) to the Windows-assigned drive letter,
    // needed to invoke bootsect.exe. Assumes WMI's Win32_DiskPartition "Index" matches
    // DiscUtils' partition creation order (both are 0-based, observed to agree in the
    // single-partition/two-partition layouts this tool creates) — unverified on real
    // hardware. Returns null if no drive letter is assigned yet (e.g. Windows hasn't
    // finished re-reading the partition table — see Partitioner.RefreshPartitionTable).
    public string? GetDriveLetterForPartition(string deviceId, int partitionIndex)
    {
        // See GetFileSystem above: deviceId must be used verbatim, not backslash-escaped.
        using var searcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

        foreach (ManagementObject partition in searcher.Get())
        {
            if (Convert.ToInt32(partition["Index"]) != partitionIndex)
            {
                continue;
            }

            using var logicalSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject logicalDisk in logicalSearcher.Get())
            {
                return (string?)logicalDisk["DeviceID"];
            }
        }

        return null;
    }

    // I2 root-cause fix (confirmed on real hardware): FSCTL_LOCK_VOLUME/
    // FSCTL_DISMOUNT_VOLUME are *volume*-scoped control codes (expect a \\.\X: volume
    // handle), not physical-disk-scoped. The original implementation issued them
    // against the \\.\PHYSICALDRIVEn handle itself, which is a no-op — Windows kept
    // each mounted child volume live, and any subsequent attempt to open the physical
    // disk for raw write (DiscUtils' Disk class in Partitioner) failed with
    // UnauthorizedAccessException ("Access to the path '\\.\PHYSICALDRIVEn' is
    // denied."), reproduced against a real USB drive. The actual fix: enumerate the
    // physical disk's child volumes (same WMI associator pattern as GetFileSystem/
    // GetDriveLetterForPartition), and lock+dismount each one individually — this
    // releases Windows' exclusive claim on the disk's live filesystems, after which
    // opening the physical disk for raw write succeeds.
    public IDisposable LockVolume(string deviceId)
    {
        var volumeHandles = new List<SafeFileHandle>();

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
                var driveLetter = (string?)logicalDisk["DeviceID"];
                if (string.IsNullOrEmpty(driveLetter))
                {
                    continue;
                }

                var volumePath = $@"\\.\{driveLetter}";
                var handle = NativeMethods.CreateFile(
                    volumePath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    DisposeAll(volumeHandles);
                    throw new IOException($"Tidak bisa membuka volume {volumePath}. Volume mungkin sedang dipakai aplikasi lain.");
                }

                bool locked = NativeMethods.DeviceIoControl(
                    handle, NativeMethods.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

                if (!locked)
                {
                    handle.Dispose();
                    DisposeAll(volumeHandles);
                    throw new IOException($"Volume {volumePath} sedang digunakan aplikasi lain, tutup dulu aplikasi yang mengakses drive tersebut.");
                }

                NativeMethods.DeviceIoControl(
                    handle, NativeMethods.FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

                volumeHandles.Add(handle);
            }
        }

        // No child volumes (e.g. brand-new/unpartitioned drive) — nothing to lock,
        // proceed directly to raw physical-disk access.
        return new VolumeLock(volumeHandles);
    }

    private static void DisposeAll(List<SafeFileHandle> handles)
    {
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    private sealed class VolumeLock : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;

        public VolumeLock(List<SafeFileHandle> handles)
        {
            _handles = handles;
        }

        public void Dispose()
        {
            DisposeAll(_handles);
        }
    }
}
