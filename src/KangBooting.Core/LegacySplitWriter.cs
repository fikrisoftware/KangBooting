using DiscUtils;
using DiscUtils.Iso9660;

namespace KangBooting.Core;

public class LegacySplitWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;
    private readonly IDismRunner _dismRunner;

    private const int FourGigabytes = 4000; // MB, matches spec's split threshold

    public LegacySplitWriter(IDriveService driveService, IPartitioner partitioner, IDismRunner dismRunner)
    {
        _driveService = driveService;
        _partitioner = partitioner;
        _dismRunner = dismRunner;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        var stagingDir = Directory.CreateTempSubdirectory("kangbooting-staging").FullName;
        try
        {
            using (var isoStream = File.OpenRead(isoPath))
            using (var cdReader = new CDReader(isoStream, joliet: true))
            {
                progress.Report(new WriteProgress(10, 0, null, "Extracting ISO"));
                ExtractIsoToDirectory(cdReader, stagingDir);
            }

            var wimPath = Path.Combine(stagingDir, "sources", "install.wim");
            if (File.Exists(wimPath) && new FileInfo(wimPath).Length > FourGigabytes * 1024L * 1024)
            {
                progress.Report(new WriteProgress(50, 0, null, "Splitting install.wim"));
                var swmPath = Path.Combine(stagingDir, "sources", "install.swm");
                await _dismRunner.SplitWimAsync(wimPath, swmPath, FourGigabytes, ct);
            }

            using (var volumeLock = _driveService.LockVolume(target.DeviceId))
            {
                var fat32Partition = await _partitioner.CreateLegacyFat32LayoutAsync(target, ct);

                progress.Report(new WriteProgress(80, 0, null, "Copying files"));
                using var fat32 = _partitioner.OpenFat32FileSystem(fat32Partition);
                CopyDirectoryToFileSystem(stagingDir, fat32, "");
            }
        }
        finally
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    private static void ExtractIsoToDirectory(CDReader source, string destinationDir, string path = "")
    {
        // ponytail: GetDirectories(path)/GetFiles(path) without SearchOption default to
        // TopDirectoryOnly (same convention as System.IO), so this recurses manually —
        // the brief's single-level version would silently skip sources\install.wim.
        foreach (var dir in source.GetDirectories(path))
        {
            Directory.CreateDirectory(Path.Combine(destinationDir, dir));
            ExtractIsoToDirectory(source, destinationDir, dir);
        }

        foreach (var file in source.GetFiles(path))
        {
            var destPath = Path.Combine(destinationDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = File.Create(destPath);
            sourceStream.CopyTo(destStream);
        }
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
