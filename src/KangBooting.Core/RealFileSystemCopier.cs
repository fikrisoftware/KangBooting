namespace KangBooting.Core;

// Shared helper for copying between two real Windows-accessible directories/drive
// letters via plain System.IO — used by both IWriteEngine implementations once a source
// (a natively-mounted ISO, or an extracted staging dir) and destination (a formatted USB
// drive letter) are both real filesystem paths. This deliberately avoids any DiscUtils
// reader/writer for the actual bulk file copy — see IsoFileSystemOpener's and
// LegacySplitWriter's comments for the specific real bugs (a near-empty UDF disc read
// via ISO9660 alone; "Invalid path" on valid multi-dot Windows filenames) this sidesteps
// by using the OS's own filesystem drivers on both ends instead.
internal static class RealFileSystemCopier
{
    public static long ComputeTotalBytes(string sourceDir, string? excludeRelativePath = null)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (excludeRelativePath is not null && Path.GetRelativePath(sourceDir, file) == excludeRelativePath)
            {
                continue;
            }

            total += new FileInfo(file).Length;
        }

        return total;
    }

    // excludeRelativePath skips one file (used to omit an oversized install.wim/.esd
    // that's being split separately instead of copied whole).
    public static void CopyDirectory(
        string sourceDir, string destDriveOrDir, CopyProgressTracker? tracker = null,
        CancellationToken ct = default, string? excludeRelativePath = null)
    {
        var destRoot = destDriveOrDir.EndsWith('\\') ? destDriveOrDir : destDriveOrDir + @"\";

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relDir = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destRoot, relDir));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relFile = Path.GetRelativePath(sourceDir, file);
            if (excludeRelativePath is not null && relFile == excludeRelativePath)
            {
                continue;
            }

            var destPath = Path.Combine(destRoot, relFile);
            using var sourceStream = File.OpenRead(file);
            using var destStream = File.Create(destPath);
            CopyProgressTracker.CopyStreamWithProgress(sourceStream, destStream, tracker, ct);
        }
    }
}
