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
    private const int DriveLetterRetryAttempts = 5;
    private static readonly TimeSpan DriveLetterRetryDelay = TimeSpan.FromMilliseconds(300);

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
                ExtractIsoToDirectory(cdReader, stagingDir);
            }

            await SplitInstallImageIfNeededAsync(stagingDir, progress, ct);

            PartitionHandle fat32Partition;
            using (var volumeLock = _driveService.LockVolume(target.DeviceId))
            {
                fat32Partition = await _partitioner.CreateLegacyFat32LayoutAsync(target, ct);

                progress.Report(new WriteProgress(80, 0, null, "Copying files"));
                using var fat32 = _partitioner.OpenFat32FileSystem(fat32Partition);
                CopyDirectoryToFileSystem(stagingDir, fat32, "");
            }

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

    internal static void ExtractIsoToDirectory(CDReader source, string destinationDir, string path = "")
    {
        // ponytail: GetDirectories(path)/GetFiles(path) without SearchOption default to
        // TopDirectoryOnly (same convention as System.IO), so this recurses manually —
        // the brief's single-level version would silently skip sources\install.wim.
        foreach (var dir in source.GetDirectories(path))
        {
            // ponytail: DiscUtils returns paths like "\sources" — Path.Combine treats a
            // leading separator as drive-rooted, so trim it before combining (same fix
            // as UefiNtfsWriter.StripIsoVersionSuffix handles for the ";1" file suffix).
            Directory.CreateDirectory(Path.Combine(destinationDir, TrimLeadingSeparator(dir)));
            ExtractIsoToDirectory(source, destinationDir, dir);
        }

        foreach (var file in source.GetFiles(path))
        {
            var relativePath = TrimLeadingSeparator(StripIsoVersionSuffix(file));
            var destPath = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = File.Create(destPath);
            sourceStream.CopyTo(destStream);
        }
    }

    private static string TrimLeadingSeparator(string path) => path.TrimStart('\\', '/');

    private static string StripIsoVersionSuffix(string isoPath)
    {
        var semicolonIndex = isoPath.LastIndexOf(';');
        return semicolonIndex >= 0 ? isoPath[..semicolonIndex] : isoPath;
    }

    private static void CopyDirectoryToFileSystem(string sourceDir, IFileSystem destination, string relativePath)
    {
        var fullSourceDir = Path.Combine(sourceDir, relativePath);

        foreach (var dir in Directory.GetDirectories(fullSourceDir))
        {
            var relDir = Path.GetRelativePath(sourceDir, dir);
            destination.CreateDirectory(relDir);
            CopyDirectoryToFileSystem(sourceDir, destination, relDir);
        }

        foreach (var file in Directory.GetFiles(fullSourceDir))
        {
            var relFile = Path.GetRelativePath(sourceDir, file);
            using var sourceStream = File.OpenRead(file);
            using var destStream = destination.OpenFile(relFile, FileMode.Create, FileAccess.Write);
            sourceStream.CopyTo(destStream);
        }
    }
}
