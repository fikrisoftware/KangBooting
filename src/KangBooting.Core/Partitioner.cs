using DiscUtils.Fat;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Raw;

namespace KangBooting.Core;

// ponytail: implemented directly against the real DiscUtils 0.16.13 API (verified by
// spiking against throwaway VHD-backed disks) rather than the plan's brief pseudocode,
// which assumed methods (CreateWholeDiskPartition, PartitionInfo.SetActive(),
// NtfsFileSystem.Format(stream,label)) that do not exist in the installed package.
// See task-8-report.md for the full trail of what was verified and why.
public class Partitioner : IPartitioner
{
    private const int SectorSize = 512;

    // UEFI:NTFS boot partition holds only a small FAT-formatted loader image
    // (assets/uefi-ntfs.img, written verbatim by WriteBootloaderImageAsync).
    private const long BootPartitionBytes = 1 * 1024 * 1024;

    // 1MiB alignment for the first partition start — standard modern practice
    // (matches Windows/diskpart/GPT default alignment), avoids relying on the
    // library's CHS/cylinder rounding for such a small partition.
    private const long AlignmentSectors = 2048;

    public Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        using var disk = new Disk(target.DeviceId, FileAccess.ReadWrite);

        // Initialize(disk) alone writes an empty MBR with zero partitions.
        // (Initialize(disk, WellKnownPartitionType) would immediately consume the
        // whole disk with one partition, leaving no room for a second.)
        var table = BiosPartitionTable.Initialize(disk);

        long totalSectors = disk.Capacity / SectorSize;
        long bootSectorCount = BootPartitionBytes / SectorSize;

        long bootFirstSector = AlignmentSectors;
        long bootLastSector = bootFirstSector + bootSectorCount - 1;
        long dataFirstSector = bootLastSector + 1;
        long dataLastSector = totalSectors - 1;

        // CreatePrimaryBySector places partitions at exact LBAs, sidestepping
        // BiosPartitionTable.Create(size,...)'s cylinder rounding (which, for large
        // disks with large BIOS-translated cylinders, rounds a 1MiB request down to
        // zero cylinders and throws). BiosType bytes chosen to match what
        // BiosPartitionTable's own WellKnownPartitionType conversion would pick for
        // partitions of these sizes (Fat16 for the tiny boot partition, Ntfs for data).
        int bootIndex = table.CreatePrimaryBySector(
            bootFirstSector, bootLastSector, BiosPartitionTypes.Fat16, markActive: true);
        int dataIndex = table.CreatePrimaryBySector(
            dataFirstSector, dataLastSector, BiosPartitionTypes.Ntfs, markActive: false);

        using (var dataStream = table.Partitions[dataIndex].Open())
        {
            NtfsFileSystem.Format(dataStream, "KANGBOOT", disk.Geometry, 0, dataStream.Length / SectorSize);
        }

        var result = (
            new PartitionHandle(target.DeviceId, bootIndex),
            new PartitionHandle(target.DeviceId, dataIndex));

        return Task.FromResult(result);
    }

    public Task<PartitionHandle> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        using var disk = new Disk(target.DeviceId, FileAccess.ReadWrite);

        // Initialize(disk, type) creates a single partition spanning the entire disk
        // and already marks it active — no separate SetActive step needed/available.
        var table = BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsFat);
        const int index = 0;

        FatFileSystem.FormatPartition(disk, index, "KANGBOOT");

        return Task.FromResult(new PartitionHandle(target.DeviceId, index));
    }

    public async Task WriteBootloaderImageAsync(
        PartitionHandle partition, string imagePath, CancellationToken ct = default)
    {
        using var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        using var partitionStream = table.Partitions[partition.PartitionIndex].Open();
        using var imageStream = File.OpenRead(imagePath);
        await imageStream.CopyToAsync(partitionStream, ct);
    }

    public NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        return new NtfsFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }

    public FatFileSystem OpenFat32FileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        return new FatFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }
}
