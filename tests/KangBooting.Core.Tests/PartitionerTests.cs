using System.Text;
using DiscUtils.Fat;
using DiscUtils.Streams;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class PartitionerTests
{
    [Fact]
    public void PlaceBootloader_WritesFileAtEfiBootPath()
    {
        // Arrange: a small in-memory FAT filesystem, standing in for the formatted
        // UEFI:NTFS boot partition, and a fake bootloader binary's bytes.
        var fatStream = new SparseMemoryStream();
        FatFileSystem.FormatPartition(fatStream, "KANGBOOT", new DiscUtils.Geometry(1, 1, 1), 0, 8 * 1024 * 1024 / 512, 1);
        using var fat = new FatFileSystem(fatStream);

        var bootloaderBytes = Encoding.ASCII.GetBytes("fake uefi bootloader content");

        // Act
        Partitioner.PlaceBootloader(fat, new MemoryStream(bootloaderBytes));

        // Assert: content lands at the fixed default UEFI probe path.
        Assert.True(fat.FileExists(@"EFI\Boot\bootx64.efi"));
        using var written = fat.OpenFile(@"EFI\Boot\bootx64.efi", FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(written);
        Assert.Equal("fake uefi bootloader content", reader.ReadToEnd());
    }
}
