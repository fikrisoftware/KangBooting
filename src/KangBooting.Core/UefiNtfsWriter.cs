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
                CopyIsoContentsToFileSystem(cdReader, ntfs, progress);
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
        IProgress<WriteProgress>? progress = null)
    {
        CopyDirectory(source, destination, "", progress);
    }

    private static void CopyDirectory(
        CDReader source,
        IFileSystem destination,
        string path,
        IProgress<WriteProgress>? progress)
    {
        foreach (var dir in source.GetDirectories(path))
        {
            destination.CreateDirectory(dir);
            CopyDirectory(source, destination, dir, progress);
        }

        foreach (var file in source.GetFiles(path))
        {
            // ISO9660 (non-Joliet-resolved) names carry a ";<version>" suffix
            // (e.g. "install.wim;1") that must be stripped for the destination
            // file system, which has no concept of file versions.
            var destPath = StripIsoVersionSuffix(file);

            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = destination.OpenFile(destPath, FileMode.Create, FileAccess.Write);
            sourceStream.CopyTo(destStream);
        }
    }

    private static string StripIsoVersionSuffix(string isoPath)
    {
        var semicolonIndex = isoPath.LastIndexOf(';');
        return semicolonIndex >= 0 ? isoPath[..semicolonIndex] : isoPath;
    }
}
