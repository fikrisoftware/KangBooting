using DiscUtils;

namespace KangBooting.Core;

public class UefiNtfsWriter : IWriteEngine
{
    private readonly IPartitioner _partitioner;
    private readonly IIsoMounter _isoMounter;

    public UefiNtfsWriter(IPartitioner partitioner, IIsoMounter isoMounter)
    {
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
        var (bootDriveLetter, dataDriveLetter) = await _partitioner.CreateUefiNtfsLayoutAsync(target, ct);

        progress.Report(new WriteProgress(5, 0, null, "Menulis bootloader"));
        WriteBootloader(bootDriveLetter);

        progress.Report(new WriteProgress(10, 0, null, "Copying files"));
        await CopyIsoContentsAsync(isoPath, dataDriveLetter, progress, ct);

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    // Places the EFI bootloader at the fixed path UEFI firmware probes by default when
    // no other boot entry is configured: EFI\Boot\bootx64.efi. Plain File.Copy onto the
    // boot partition's real (already-formatted, native-cmdlet-assigned) drive letter —
    // no DiscUtils FAT writer involved.
    private static void WriteBootloader(string bootDriveLetter)
    {
        var bootloaderSourcePath = Path.Combine(AppContext.BaseDirectory, "assets", "bootx64_signed.efi");
        var destDir = Path.Combine(bootDriveLetter + @"\", "EFI", "Boot");
        Directory.CreateDirectory(destDir);
        File.Copy(bootloaderSourcePath, Path.Combine(destDir, "bootx64.efi"), overwrite: true);
    }

    // Prefer mounting the ISO via Windows' own UDF/ISO9660 driver (IIsoMounter) and
    // writing through the NTFS partition's real Windows-assigned drive letter — see
    // Partitioner's class comment for the specific DiscUtils bugs (a near-empty UDF disc
    // read via ISO9660 alone; "Invalid path" on valid multi-dot Windows filenames;
    // "Corrupt record" formatting against stale on-disk state) this sidesteps by using
    // the OS's own drivers throughout. Falls back to DiscUtils only for READING the ISO
    // if native mounting fails — writing always goes through the real drive letter via
    // plain System.IO, never DiscUtils' NTFS writer.
    private async Task CopyIsoContentsAsync(
        string isoPath, string dataDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        var mount = await TryGetOrMountAsync(isoPath, ct);
        if (mount is null)
        {
            CopyViaDiscUtilsFallback(isoPath, dataDriveLetter, progress, ct);
            return;
        }

        var (mountedDriveLetter, weMountedIt) = mount.Value;
        try
        {
            var sourceRoot = mountedDriveLetter + @"\";
            var totalBytes = RealFileSystemCopier.ComputeTotalBytes(sourceRoot);
            var tracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 88, "Copying files", totalBytes);
            RealFileSystemCopier.CopyDirectory(sourceRoot, dataDriveLetter, tracker, ct);
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
    // DiscUtils-based reading.
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

    // Fallback used only if native ISO mounting fails: reads via DiscUtils (UDF-first,
    // ISO9660 fallback) but still writes via plain System.IO to the real destination
    // drive letter — DiscUtils is never used to write.
    private static void CopyViaDiscUtilsFallback(
        string isoPath, string dataDriveLetter, IProgress<WriteProgress> progress, CancellationToken ct)
    {
        using var isoStream = File.OpenRead(isoPath);
        using var isoFileSystem = IsoFileSystemOpener.Open(isoStream);

        var totalBytes = ComputeTotalBytes(isoFileSystem);
        var tracker = new CopyProgressTracker(progress, rangeStart: 10, rangeSpan: 88, "Copying files", totalBytes);
        CopyIsoContentsToRealDrive(isoFileSystem, dataDriveLetter, tracker, ct);
    }

    internal static void CopyIsoContentsToRealDrive(
        IFileSystem source, string driveLetter, CopyProgressTracker? tracker = null, CancellationToken ct = default)
    {
        var destRoot = driveLetter.EndsWith('\\') ? driveLetter : driveLetter + @"\";
        CopyDirectory(source, destRoot, "", tracker, ct);
    }

    private static void CopyDirectory(
        IFileSystem source,
        string destRoot,
        string path,
        CopyProgressTracker? tracker,
        CancellationToken ct)
    {
        foreach (var dir in source.GetDirectories(path))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destRoot, TrimLeadingSeparator(dir)));
            CopyDirectory(source, destRoot, dir, tracker, ct);
        }

        foreach (var file in source.GetFiles(path))
        {
            ct.ThrowIfCancellationRequested();

            // ISO9660 (non-Joliet-resolved) names carry a ";<version>" suffix
            // (e.g. "install.wim;1") that must be stripped for the destination
            // file system, which has no concept of file versions.
            var destPath = Path.Combine(destRoot, TrimLeadingSeparator(StripIsoVersionSuffix(file)));

            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = File.Create(destPath);
            CopyProgressTracker.CopyStreamWithProgress(sourceStream, destStream, tracker, ct);
        }
    }

    private static string TrimLeadingSeparator(string path) => path.TrimStart('\\', '/');

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
