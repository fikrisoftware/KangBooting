using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class PartitionerTests
{
    [Theory]
    [InlineData(@"\\.\PHYSICALDRIVE1", 1)]
    [InlineData(@"\\.\PHYSICALDRIVE0", 0)]
    [InlineData(@"\\.\PHYSICALDRIVE12", 12)]
    public void ExtractDiskNumber_ParsesPhysicalDrivePath(string deviceId, int expected)
    {
        Assert.Equal(expected, Partitioner.ExtractDiskNumber(deviceId));
    }

    [Fact]
    public void ExtractDiskNumber_InvalidFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => Partitioner.ExtractDiskNumber(@"C:\not-a-physical-drive"));
    }

    [Fact]
    public void BuildUefiNtfsLayoutScript_ContainsExpectedCmdletsAndDiskNumber()
    {
        var script = Partitioner.BuildUefiNtfsLayoutScript(diskNumber: 2, bootPartitionMB: 16);

        Assert.Contains("Clear-Disk -Number 2", script);
        // Regression guard: real-hardware bug — Initialize-Disk called unconditionally
        // after Clear-Disk threw "The disk has already been initialized" on a second/
        // third flash of the same drive, since Clear-Disk doesn't reliably reset
        // PartitionStyle back to RAW. Must be gated on an actual RAW check.
        Assert.Contains("if ((Get-Disk -Number 2).PartitionStyle -eq 'RAW')", script);
        Assert.Contains("Initialize-Disk -Number 2 -PartitionStyle MBR", script);
        Assert.Contains("New-Partition -DiskNumber 2 -Size 16MB -MbrType EFI -IsActive -AssignDriveLetter", script);
        Assert.Contains("Format-Volume -Partition $boot -FileSystem FAT", script);
        Assert.Contains("New-Partition -DiskNumber 2 -UseMaximumSize -AssignDriveLetter", script);
        Assert.Contains("Format-Volume -Partition $data -FileSystem NTFS", script);
        Assert.DoesNotContain("\"", script); // no embedded double quotes — see class comment on escaping
    }

    [Fact]
    public void BuildLegacyFat32LayoutScript_ContainsExpectedCmdletsAndDiskNumber()
    {
        var script = Partitioner.BuildLegacyFat32LayoutScript(diskNumber: 3);

        Assert.Contains("Clear-Disk -Number 3", script);
        Assert.Contains("Initialize-Disk -Number 3 -PartitionStyle MBR", script);
        Assert.Contains("New-Partition -DiskNumber 3 -UseMaximumSize -MbrType FAT32 -IsActive -AssignDriveLetter", script);
        Assert.Contains("Format-Volume -Partition $part -FileSystem FAT32", script);
        Assert.DoesNotContain("\"", script);
    }

    [Fact]
    public void ParseDriveLetters_TwoLetters_ParsesCorrectly()
    {
        var letters = Partitioner.ParseDriveLetters("E:|F:", expectedCount: 2);

        Assert.Equal(new[] { "E:", "F:" }, letters);
    }

    [Fact]
    public void ParseDriveLetters_OneLetter_ParsesCorrectly()
    {
        var letters = Partitioner.ParseDriveLetters("G:", expectedCount: 1);

        Assert.Equal(new[] { "G:" }, letters);
    }

    [Fact]
    public void ParseDriveLetters_UsesLastNonEmptyLine_IgnoringNoise()
    {
        // PowerShell output can carry leading blank lines/whitespace from cmdlet
        // pipelines that don't suppress all output — only the last non-empty line
        // (our own explicit format-string output) should be parsed.
        var output = "\nSome informational noise\n\nE:|F:\n";

        var letters = Partitioner.ParseDriveLetters(output, expectedCount: 2);

        Assert.Equal(new[] { "E:", "F:" }, letters);
    }

    [Fact]
    public void ParseDriveLetters_EmptyOutput_Throws()
    {
        Assert.Throws<IOException>(() => Partitioner.ParseDriveLetters("   \n  \n", expectedCount: 1));
    }

    [Fact]
    public void ParseDriveLetters_WrongCount_Throws()
    {
        Assert.Throws<IOException>(() => Partitioner.ParseDriveLetters("E:|F:", expectedCount: 1));
    }

    [Fact]
    public void ParseDriveLetters_MalformedLetter_Throws()
    {
        Assert.Throws<IOException>(() => Partitioner.ParseDriveLetters("not-a-letter", expectedCount: 1));
    }
}
