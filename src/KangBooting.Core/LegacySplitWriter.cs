using DiscUtils;
using DiscUtils.Iso9660;

namespace KangBooting.Core;

public class LegacySplitWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;
    private readonly IDismRunner _dismRunner;
    private readonly IBootsectRunner _bootsectRunner;

    private const int FourGigabytes = 4000; // MB, matches spec's split threshold
    // IOCTL_DISK_UPDATE_PROPERTIES (Partitioner.RefreshPartitionTable) fires while the
    // old volume's lock handle is still open (inside LockVolume's using-block in
    // WriteAsync below) — observed on real hardware to mean Windows' mount manager
    // doesn't finish reassigning a drive letter until well after that handle closes,
    // often several seconds later. 5 attempts * 300ms (1.5s total) was not enough in
    // practice; widened to give the mount manager realistic time to catch up.
    private const int DriveLetterRetryAttempts = 30;
    private static readonly TimeSpan DriveLetterRetryDelay = TimeSpan.FromMilliseconds(500);

    public LegacySplitWriter(
        IDriveService driveService, IPartitioner partitioner, IDismRunner dismRunner, IBootsectRunner bootsectRunner)
    {
        _driveService = driveService;
        _partitioner = partitioner;
        _dismRunner = dismRunner;
        _bootsectRunner = bootsectRunner;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        // I6: fail fast, before wiping the drive, if there's clearly not enough room
        // for the extracted ISO on the system temp drive.
        var isoSizeBytes = new FileInfo(isoPath).Length;
        var tempFreeBytes = new System.IO.DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace;
        if (tempFreeBytes < isoSizeBytes)
        {
            throw new InvalidOperationException(
                "Ruang kosong di drive sistem (temp) tidak cukup untuk mengekstrak ISO ini.");
        }

        var stagingDir = Directory.CreateTempSubdirectory("kangbooting-staging").FullName;
        try
        {
            using (var isoStream = File.OpenRead(isoPath))
            using (var cdReader = new CDReader(isoStream, joliet: true))
            {
                progress.Report(new WriteProgress(10, 0, null, "Extracting ISO"));
                var extractTotalBytes = ComputeTotalBytes(cdReader);
                var extractTracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 30, "Extracting ISO", extractTotalBytes);
                ExtractIsoToDirectory(cdReader, stagingDir, tracker: extractTracker, ct: ct);
            }

            await SplitInstallImageIfNeededAsync(stagingDir, progress, ct);

            PartitionHandle fat32Partition;
            using (var volumeLock = _driveService.LockVolume(target.DeviceId))
            {
                fat32Partition = await _partitioner.CreateLegacyFat32LayoutAsync(target, ct);

                progress.Report(new WriteProgress(80, 0, null, "Copying files"));
                using var fat32 = _partitioner.OpenFat32FileSystem(fat32Partition);
                var copyTotalBytes = ComputeStagingDirTotalBytes(stagingDir);
                var copyTracker = new CopyProgressTracker(progress, rangeStart: 80, rangeSpan: 13, "Copying files", copyTotalBytes);
                CopyDirectoryToFileSystem(stagingDir, fat32, "", copyTracker, ct);
            }

            // Release the Disk handle opened by OpenFat32FileSystem now, before waiting
            // for Windows to assign a drive letter below — holding it open blocks the
            // OS's mount manager from fully re-enumerating the disk (same root cause as
            // the leaked-handle-blocks-retry bug this fixes; here it also plausibly
            // delays/prevents drive-letter assignment on the same attempt).
            _partitioner.ReleaseOpenDisks();

            // C2: write BIOS-bootable MBR/VBR boot code onto the FAT32 partition now that
            // it's formatted and populated. Requires a drive letter, which PartitionHandle
            // (a raw DeviceId+PartitionIndex reference) doesn't carry — resolved via WMI,
            // with a short retry loop since Windows may take a moment after
            // IOCTL_DISK_UPDATE_PROPERTIES (see Partitioner.RefreshPartitionTable) to
            // finish assigning one. Unverified on real hardware.
            progress.Report(new WriteProgress(95, 0, null, "Menulis boot code"));
            var driveLetter = await ResolveDriveLetterWithRetryAsync(target.DeviceId, fat32Partition.PartitionIndex, ct);
            if (driveLetter is null)
            {
                throw new IOException(
                    "Partisi FAT32 berhasil dibuat tetapi Windows belum memberi drive letter untuk menulis boot code. " +
                    "Drive kemungkinan tidak akan bisa boot dari BIOS/Legacy - coba lepas dan pasang ulang drive USB.");
            }

            await _bootsectRunner.WriteBootCodeAsync(driveLetter, ct);
        }
        finally
        {
            // Must run even on failure/cancellation: an open Disk handle left over from
            // a failed attempt blocks a subsequent Retry (same process) from reopening
            // the same physical disk — confirmed on real hardware.
            _partitioner.ReleaseOpenDisks();
            Directory.Delete(stagingDir, recursive: true);
        }

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    private async Task<string?> ResolveDriveLetterWithRetryAsync(string deviceId, int partitionIndex, CancellationToken ct)
    {
        for (int attempt = 0; attempt < DriveLetterRetryAttempts; attempt++)
        {
            var driveLetter = _driveService.GetDriveLetterForPartition(deviceId, partitionIndex);
            if (driveLetter is not null)
            {
                return driveLetter;
            }

            await Task.Delay(DriveLetterRetryDelay, ct);
        }

        return null;
    }

    // I3: install.esd (also a valid Windows install image, per IsoInspector) hit the
    // same >4GB split requirement as install.wim but was never checked, so an oversized
    // .esd was copied to FAT32 verbatim and would fail with an unclear error mid-copy.
    // DISM's /Split-Image operates on WIM-family container images regardless of the
    // .wim/.esd extension (both are documented as supported source formats), so
    // DismRunner.SplitWimAsync is reused as-is for .esd too.
    private async Task SplitInstallImageIfNeededAsync(
        string stagingDir, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var wimPath = Path.Combine(stagingDir, "sources", "install.wim");
        var esdPath = Path.Combine(stagingDir, "sources", "install.esd");

        var imagePath = File.Exists(wimPath) ? wimPath : File.Exists(esdPath) ? esdPath : null;
        if (imagePath is null)
        {
            return;
        }

        if (new FileInfo(imagePath).Length <= FourGigabytes * 1024L * 1024)
        {
            return;
        }

        progress.Report(new WriteProgress(50, 0, null, $"Splitting {Path.GetFileName(imagePath)}"));
        var swmPath = Path.Combine(stagingDir, "sources", "install.swm");
        await _dismRunner.SplitWimAsync(imagePath, swmPath, FourGigabytes, ct);
    }

    internal static void ExtractIsoToDirectory(
        CDReader source, string destinationDir, string path = "",
        CopyProgressTracker? tracker = null, CancellationToken ct = default)
    {
        // ponytail: GetDirectories(path)/GetFiles(path) without SearchOption default to
        // TopDirectoryOnly (same convention as System.IO), so this recurses manually —
        // the brief's single-level version would silently skip sources\install.wim.
        foreach (var dir in source.GetDirectories(path))
        {
            ct.ThrowIfCancellationRequested();

            // ponytail: DiscUtils returns paths like "\sources" — Path.Combine treats a
            // leading separator as drive-rooted, so trim it before combining (same fix
            // as UefiNtfsWriter.StripIsoVersionSuffix handles for the ";1" file suffix).
            Directory.CreateDirectory(Path.Combine(destinationDir, TrimLeadingSeparator(dir)));
            ExtractIsoToDirectory(source, destinationDir, dir, tracker, ct);
        }

        foreach (var file in source.GetFiles(path))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = TrimLeadingSeparator(StripIsoVersionSuffix(file));
            var destPath = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = File.Create(destPath);
            CopyProgressTracker.CopyStreamWithProgress(sourceStream, destStream, tracker, ct);
        }
    }

    private static string TrimLeadingSeparator(string path) => path.TrimStart('\\', '/');

    private static string StripIsoVersionSuffix(string isoPath)
    {
        var semicolonIndex = isoPath.LastIndexOf(';');
        return semicolonIndex >= 0 ? isoPath[..semicolonIndex] : isoPath;
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

    private static long ComputeStagingDirTotalBytes(string stagingDir)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static void CopyDirectoryToFileSystem(
        string sourceDir, IFileSystem destination, string relativePath,
        CopyProgressTracker? tracker = null, CancellationToken ct = default)
    {
        var fullSourceDir = Path.Combine(sourceDir, relativePath);

        foreach (var dir in Directory.GetDirectories(fullSourceDir))
        {
            ct.ThrowIfCancellationRequested();

            var relDir = Path.GetRelativePath(sourceDir, dir);
            destination.CreateDirectory(relDir);
            CopyDirectoryToFileSystem(sourceDir, destination, relDir, tracker, ct);
        }

        foreach (var file in Directory.GetFiles(fullSourceDir))
        {
            ct.ThrowIfCancellationRequested();

            var relFile = Path.GetRelativePath(sourceDir, file);
            using var sourceStream = File.OpenRead(file);
            using var destStream = destination.OpenFile(relFile, FileMode.Create, FileAccess.Write);
            CopyProgressTracker.CopyStreamWithProgress(sourceStream, destStream, tracker, ct);
        }
    }
}
