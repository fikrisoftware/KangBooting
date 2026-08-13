using DiscUtils;
using DiscUtils.Iso9660;

namespace KangBooting.Core;

public class UefiNtfsWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;

    public UefiNtfsWriter(IDriveService driveService, IPartitioner partitioner)
    {
        _driveService = driveService;
        _partitioner = partitioner;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        using var isoStream = File.OpenRead(isoPath);
        using var cdReader = new CDReader(isoStream, joliet: true);

        using (var volumeLock = _driveService.LockVolume(target.DeviceId))
        {
            try
            {
                var (bootPartition, dataPartition) = await _partitioner
                    .CreateUefiNtfsLayoutAsync(target, ct);

                var bootloaderImagePath = Path.Combine(AppContext.BaseDirectory, "assets", "bootx64_signed.efi");
                await _partitioner.WriteBootloaderImageAsync(
                    bootPartition, bootloaderImagePath, ct);

                progress.Report(new WriteProgress(10, 0, null, "Copying files"));

                using var ntfs = _partitioner.OpenNtfsFileSystem(dataPartition);
                var totalBytes = ComputeTotalBytes(cdReader);
                var tracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 88, "Copying files", totalBytes);
                CopyIsoContentsToFileSystem(cdReader, ntfs, tracker, ct);
            }
            finally
            {
                // See LegacySplitWriter's equivalent call: an open Disk handle left over
                // from a failed/completed attempt blocks a subsequent Retry (same
                // process) from reopening the same physical disk — confirmed on real
                // hardware in the Legacy mode path; fixed here too for consistency.
                _partitioner.ReleaseOpenDisks();
            }
        }

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    internal static void CopyIsoContentsToFileSystem(
        CDReader source,
        IFileSystem destination,
        CopyProgressTracker? tracker = null,
        CancellationToken ct = default)
    {
        CopyDirectory(source, destination, "", tracker, ct);
    }

    private static void CopyDirectory(
        CDReader source,
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

    private static long ComputeTotalBytes(CDReader source, string path = "")
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
