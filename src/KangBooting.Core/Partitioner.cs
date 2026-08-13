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
        // zero cylinders and throws).
        //
        // Boot partition type: verified against pbatard/rufus (the reference
        // implementation of this exact UEFI:NTFS technique) - src/drive.c sets
        // `DriveLayoutEx.PartitionEntry[i].Mbr.PartitionType = 0xef` for the
        // "UEFI:NTFS" partition even in MBR (non-GPT) mode, with a comment noting
        // they picked EFI System (0xEF) over classic FAT types and it "seems to be
        // okay". DiscUtils exposes this exact byte as BiosPartitionTypes.EfiSystem
        // (0xEF), used here instead of a classic FAT16/FAT12 type byte.
        int bootIndex = table.CreatePrimaryBySector(
            bootFirstSector, bootLastSector, BiosPartitionTypes.EfiSystem, markActive: true);
        int dataIndex = table.CreatePrimaryBySector(
            dataFirstSector, dataLastSector, BiosPartitionTypes.Ntfs, markActive: false);

        using (var dataStream = table.Partitions[dataIndex].Open())
        {
            // firstSector must be the partition's ABSOLUTE start sector on the physical
            // disk, not an offset into this partition-scoped stream. Verified against
            // DiscUtils source (NtfsFileSystem.Format -> NtfsFormatter.FirstSector ->
            // BiosParameterBlock.Initialized(..., (uint)FirstSector, ...)), which feeds
            // straight into the boot sector's BPB HiddenSectors field - the standard
            // NTFS/FAT convention for recording a volume's absolute LBA start so
            // bootloaders/tools can locate it from the volume alone.
            NtfsFileSystem.Format(dataStream, "KANGBOOT", disk.Geometry, dataFirstSector, dataStream.Length / SectorSize);
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

        // Explicit insurance: Initialize(disk, type) is documented/observed to mark the
        // sole partition active as a side effect, but that behavior isn't asserted by
        // the library's public contract. This call is harmless if already active and
        // removes the risk entirely if the side effect ever changes or was wrong.
        table.SetActivePartition(index);

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

    // Both Open*FileSystem methods below intentionally leak the underlying `Disk` handle:
    // the frozen IPartitioner interface returns a bare NtfsFileSystem/FatFileSystem with
    // no hook to dispose the Disk that backs its stream, so the device handle is left
    // open, relying on process exit or GC finalization to release it. This is safe for
    // this tool's short-lived, one-write-per-run CLI usage pattern. It is NOT safe to
    // assume in a long-running process: callers (the app layer) must avoid
    // opening/closing the same physical drive multiple times within one process run
    // without accounting for accumulating open device handles. Fixing this properly
    // requires an IPartitioner interface change (out of scope here).
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
