using System.Text;
using DiscUtils.Fat;
using DiscUtils.Partitions;
using DiscUtils.Raw;
using DiscUtils.Streams;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class PartitionerTests
{
    // Regression test for a real-hardware bug: BootPartitionBytes was 1MiB, which
    // DiscUtils' FatFileSystem.FormatPartition(disk, index, label) convenience overload
    // rejects with ArgumentException("Requested size is too small for a partition") -
    // the partition got created on disk but was left unformatted, surfacing to the user
    // as "Requested size is too small for a partition" and an "Unknown" filesystem type
    // in Windows. This exercises the exact same call path (BiosPartitionTable +
    // FormatPartition(disk, index, label)) at the real production constant, against an
    // in-memory Disk, so a future size regression fails a unit test instead of only
    // surfacing on real hardware.
    [Fact]
    public void BootPartitionBytes_IsLargeEnoughToFormat()
    {
        var totalBytes = Partitioner.BootPartitionBytes + (4 * 1024 * 1024); // + MBR/alignment overhead
        var stream = new SparseMemoryStream();
        stream.SetLength(totalBytes);
        using var disk = new Disk(stream, Ownership.Dispose);

        var table = BiosPartitionTable.Initialize(disk);
        long bootSectorCount = Partitioner.BootPartitionBytes / 512;
        const long bootFirstSector = 2048;
        long bootLastSector = bootFirstSector + bootSectorCount - 1;

        int bootIndex = table.CreatePrimaryBySector(
            bootFirstSector, bootLastSector, BiosPartitionTypes.EfiSystem, markActive: true);

        // Should not throw. Prior to the fix, this line threw
        // ArgumentException("Requested size is too small for a partition") at 1MiB.
        FatFileSystem.FormatPartition(disk, bootIndex, "KANGBOOT");
    }

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
