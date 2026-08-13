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

        // Real-hardware root cause: Windows' Format-Volume cmdlet (and Disk Management's
        // GUI formatter) refuses to create an NTFS volume on media Windows classifies as
        // "Removable" (most USB flash drives) — this is a genuine, longstanding Windows
        // restriction, not a caching/timing issue ("Update-Disk" was tried first and did
        // NOT fix it — same "Format-Volume : Not Supported" error reproduced identically
        // afterward). diskpart's own `format` command is not subject to this restriction,
        // so both the MBR type-byte patch AND the actual format are done via diskpart for
        // every partition here; PowerShell is used only for New-Partition (disk-level ops,
        // partition creation, and drive-letter assignment, which all worked fine).
        await FormatPartitionViaDiskpartAsync(diskNumber, bootPartitionNumber, "fat", EfiSystemMbrTypeHex, ct);

        var (dataPartitionNumber, dataDriveLetter) = await CreatePartitionOnlyAsync(
            diskNumber, sizeMB: null, isActive: false, ct);

        await FormatPartitionViaDiskpartAsync(diskNumber, dataPartitionNumber, "ntfs", mbrTypeHex: null, ct);

        return (bootDriveLetter, dataDriveLetter);
    }

    public async Task<string> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        var diskNumber = ExtractDiskNumber(target.DeviceId);

        await RunPowerShellAsync(BuildClearAndInitializeScript(diskNumber), ct);

        var (partitionNumber, driveLetter) = await CreatePartitionOnlyAsync(
            diskNumber, sizeMB: null, isActive: true, ct);

        await FormatPartitionViaDiskpartAsync(diskNumber, partitionNumber, "fat32", mbrTypeHex: null, ct);

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

    // mbrTypeHex is only needed for the UEFI:NTFS boot partition (0xEF); null skips it.
    // The `set id=` line must come before `format` in the same diskpart invocation.
    internal static string BuildFormatPartitionDiskpartScript(
        int diskNumber, int partitionNumber, string fileSystem, string? mbrTypeHex)
    {
        var setIdLine = mbrTypeHex is null ? "" : $"set id={mbrTypeHex} override\r\n";
        return $"select disk {diskNumber}\r\n" +
            $"select partition {partitionNumber}\r\n" +
            setIdLine +
            $"format fs={fileSystem} quick label=\"KANGBOOT\"\r\n";
    }

    private static async Task FormatPartitionViaDiskpartAsync(
        int diskNumber, int partitionNumber, string fileSystem, string? mbrTypeHex, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"kangbooting-diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, BuildFormatPartitionDiskpartScript(diskNumber, partitionNumber, fileSystem, mbrTypeHex), ct);
        try
        {
            await RunProcessAsync("diskpart.exe", $"/s \"{scriptPath}\"", ct,
                errorPrefix: "Gagal format partisi");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static Task<string> RunPowerShellAsync(string script, CancellationToken ct) =>
        RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
            ct,
            errorPrefix: "Gagal partisi/format drive");

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct, string errorPrefix)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException($"Gagal menjalankan {fileName}.");

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
