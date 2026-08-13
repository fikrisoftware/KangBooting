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

    // EnumerateUsbDrives() requires real USB hardware and Windows WMI access — it is
    // covered by manual hardware testing (see manual-test-checklist-phase1.md), not by
    // this automated suite.
}
