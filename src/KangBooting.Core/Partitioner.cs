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

    // UEFI:NTFS boot partition is a small FAT12/16-formatted EFI system partition
    // holding just the bootloader binary at EFI\Boot\bootx64.efi (placed by
    // WriteBootloaderImageAsync), which chain-loads into the NTFS data partition.
    // Root-cause fix (confirmed on real hardware + reproduced via a throwaway probe):
    // 1MiB was too small for DiscUtils' FatFileSystem.FormatPartition(disk, index,
    // label) convenience overload, which threw ArgumentException("Requested size is
    // too small for a partition") — the on-disk partition was created (correct size)
    // but left unformatted ("Unknown" filesystem type in Windows) because formatting
    // failed right after. Probed 1/2/4/8/16/32 MiB against the real DiscUtils 0.16.13
    // FAT formatter: 1/2/4 MiB fail, 8 MiB is the minimum that succeeds. 16 MiB gives
    // headroom above that measured threshold — still negligible relative to any USB
    // drive's capacity.
    internal const long BootPartitionBytes = 16 * 1024 * 1024;

    // 1MiB alignment for the first partition start — standard modern practice
    // (matches Windows/diskpart/GPT default alignment), avoids relying on the
    // library's CHS/cylinder rounding for such a small partition.
    private const long AlignmentSectors = 2048;

    // Root-cause fix (confirmed on real hardware): Open*FileSystem previously left the
    // backing Disk device handle open with no way to close it (see the comment above
    // those methods for why). This was flagged as a known limitation, then reproduced
    // live: clicking "Coba Lagi" (Retry) within the same running process failed with
    // "The process cannot access the file '\\.\PHYSICALDRIVEn' because it is being
    // used by another process" — the FIRST attempt's leaked Disk handle was still open,
    // blocking the retry's own attempt to open the same physical disk. Tracking every
    // opened Disk here and releasing them via ReleaseOpenDisks() (called from each
    // IWriteEngine's finally block) closes that gap without changing IPartitioner's
    // Open*FileSystem return types.
    private readonly List<Disk> _openDisks = new();

    public void ReleaseOpenDisks()
    {
        foreach (var disk in _openDisks)
        {
            disk.Dispose();
        }

        _openDisks.Clear();
    }

    public Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var result = CreateUefiNtfsLayout(target);
        RefreshPartitionTable(target.DeviceId);
        return Task.FromResult(result);
    }

    private (PartitionHandle bootPartition, PartitionHandle dataPartition) CreateUefiNtfsLayout(UsbDriveInfo target)
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

        // DiscUtils' FAT formatter auto-selects FAT12/16 for a partition this small
        // (~1MiB) - correct and expected for an EFI system partition of this size.
        // Same FatFileSystem.FormatPartition(disk, index, label) pattern already used
        // for the Legacy FAT32 partition below.
        FatFileSystem.FormatPartition(disk, bootIndex, "KANGBOOT");

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

        return (
            new PartitionHandle(target.DeviceId, bootIndex),
            new PartitionHandle(target.DeviceId, dataIndex));
    }

    public Task<PartitionHandle> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var result = CreateLegacyFat32Layout(target);
        RefreshPartitionTable(target.DeviceId);
        return Task.FromResult(result);
    }

    private PartitionHandle CreateLegacyFat32Layout(UsbDriveInfo target)
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

        return new PartitionHandle(target.DeviceId, index);
    }

    // I2 fix: after DiscUtils writes a new partition table directly to the raw disk,
    // Windows' own view of the disk (and any drive-letter/volume assignment downstream
    // code depends on, e.g. DriveService.GetDriveLetterForPartition) is stale until it
    // re-reads the partition table. IOCTL_DISK_UPDATE_PROPERTIES forces that re-read.
    // Untested on real hardware — see manual-test-checklist-phase1.md.
    private static void RefreshPartitionTable(string deviceId)
    {
        using var handle = NativeMethods.CreateFile(
            deviceId,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            // Best-effort: if we can't even open the disk here, downstream code
            // (drive-letter resolution, next write) will surface a clearer error.
            return;
        }

        // Capture (rather than discard) the result: a failed refresh isn't fatal on its
        // own — Windows may still pick up the new partition table another way — but if
        // it fails, the drive-letter retry loop in LegacySplitWriter that runs right
        // after this is far more likely to time out, and that's the first place a user
        // sees an error. No logging framework here, so this is a one-line note rather
        // than threading the flag through the frozen IPartitioner return types.
        bool refreshed = NativeMethods.DeviceIoControl(
            handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        _ = refreshed;
    }

    public Task WriteBootloaderImageAsync(
        PartitionHandle partition, string bootloaderPath, CancellationToken ct = default)
    {
        using var fat = OpenFat32FileSystem(partition);
        using var bootloaderStream = File.OpenRead(bootloaderPath);
        PlaceBootloader(fat, bootloaderStream);
        return Task.CompletedTask;
    }

    // Places the EFI bootloader at the fixed path UEFI firmware probes by default
    // when no other boot entry is configured: EFI\Boot\bootx64.efi.
    internal static void PlaceBootloader(DiscUtils.IFileSystem fat, Stream bootloaderContent)
    {
        fat.CreateDirectory(@"EFI\Boot");
        using var destStream = fat.OpenFile(@"EFI\Boot\bootx64.efi", FileMode.Create, FileAccess.Write);
        bootloaderContent.CopyTo(destStream);
    }

    // Both Open*FileSystem methods return a bare NtfsFileSystem/FatFileSystem (per the
    // IPartitioner contract) with no hook for the caller to dispose the Disk backing its
    // stream. Rather than leak it, the Disk is tracked in _openDisks and released via
    // ReleaseOpenDisks() (called from each IWriteEngine's finally block) once the
    // returned filesystem is no longer needed.
    public NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        _openDisks.Add(disk);
        var table = new BiosPartitionTable(disk);
        return new NtfsFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }

    public FatFileSystem OpenFat32FileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        _openDisks.Add(disk);
        var table = new BiosPartitionTable(disk);
        return new FatFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }
}
