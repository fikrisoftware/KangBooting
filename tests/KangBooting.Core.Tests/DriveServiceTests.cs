using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class DriveServiceTests
{
    [Fact]
    public void UsbDriveInfo_StoresAllFields()
    {
        var info = new UsbDriveInfo(
            DeviceId: @"\\.\PHYSICALDRIVE1",
            DisplayName: "SanDisk USB Device",
            SizeBytes: 32L * 1024 * 1024 * 1024,
            CurrentFileSystem: "FAT32");

        Assert.Equal(@"\\.\PHYSICALDRIVE1", info.DeviceId);
        Assert.Equal(32L * 1024 * 1024 * 1024, info.SizeBytes);
        Assert.Equal("FAT32", info.CurrentFileSystem);
    }

    // EnumerateUsbDrives() and LockVolume() require real USB hardware and Windows
    // WMI/kernel access — they are covered by manual hardware testing (see Task 9
    // of the implementation plan), not by this automated suite.
}
