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
// shown none of these issues for creation/formatting/drive-letter assignment. DiscUtils
// remains only for the ISO-reading fallback (IsoFileSystemOpener), a lower-risk
// read-only concern that hasn't had a real-hardware bug.
//
// One further real-hardware finding: New-Partition's -MbrType parameter's accepted
// enum values vary by Windows/PowerShell version — this environment's Storage module
// rejects "EFI" outright ("Cannot convert value 'EFI'... Specify one of: FAT12, FAT16,
// Extended, Huge, IFS, FAT32"), even though the UEFI:NTFS boot partition needs the
// 0xEF (EFI System) MBR type byte specifically. diskpart's `set id=` command accepts
// an arbitrary raw type byte and has been stable across Windows versions since XP, so
// the boot partition is created via New-Partition/Format-Volume without specifying a
// type (picking whatever this Windows version defaults to), then diskpart patches just
// the type byte afterward — narrowly using the one tool that reliably supports it,
// while keeping PowerShell for everything that needs structured return values
// (partition numbers, drive letters).
public class Partitioner : IPartitioner
{
    private const int BootPartitionMB = 16;
    private const string EfiSystemMbrTypeHex = "ef";

    public async Task<(string bootDriveLetter, string dataDriveLetter)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var diskNumber = ExtractDiskNumber(target.DeviceId);

        await RunPowerShellAsync(BuildClearAndInitializeScript(diskNumber), ct);

        var (bootPartitionNumber, bootDriveLetter) = await CreatePartitionOnlyAsync(
            diskNumber, sizeMB: BootPartitionMB, isActive: true, ct);

        await SetPartitionTypeAsync(diskNumber, bootPartitionNumber, EfiSystemMbrTypeHex, ct);

        // Real-hardware finding: Windows' Format-Volume cmdlet refuses NTFS on removable
        // media ("Not Supported"). diskpart's `format` command was tried next and turned
        // out to be MORE restrictive, not less — it refused to format ANY filesystem
        // (including FAT) on this removable disk ("The operation is not supported on
        // removable media"), even though Format-Volume had formatted the boot partition's
        // FAT filesystem just fine. So this restriction is specific to diskpart's and
        // Format-Volume's own VDS-backed code paths, not universal — confirmed on real
        // hardware that the classic command-line format.exe (`format X: /FS:NTFS /Q`)
        // succeeds on the same removable disk where both of those failed. format.exe is
        // now used for all actual formatting; diskpart is used only for the boot
        // partition's MBR type-byte patch (`set id=`), which it has always handled fine.
        await FormatWithFormatExeAsync(bootDriveLetter, "FAT", ct);

        var (_, dataDriveLetter) = await CreatePartitionOnlyAsync(
            diskNumber, sizeMB: null, isActive: false, ct);

        await FormatWithFormatExeAsync(dataDriveLetter, "NTFS", ct);

        return (bootDriveLetter, dataDriveLetter);
    }

    public async Task<string> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var diskNumber = ExtractDiskNumber(target.DeviceId);

        await RunPowerShellAsync(BuildClearAndInitializeScript(diskNumber), ct);

        var (_, driveLetter) = await CreatePartitionOnlyAsync(
            diskNumber, sizeMB: null, isActive: true, ct);

        await FormatWithFormatExeAsync(driveLetter, "FAT32", ct);

        return driveLetter;
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

    // Clear-Disk (RemoveData+RemoveOEM) wipes partitions/data but does not reliably
    // reset a disk's PartitionStyle back to RAW — a disk that was already MBR-
    // initialized from a prior run stays MBR-initialized after Clear-Disk. Calling
    // Initialize-Disk unconditionally then fails with "The disk has already been
    // initialized" (reproduced on real hardware on a second/third flash of the same
    // drive in this session). Only initialize when the disk actually comes back RAW.
    internal static string BuildClearAndInitializeScript(int diskNumber) =>
        $"$ErrorActionPreference = 'Stop'; " +
        $"Clear-Disk -Number {diskNumber} -RemoveData -RemoveOEM -Confirm:$false; " +
        $"if ((Get-Disk -Number {diskNumber}).PartitionStyle -eq 'RAW') " +
        $"{{ Initialize-Disk -Number {diskNumber} -PartitionStyle MBR -Confirm:$false }}";

    // Single-quoted PowerShell strings throughout (no embedded double quotes) so the
    // whole script can be wrapped in double quotes as one process argument without
    // needing to escape anything inside it. sizeMB null means -UseMaximumSize.
    // No Format-Volume here — see CreateUefiNtfsLayoutAsync's comment: formatting is
    // done via diskpart instead, since Format-Volume refuses NTFS on removable media.
    internal static string BuildCreatePartitionScript(int diskNumber, int? sizeMB, bool isActive)
    {
        var sizeArg = sizeMB is { } mb ? $"-Size {mb}MB" : "-UseMaximumSize";
        var activeArg = isActive ? " -IsActive" : "";

        return $"$ErrorActionPreference = 'Stop'; " +
            $"$p = New-Partition -DiskNumber {diskNumber} {sizeArg}{activeArg} -AssignDriveLetter; " +
            $"'{{0}}|{{1}}:' -f $p.PartitionNumber, $p.DriveLetter";
    }

    internal static (int partitionNumber, string driveLetter) ParsePartitionResult(string output)
    {
        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (string.IsNullOrEmpty(line))
        {
            throw new IOException("Tidak mendapat info partisi dari proses format drive.");
        }

        var parts = line.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var partitionNumber)
            || parts[1].Length < 2 || !char.IsLetter(parts[1][0]) || parts[1][^1] != ':')
        {
            throw new IOException($"Format hasil partisi tidak dikenali: '{line}'.");
        }

        return (partitionNumber, parts[1]);
    }

    private static async Task<(int partitionNumber, string driveLetter)> CreatePartitionOnlyAsync(
        int diskNumber, int? sizeMB, bool isActive, CancellationToken ct)
    {
        var script = BuildCreatePartitionScript(diskNumber, sizeMB, isActive);
        var output = await RunPowerShellAsync(script, ct);
        return ParsePartitionResult(output);
    }

    internal static string BuildSetPartitionTypeDiskpartScript(int diskNumber, int partitionNumber, string mbrTypeHex) =>
        $"select disk {diskNumber}\r\n" +
        $"select partition {partitionNumber}\r\n" +
        $"set id={mbrTypeHex} override\r\n";

    private static async Task SetPartitionTypeAsync(int diskNumber, int partitionNumber, string mbrTypeHex, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"kangbooting-diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, BuildSetPartitionTypeDiskpartScript(diskNumber, partitionNumber, mbrTypeHex), ct);
        try
        {
            await RunProcessAsync("diskpart.exe", $"/s \"{scriptPath}\"", ct,
                errorPrefix: "Gagal mengatur tipe partisi boot");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    // format.com has no command-line flag to auto-confirm — it prompts on stdin
    // ("Proceed with Format (Y/N)?" and, if the volume already carries a label, "Enter
    // current volume label for drive X:"). Answering blind with several "Y\r\n" lines
    // covers both prompts without needing to parse which one appears.
    internal static string BuildFormatCommandArguments(string driveLetter, string fileSystem) =>
        $"{driveLetter} /FS:{fileSystem} /V:KANGBOOT /Q";

    private static Task FormatWithFormatExeAsync(string driveLetter, string fileSystem, CancellationToken ct) =>
        RunProcessAsync("format.exe", BuildFormatCommandArguments(driveLetter, fileSystem), ct,
            errorPrefix: "Gagal format partisi", stdinInput: "Y\r\nY\r\nY\r\n");

    private static Task<string> RunPowerShellAsync(string script, CancellationToken ct) =>
        RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
            ct,
            errorPrefix: "Gagal partisi/format drive");

    private static async Task<string> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct, string errorPrefix, string? stdinInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException($"Gagal menjalankan {fileName}.");

        // Written before reading stdout/stderr: the child buffers stdin regardless of
        // whether it has asked for it yet, so this can't deadlock against the drains below.
        if (stdinInput is not null)
        {
            await process.StandardInput.WriteAsync(stdinInput);
            process.StandardInput.Close();
        }

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
                $"{errorPrefix} (exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim()));
        }

        return stdout;
    }
}
