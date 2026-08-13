using DiscUtils;

namespace KangBooting.Core;

public class LegacySplitWriter : IWriteEngine
{
    private readonly IPartitioner _partitioner;
    private readonly IDismRunner _dismRunner;
    private readonly IBootsectRunner _bootsectRunner;
    private readonly IIsoMounter _isoMounter;

    private const int FourGigabytes = 4000; // MB, matches spec's split threshold

    public LegacySplitWriter(
        IPartitioner partitioner, IDismRunner dismRunner,
        IBootsectRunner bootsectRunner, IIsoMounter isoMounter)
    {
        _partitioner = partitioner;
        _dismRunner = dismRunner;
        _bootsectRunner = bootsectRunner;
        _isoMounter = isoMounter;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        // Partitioner.CreateLegacyFat32LayoutAsync (native New-Partition/Format-Volume,
        // not DiscUtils) returns an already-formatted, drive-lettered partition — no
        // separate volume-lock or drive-letter-polling step needed; the native cmdlets
        // handle both internally as part of their own job.
        var usbDriveLetter = await _partitioner.CreateLegacyFat32LayoutAsync(target, ct);

        await CopyIsoContentsAsync(isoPath, usbDriveLetter, progress, ct);

        progress.Report(new WriteProgress(95, 0, null, "Menulis boot code"));
        await _bootsectRunner.WriteBootCodeAsync(usbDriveLetter, ct);

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    // Prefer mounting the ISO via Windows' own UDF/ISO9660 driver (IIsoMounter) over
    // reading it through DiscUtils (IsoFileSystemOpener): native mounting sidesteps two
    // real bugs found in DiscUtils when this was built against a real Windows 11 ISO —
    // its ISO9660/Joliet reader saw an almost-empty disc (real content lived in the UDF
    // layer it doesn't read), and its FAT writer rejected valid multi-dot filenames
    // (e.g. "bootsect.exe.mui") that Windows itself writes/reads without issue. Falls
    // back to the DiscUtils-based extract-to-staging path only if native mounting itself
    // fails (e.g. Mount-DiskImage unavailable/blocked in some environment).
    private async Task CopyIsoContentsAsync(
        string isoPath, string usbDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var mount = await TryGetOrMountAsync(isoPath, ct);
        if (mount is null)
        {
            await CopyViaStagingExtractionAsync(isoPath, usbDriveLetter, progress, ct);
            return;
        }

        var (mountedDriveLetter, weMountedIt) = mount.Value;
        try
        {
            await CopyFromMountedIsoAsync(mountedDriveLetter, usbDriveLetter, progress, ct);
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

    // Checks for an already-mounted instance first (never double-mount the same ISO —
    // if the user or another tool already has it mounted, reuse that mount and don't
    // dismount it when done). Returns null if mounting isn't possible at all, signalling
    // the caller to fall back to DiscUtils-based reading.
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

    private async Task CopyFromMountedIsoAsync(
        string mountedDriveLetter, string usbDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var sourceRoot = mountedDriveLetter + @"\";

        var wimPath = Path.Combine(sourceRoot, "sources", "install.wim");
        var esdPath = Path.Combine(sourceRoot, "sources", "install.esd");
        var imagePath = File.Exists(wimPath) ? wimPath : File.Exists(esdPath) ? esdPath : null;

        string? excludeRelativePath = imagePath is not null && new FileInfo(imagePath).Length > FourGigabytes * 1024L * 1024
            ? Path.GetRelativePath(sourceRoot, imagePath)
            : null;

        progress.Report(new WriteProgress(20, 0, null, "Copying files"));
        var totalBytes = RealFileSystemCopier.ComputeTotalBytes(sourceRoot, excludeRelativePath);
        var copyRangeSpan = excludeRelativePath is null ? 60 : 50;
        var tracker = new CopyProgressTracker(progress, rangeStart: 20, rangeSpan: copyRangeSpan, "Copying files", totalBytes);
        RealFileSystemCopier.CopyDirectory(sourceRoot, usbDriveLetter, tracker, ct, excludeRelativePath);

        if (excludeRelativePath is not null)
        {
            await SplitAndCopyInstallImageAsync(imagePath!, usbDriveLetter, progress, ct);
        }
    }

    // Splits the oversized install.wim/.esd into a small temp directory (only the split
    // chunks, not a copy of the whole ISO) and copies just those chunks onto the USB —
    // much less temp disk usage than the old extract-everything-first approach.
    private async Task SplitAndCopyInstallImageAsync(
        string imagePath, string usbDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var imageSizeBytes = new FileInfo(imagePath).Length;
        var tempFreeBytes = new System.IO.DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace;
        if (tempFreeBytes < imageSizeBytes)
        {
            throw new InvalidOperationException(
                $"Ruang kosong di drive sistem (temp) tidak cukup untuk memecah {Path.GetFileName(imagePath)}.");
        }

        progress.Report(new WriteProgress(70, 0, null, $"Splitting {Path.GetFileName(imagePath)}"));
        var tempSplitDir = Directory.CreateTempSubdirectory("kangbooting-split").FullName;
        try
        {
            var swmPath = Path.Combine(tempSplitDir, "install.swm");
            await _dismRunner.SplitWimAsync(imagePath, swmPath, FourGigabytes, ct);

            var destSourcesDir = Path.Combine(usbDriveLetter + @"\", "sources");
            Directory.CreateDirectory(destSourcesDir);
            foreach (var swmFile in Directory.GetFiles(tempSplitDir, "install*.swm"))
            {
                File.Copy(swmFile, Path.Combine(destSourcesDir, Path.GetFileName(swmFile)), overwrite: true);
            }
        }
        finally
        {
            Directory.Delete(tempSplitDir, recursive: true);
        }
    }

    // Fallback used only if native ISO mounting itself fails. Matches the original
    // extract-to-staging approach: read via DiscUtils (UDF-first, ISO9660 fallback),
    // extract everything to a temp directory, split if needed, then copy to the USB.
    private async Task CopyViaStagingExtractionAsync(
        string isoPath, string usbDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
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
            using (var isoFileSystem = IsoFileSystemOpener.Open(isoStream))
            {
                progress.Report(new WriteProgress(15, 0, null, "Extracting ISO"));
                var extractTotalBytes = ComputeTotalBytes(isoFileSystem);
                var extractTracker = new CopyProgressTracker(progress, rangeStart: 15, rangeSpan: 35, "Extracting ISO", extractTotalBytes);
                ExtractIsoToDirectory(isoFileSystem, stagingDir, tracker: extractTracker, ct: ct);
            }

            await SplitStagingImageIfNeededAsync(stagingDir, progress, ct);

            progress.Report(new WriteProgress(85, 0, null, "Copying files"));
            var copyTotalBytes = RealFileSystemCopier.ComputeTotalBytes(stagingDir);
            var copyTracker = new CopyProgressTracker(progress, rangeStart: 85, rangeSpan: 8, "Copying files", copyTotalBytes);
            RealFileSystemCopier.CopyDirectory(stagingDir, usbDriveLetter, copyTracker, ct);
        }
        finally
        {
            Directory.Delete(stagingDir, recursive: true);
        }
    }

    // I3: install.esd (also a valid Windows install image, per IsoInspector) hit the
    // same >4GB split requirement as install.wim but was never checked, so an oversized
    // .esd was copied to FAT32 verbatim and would fail with an unclear error mid-copy.
    // DISM's /Split-Image operates on WIM-family container images regardless of the
    // .wim/.esd extension (both are documented as supported source formats), so
    // DismRunner.SplitWimAsync is reused as-is for .esd too.
    private async Task SplitStagingImageIfNeededAsync(
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
        File.Delete(imagePath);
    }

    internal static void ExtractIsoToDirectory(
        IFileSystem source, string destinationDir, string path = "",
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

}
