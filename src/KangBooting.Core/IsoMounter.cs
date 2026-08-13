using System.Diagnostics;

namespace KangBooting.Core;

public interface IIsoMounter
{
    // Returns the drive letter (e.g. "E:") if the ISO is already mounted, or null if
    // not — checked first so callers never double-mount the same ISO.
    Task<string?> GetExistingMountedDriveLetterAsync(string isoPath, CancellationToken ct = default);

    // Mounts the ISO via Windows' native disk-image mounting and returns its assigned
    // drive letter (e.g. "E:"). Throws a human-readable exception on failure.
    Task<string> MountAsync(string isoPath, CancellationToken ct = default);

    Task DismountAsync(string isoPath, CancellationToken ct = default);
}

// Mounts ISOs via Windows' native Mount-DiskImage (shelling out to powershell.exe, same
// pattern as DismRunner/BootsectRunner) instead of reading them through a third-party
// library. Preferred over DiscUtils-based reading (IsoFileSystemOpener) because it uses
// the OS's own, fully-correct UDF/ISO9660 driver — sidesteps the whole class of bug
// discovered in DiscUtils' readers/writers (see IsoFileSystemOpener's and
// LegacySplitWriter's comments for the specific bugs found). Callers should fall back to
// IsoFileSystemOpener + extract-to-staging only if mounting fails.
public class IsoMounter : IIsoMounter
{
    public async Task<string?> GetExistingMountedDriveLetterAsync(string isoPath, CancellationToken ct = default)
    {
        var output = await RunPowerShellAsync(BuildGetExistingMountScript(isoPath), ct);
        var driveLetter = output.Trim();
        return string.IsNullOrEmpty(driveLetter) ? null : driveLetter + ":";
    }

    public async Task<string> MountAsync(string isoPath, CancellationToken ct = default)
    {
        var output = await RunPowerShellAsync(BuildMountScript(isoPath), ct);
        var driveLetter = output.Trim();
        if (string.IsNullOrEmpty(driveLetter))
        {
            throw new IOException($"Gagal mount ISO '{isoPath}' — Windows tidak memberi drive letter.");
        }

        return driveLetter + ":";
    }

    public async Task DismountAsync(string isoPath, CancellationToken ct = default)
    {
        await RunPowerShellAsync(BuildDismountScript(isoPath), ct, throwOnFailure: false);
    }

    internal static string EscapeForPowerShell(string value) => value.Replace("'", "''");

    internal static string BuildGetExistingMountScript(string isoPath) =>
        $"$img = Get-DiskImage -ImagePath '{EscapeForPowerShell(isoPath)}' -ErrorAction SilentlyContinue; " +
        "if ($img -and $img.Attached) { ($img | Get-Volume).DriveLetter } else { '' }";

    internal static string BuildMountScript(string isoPath) =>
        $"$v = Mount-DiskImage -ImagePath '{EscapeForPowerShell(isoPath)}' -PassThru | Get-Volume; " +
        "$v.DriveLetter";

    internal static string BuildDismountScript(string isoPath) =>
        $"Dismount-DiskImage -ImagePath '{EscapeForPowerShell(isoPath)}'";

    private async Task<string> RunPowerShellAsync(string script, CancellationToken ct, bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Gagal menjalankan powershell.exe untuk mount/dismount ISO.");

        // Drain both streams concurrently — see DismRunner/BootsectRunner for why
        // sequential reads risk a pipe-buffer deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        await process.WaitForExitAsync(ct);

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new IOException(
                $"Operasi mount/dismount ISO gagal (exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? "Tidak ada detail error." : stderr.Trim()));
        }

        return stdout;
    }
}
