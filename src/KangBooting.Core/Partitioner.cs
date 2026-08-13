using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KangBooting.Core;

// Root-cause pivot after repeated real-hardware failures: DiscUtils' own partition-table
// writer and FAT/NTFS formatters were the source of three separate real-hardware bugs —
// FatFileSystem.FormatPartition rejecting a 1MiB boot partition as "too small",
// NtfsFileSystem.Format throwing "Corrupt record" against stale on-disk state left by a
// previous DiscUtils-written layout, and needing to reverse-engineer the correct MBR
// partition-type byte from Rufus's source in the first place. Windows' own Storage
// module cmdlets (New-Partition/Format-Volume) are what Windows Setup and diskpart
// themselves use to partition/format disks, and — shelled out to via powershell.exe,
// the same reliable pattern already used by IsoMounter/DismRunner/BootsectRunner — have
// shown none of these issues. Partitioning, formatting, and drive-letter assignment are
// all done natively now; DiscUtils remains only for the ISO-reading fallback
// (IsoFileSystemOpener), a lower-risk read-only concern that hasn't had a real-hardware
// bug. This also eliminates the previous FSCTL_LOCK_VOLUME/drive-letter-polling dance
// (DriveService.LockVolume, DriveLetterResolver) entirely: New-Partition/Format-Volume
// are synchronous and handle volume locking and drive-letter assignment internally as
// part of their own job, so callers get a ready-to-use drive letter the moment the
// PowerShell call returns.
public class Partitioner : IPartitioner
{
    private const int BootPartitionMB = 16;

    public async Task<(string bootDriveLetter, string dataDriveLetter)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var diskNumber = ExtractDiskNumber(target.DeviceId);
        var script = BuildUefiNtfsLayoutScript(diskNumber, BootPartitionMB);
        var output = await RunPowerShellAsync(script, ct);
        var letters = ParseDriveLetters(output, expectedCount: 2);
        return (letters[0], letters[1]);
    }

    public async Task<string> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var diskNumber = ExtractDiskNumber(target.DeviceId);
        var script = BuildLegacyFat32LayoutScript(diskNumber);
        var output = await RunPowerShellAsync(script, ct);
        var letters = ParseDriveLetters(output, expectedCount: 1);
        return letters[0];
    }

    internal static int ExtractDiskNumber(string deviceId)
    {
        // deviceId looks like "\\.\PHYSICALDRIVE1".
        var match = Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new ArgumentException($"Tidak bisa menentukan nomor disk dari '{deviceId}'.");
        }

        return int.Parse(match.Groups[1].Value);
    }

    // Single-quoted PowerShell strings throughout (no embedded double quotes) so the
    // whole script can be wrapped in double quotes as one process argument without
    // needing to escape anything inside it.
    // Clear-Disk (RemoveData+RemoveOEM) wipes partitions/data but does not reliably
    // reset a disk's PartitionStyle back to RAW — a disk that was already MBR-
    // initialized from a prior run stays MBR-initialized after Clear-Disk. Calling
    // Initialize-Disk unconditionally then fails with "The disk has already been
    // initialized" (reproduced on real hardware on a second/third flash of the same
    // drive in this session). Only initialize when the disk actually comes back RAW.
    private const string InitializeIfRawSnippet =
        "if ((Get-Disk -Number {0}).PartitionStyle -eq 'RAW') {{ Initialize-Disk -Number {0} -PartitionStyle MBR -Confirm:$false }}; ";

    internal static string BuildUefiNtfsLayoutScript(int diskNumber, int bootPartitionMB) =>
        $"$ErrorActionPreference = 'Stop'; " +
        $"Clear-Disk -Number {diskNumber} -RemoveData -RemoveOEM -Confirm:$false; " +
        string.Format(InitializeIfRawSnippet, diskNumber) +
        $"$boot = New-Partition -DiskNumber {diskNumber} -Size {bootPartitionMB}MB -MbrType EFI -IsActive -AssignDriveLetter; " +
        $"Format-Volume -Partition $boot -FileSystem FAT -NewFileSystemLabel 'KANGBOOT' -Confirm:$false -Force | Out-Null; " +
        $"$data = New-Partition -DiskNumber {diskNumber} -UseMaximumSize -AssignDriveLetter; " +
        $"Format-Volume -Partition $data -FileSystem NTFS -NewFileSystemLabel 'KANGBOOT' -Confirm:$false -Force | Out-Null; " +
        $"'{{0}}:|{{1}}:' -f $boot.DriveLetter, $data.DriveLetter";

    internal static string BuildLegacyFat32LayoutScript(int diskNumber) =>
        $"$ErrorActionPreference = 'Stop'; " +
        $"Clear-Disk -Number {diskNumber} -RemoveData -RemoveOEM -Confirm:$false; " +
        string.Format(InitializeIfRawSnippet, diskNumber) +
        $"$part = New-Partition -DiskNumber {diskNumber} -UseMaximumSize -MbrType FAT32 -IsActive -AssignDriveLetter; " +
        $"Format-Volume -Partition $part -FileSystem FAT32 -NewFileSystemLabel 'KANGBOOT' -Confirm:$false -Force | Out-Null; " +
        $"'{{0}}:' -f $part.DriveLetter";

    internal static IReadOnlyList<string> ParseDriveLetters(string output, int expectedCount)
    {
        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (string.IsNullOrEmpty(line))
        {
            throw new IOException("Tidak mendapat drive letter dari proses partisi/format.");
        }

        var letters = line.Split('|');
        if (letters.Length != expectedCount || letters.Any(l => l.Length < 2 || !char.IsLetter(l[0]) || l[^1] != ':'))
        {
            throw new IOException($"Format drive letter tidak dikenali: '{line}'.");
        }

        return letters;
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
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
            ?? throw new IOException("Gagal menjalankan powershell.exe untuk partisi/format drive.");

        // Drain both streams concurrently — see DismRunner/BootsectRunner for why
        // sequential reads risk a pipe-buffer deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"Gagal partisi/format drive (exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? "Tidak ada detail error." : stderr.Trim()));
        }

        return stdout;
    }
}
