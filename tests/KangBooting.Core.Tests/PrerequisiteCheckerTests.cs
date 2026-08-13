using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class PrerequisiteCheckerTests
{
    [Fact]
    public void BuildIssues_AllOk_ReturnsEmpty()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: true, isoExists: true, powerShellAvailable: true,
            dismAvailable: true, bootsectAvailable: true, bootloaderAssetExists: true,
            mode: BootMode.LegacySplitFat32, isoPath: "x.iso", bootloaderPath: "boot.efi");

        Assert.Empty(issues);
    }

    [Fact]
    public void BuildIssues_NotAdministrator_ReportsIssue()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: false, isoExists: true, powerShellAvailable: true,
            dismAvailable: true, bootsectAvailable: true, bootloaderAssetExists: true,
            mode: BootMode.UefiNtfs, isoPath: "x.iso", bootloaderPath: "boot.efi");

        Assert.Contains(issues, i => i.Contains("Administrator"));
    }

    [Fact]
    public void BuildIssues_LegacyMode_MissingDismAndBootsect_ReportsBoth()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: true, isoExists: true, powerShellAvailable: true,
            dismAvailable: false, bootsectAvailable: false, bootloaderAssetExists: true,
            mode: BootMode.LegacySplitFat32, isoPath: "x.iso", bootloaderPath: "boot.efi");

        Assert.Contains(issues, i => i.Contains("dism.exe"));
        Assert.Contains(issues, i => i.Contains("bootsect.exe"));
    }

    [Fact]
    public void BuildIssues_UefiMode_DoesNotCheckDismOrBootsect()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: true, isoExists: true, powerShellAvailable: true,
            dismAvailable: false, bootsectAvailable: false, bootloaderAssetExists: true,
            mode: BootMode.UefiNtfs, isoPath: "x.iso", bootloaderPath: "boot.efi");

        Assert.Empty(issues);
    }

    [Fact]
    public void BuildIssues_UefiMode_MissingBootloaderAsset_ReportsIssue()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: true, isoExists: true, powerShellAvailable: true,
            dismAvailable: true, bootsectAvailable: true, bootloaderAssetExists: false,
            mode: BootMode.UefiNtfs, isoPath: "x.iso", bootloaderPath: @"C:\boot.efi");

        Assert.Contains(issues, i => i.Contains(@"C:\boot.efi"));
    }

    [Fact]
    public void BuildIssues_IsoMissing_ReportsPathInMessage()
    {
        var issues = PrerequisiteChecker.BuildIssues(
            isAdministrator: true, isoExists: false, powerShellAvailable: true,
            dismAvailable: true, bootsectAvailable: true, bootloaderAssetExists: true,
            mode: BootMode.UefiNtfs, isoPath: @"D:\missing.iso", bootloaderPath: "boot.efi");

        Assert.Contains(issues, i => i.Contains(@"D:\missing.iso"));
    }
}
