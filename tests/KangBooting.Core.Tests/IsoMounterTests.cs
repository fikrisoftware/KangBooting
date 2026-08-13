using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class IsoMounterTests
{
    [Fact]
    public void EscapeForPowerShell_DoublesSingleQuotes()
    {
        Assert.Equal("it''s a test", IsoMounter.EscapeForPowerShell("it's a test"));
    }

    [Fact]
    public void EscapeForPowerShell_NoQuotes_Unchanged()
    {
        Assert.Equal(@"D:\path\file.iso", IsoMounter.EscapeForPowerShell(@"D:\path\file.iso"));
    }

    [Fact]
    public void BuildGetExistingMountScript_EmbedsEscapedPath()
    {
        var script = IsoMounter.BuildGetExistingMountScript(@"D:\it's a test\file.iso");

        Assert.Contains(@"'D:\it''s a test\file.iso'", script);
        Assert.Contains("Get-DiskImage", script);
        Assert.Contains("Attached", script);
    }

    [Fact]
    public void BuildMountScript_EmbedsEscapedPath()
    {
        var script = IsoMounter.BuildMountScript(@"D:\iso\file.iso");

        Assert.Contains(@"'D:\iso\file.iso'", script);
        Assert.Contains("Mount-DiskImage", script);
        Assert.Contains("DriveLetter", script);
    }

    [Fact]
    public void BuildDismountScript_EmbedsEscapedPath()
    {
        var script = IsoMounter.BuildDismountScript(@"D:\iso\file.iso");

        Assert.Contains(@"'D:\iso\file.iso'", script);
        Assert.Contains("Dismount-DiskImage", script);
    }
}
