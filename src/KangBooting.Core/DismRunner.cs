using System.Diagnostics;

namespace KangBooting.Core;

public interface IDismRunner
{
    Task SplitWimAsync(string wimPath, string outputSwmPath, int maxSizeMb, CancellationToken ct = default);
}

public class DismRunner : IDismRunner
{
    public async Task SplitWimAsync(string wimPath, string outputSwmPath, int maxSizeMb, CancellationToken ct = default)
    {
        var args = BuildSplitArguments(wimPath, outputSwmPath, maxSizeMb);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Gagal menjalankan dism.exe. Pastikan Windows ADK/DISM tersedia di sistem.");

        // ponytail: dism.exe streams verbose progress to stdout during /Split-Image;
        // reading stderr alone risks a pipe-buffer deadlock, so drain both concurrently.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        string stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        if (!IsSuccessExitCode(process.ExitCode))
        {
            throw new IOException(
                $"Gagal memecah install.wim (dism.exe exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? "Tidak ada detail error dari dism.exe." : stderr.Trim()));
        }

        // Deliberately does not delete wimPath: this method only reads the source (DISM
        // needs no write access to it), and callers may pass a read-only source (e.g. a
        // natively-mounted ISO's install.wim, which cannot be deleted) — cleanup of any
        // caller-owned temp copy is the caller's responsibility, not this method's.
    }

    internal static string BuildSplitArguments(string wimPath, string outputSwmPath, int maxSizeMb)
    {
        return $"/Split-Image /ImageFile:\"{wimPath}\" /SWMFile:\"{outputSwmPath}\" /FileSize:{maxSizeMb}";
    }

    internal static bool IsSuccessExitCode(int exitCode) => exitCode == 0;
}
