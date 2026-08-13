using DiscUtils;

namespace KangBooting.Core;

public class UefiNtfsWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;
    private readonly IIsoMounter _isoMounter;

    public UefiNtfsWriter(IDriveService driveService, IPartitioner partitioner, IIsoMounter isoMounter)
    {
        _driveService = driveService;
        _partitioner = partitioner;
        _isoMounter = isoMounter;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        (PartitionHandle bootPartition, PartitionHandle dataPartition) partitions;
        using (var volumeLock = _driveService.LockVolume(target.DeviceId))
        {
            try
            {
                partitions = await _partitioner.CreateUefiNtfsLayoutAsync(target, ct);

                var bootloaderImagePath = Path.Combine(AppContext.BaseDirectory, "assets", "bootx64_signed.efi");
                await _partitioner.WriteBootloaderImageAsync(partitions.bootPartition, bootloaderImagePath, ct);
            }
            finally
            {
                // An open Disk handle left over from a failed/completed attempt blocks a
                // subsequent Retry (same process) from reopening the same physical disk —
                // confirmed on real hardware in the Legacy mode path; fixed here too for
                // consistency, and so the data-partition drive-letter resolution below
                // isn't blocked by our own still-open formatting handle.
                _partitioner.ReleaseOpenDisks();
            }
        }

        progress.Report(new WriteProgress(10, 0, null, "Copying files"));
        await CopyIsoContentsAsync(isoPath, target, partitions.dataPartition, progress, ct);

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    // Prefer mounting the ISO via Windows' own UDF/ISO9660 driver (IIsoMounter) over
    // reading it through DiscUtils (IsoFileSystemOpener) and prefer writing through the
    // NTFS partition's real Windows-assigned drive letter over DiscUtils' NtfsFileSystem
    // writer — see LegacySplitWriter's equivalent comment for the specific DiscUtils bugs
    // (a near-empty UDF disc read via ISO9660 alone; "Invalid path" on valid multi-dot
    // Windows filenames in its FAT writer) this sidesteps by using the OS's own
    // filesystem drivers on both ends. Falls back to the DiscUtils-based direct copy only
    // if native ISO mounting fails, or if the NTFS partition doesn't get a drive letter.
    private async Task CopyIsoContentsAsync(
        string isoPath, UsbDriveInfo target, PartitionHandle dataPartition,
        IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var dataDriveLetter = await DriveLetterResolver.ResolveWithRetryAsync(
            _driveService, target.DeviceId, dataPartition.PartitionIndex, ct);

        var mount = dataDriveLetter is not null ? await TryGetOrMountAsync(isoPath, ct) : null;
        if (mount is null)
        {
            await CopyViaDiscUtilsAsync(isoPath, dataPartition, progress, ct);
            return;
        }

        var (mountedDriveLetter, weMountedIt) = mount.Value;
        try
        {
            var sourceRoot = mountedDriveLetter + @"\";
            var totalBytes = RealFileSystemCopier.ComputeTotalBytes(sourceRoot);
            var tracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 88, "Copying files", totalBytes);
            RealFileSystemCopier.CopyDirectory(sourceRoot, dataDriveLetter!, tracker, ct);
        }
        finally
        {
            if (weMountedIt)
            {
                // Best-effort on a fixed token: dismounting is cleanup, not part of the
                // operation being cancelled, so it should still run even if `ct` fired.
                await _isoMounter.DismountAsync(isoPath, CancellationToken.None);
            }
        }
    }

    // Checks for an already-mounted instance first (never double-mount the same ISO).
    // Returns null if mounting isn't possible, signalling the caller to fall back to
    // DiscUtils-based reading/writing.
    private async Task<(string driveLetter, bool weMountedIt)?> TryGetOrMountAsync(string isoPath, CancellationToken ct)
    {
        try
        {
            var existing = await _isoMounter.GetExistingMountedDriveLetterAsync(isoPath, ct);
            if (existing is not null)
            {
                return (existing, false);
            }

            var mounted = await _isoMounter.MountAsync(isoPath, ct);
            return (mounted, true);
        }
        catch
        {
            return null;
        }
    }

    // Fallback used if native ISO mounting or drive-letter resolution isn't available:
    // reads via DiscUtils (UDF-first, ISO9660 fallback) and writes directly through
    // DiscUtils' raw NTFS partition access (no staging needed — NTFS has no per-file
    // size limit, unlike LegacySplitWriter's FAT32 case).
    private async Task CopyViaDiscUtilsAsync(
        string isoPath, PartitionHandle dataPartition, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        using var isoStream = File.OpenRead(isoPath);
        using var isoFileSystem = IsoFileSystemOpener.Open(isoStream);

        try
        {
            using var ntfs = _partitioner.OpenNtfsFileSystem(dataPartition);
            var totalBytes = ComputeTotalBytes(isoFileSystem);
            var tracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 88, "Copying files", totalBytes);
            CopyIsoContentsToFileSystem(isoFileSystem, ntfs, tracker, ct);
        }
        finally
        {
            _partitioner.ReleaseOpenDisks();
        }
    }

    internal static void CopyIsoContentsToFileSystem(
        IFileSystem source,
        IFileSystem destination,
        CopyProgressTracker? tracker = null,
        CancellationToken ct = default)
    {
        CopyDirectory(source, destination, "", tracker, ct);
    }

    private static void CopyDirectory(
        IFileSystem source,
        IFileSystem destination,
        string path,
        CopyProgressTracker? tracker,
        CancellationToken ct)
    {
        foreach (var dir in source.GetDirectories(path))
        {
            ct.ThrowIfCancellationRequested();
            destination.CreateDirectory(dir);
            CopyDirectory(source, destination, dir, tracker, ct);
        }

        foreach (var file in source.GetFiles(path))
        {
            ct.ThrowIfCancellationRequested();

            // ISO9660 (non-Joliet-resolved) names carry a ";<version>" suffix
            // (e.g. "install.wim;1") that must be stripped for the destination
            // file system, which has no concept of file versions.
            var destPath = StripIsoVersionSuffix(file);

            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = destination.OpenFile(destPath, FileMode.Create, FileAccess.Write);
            CopyProgressTracker.CopyStreamWithProgress(sourceStream, destStream, tracker, ct);
        }
    }

    private static long ComputeTotalBytes(IFileSystem source, string path = "")
    {
        long total = 0;
        foreach (var dir in source.GetDirectories(path))
        {
            total += ComputeTotalBytes(source, dir);
        }

        foreach (var file in source.GetFiles(path))
        {
            total += source.GetFileInfo(file).Length;
        }

        return total;
    }

    private static string StripIsoVersionSuffix(string isoPath)
    {
        var semicolonIndex = isoPath.LastIndexOf(';');
        return semicolonIndex >= 0 ? isoPath[..semicolonIndex] : isoPath;
    }
}
