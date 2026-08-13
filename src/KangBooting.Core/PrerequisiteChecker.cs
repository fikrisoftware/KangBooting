using System.Runtime.Versioning;
using System.Security.Principal;

namespace KangBooting.Core;

public interface IPrerequisiteChecker
{
    // Returns a human-readable issue per unmet prerequisite, or an empty list if
    // everything needed for the given mode is in place. Checked up front so a missing
    // tool (e.g. bootsect.exe) surfaces before the drive is wiped, not partway through.
    IReadOnlyList<string> Check(BootMode mode, string isoPath);
}

[SupportedOSPlatform("windows")]
public class PrerequisiteChecker : IPrerequisiteChecker
{
    public IReadOnlyList<string> Check(BootMode mode, string isoPath)
    {
        var bootloaderPath = Path.Combine(AppContext.BaseDirectory, "assets", "bootx64_signed.efi");

        return BuildIssues(
            isAdministrator: IsRunningAsAdministrator(),
            isoExists: File.Exists(isoPath),
            powerShellAvailable: IsExecutableAvailable("powershell.exe"),
            dismAvailable: IsExecutableAvailable("dism.exe"),
            bootsectAvailable: IsExecutableAvailable("bootsect.exe"),
            bootloaderAssetExists: File.Exists(bootloaderPath),
            mode: mode,
            isoPath: isoPath,
            bootloaderPath: bootloaderPath);
    }

    // Pure/testable: given the state of the world (as booleans, gathered by Check()
    // above via real I/O), decides which issues apply for the given mode.
    internal static IReadOnlyList<string> BuildIssues(
        bool isAdministrator,
        bool isoExists,
        bool powerShellAvailable,
        bool dismAvailable,
        bool bootsectAvailable,
        bool bootloaderAssetExists,
        BootMode mode,
        string isoPath,
        string bootloaderPath)
    {
        var issues = new List<string>();

        if (!isAdministrator)
        {
            issues.Add("Aplikasi tidak berjalan sebagai Administrator. Tutup lalu jalankan ulang sebagai Administrator.");
        }

        if (!isoExists)
        {
            issues.Add($"File ISO tidak ditemukan: {isoPath}");
        }

        if (!powerShellAvailable)
        {
            issues.Add("powershell.exe tidak ditemukan di sistem — dibutuhkan untuk mount ISO.");
        }

        if (mode == BootMode.LegacySplitFat32)
        {
            if (!dismAvailable)
            {
                issues.Add("dism.exe tidak ditemukan di sistem — dibutuhkan untuk memecah install.wim/.esd berukuran besar.");
            }

            if (!bootsectAvailable)
            {
                issues.Add("bootsect.exe tidak ditemukan di sistem — dibutuhkan untuk menulis boot code BIOS/Legacy.");
            }
        }

        if (mode == BootMode.UefiNtfs && !bootloaderAssetExists)
        {
            issues.Add($"File bootloader UEFI tidak ditemukan: {bootloaderPath}");
        }

        return issues;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    // dism.exe/bootsect.exe/powershell.exe all ship in System32 on modern Windows by
    // default (not ADK-only), but fall back to a PATH search in case of an unusual setup.
    private static bool IsExecutableAvailable(string exeName)
    {
        var system32Path = Path.Combine(Environment.SystemDirectory, exeName);
        if (File.Exists(system32Path))
        {
            return true;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exeName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip it.
            }
        }

        return false;
    }
}
