using System.Diagnostics;

namespace KangBooting.Core;

public interface IBootsectRunner
{
    Task WriteBootCodeAsync(string driveLetterOrPath, CancellationToken ct = default);
}

// Shells out to the standard Windows bootsect.exe utility to write BIOS+UEFI-compatible
// (bootmgr-style) VBR/boot code onto a target volume — same shell-out pattern as DismRunner.
public class BootsectRunner : IBootsectRunner
{
    public async Task WriteBootCodeAsync(string driveLetterOrPath, CancellationToken ct = default)
    {
        var args = BuildArguments(driveLetterOrPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "bootsect.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Gagal menjalankan bootsect.exe. Pastikan Windows ADK/bootsect tersedia di sistem.");

        // ponytail: same concurrent-drain fix as DismRunner — reading stdout/stderr
        // sequentially risks a pipe-buffer deadlock if bootsect.exe writes enough to fill one.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        string stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        if (!IsSuccessExitCode(process.ExitCode))
        {
            throw new IOException(
                $"Gagal menulis boot code dengan bootsect.exe (exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? "Tidak ada detail error dari bootsect.exe." : stderr.Trim()));
        }
    }

    internal static string BuildArguments(string driveLetterOrPath)
    {
        return $"/nt60 \"{driveLetterOrPath}\" /mbr /force";
    }

    internal static bool IsSuccessExitCode(int exitCode) => exitCode == 0;
}
