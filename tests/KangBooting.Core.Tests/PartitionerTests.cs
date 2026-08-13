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
    public void BuildClearAndInitializeScript_ContainsExpectedCmdlets()
    {
        var script = Partitioner.BuildClearAndInitializeScript(diskNumber: 2);

        Assert.Contains("Clear-Disk -Number 2 -RemoveData -RemoveOEM", script);
        // Regression guard: real-hardware bug — Initialize-Disk called unconditionally
        // after Clear-Disk threw "The disk has already been initialized" on a second/
        // third flash of the same drive, since Clear-Disk doesn't reliably reset
        // PartitionStyle back to RAW. Must be gated on an actual RAW check.
        Assert.Contains("if ((Get-Disk -Number 2).PartitionStyle -eq 'RAW')", script);
        Assert.Contains("Initialize-Disk -Number 2 -PartitionStyle MBR", script);
    }

    [Fact]
    public void BuildCreatePartitionScript_FixedSize_UsesSizeArgument()
    {
        var script = Partitioner.BuildCreatePartitionScript(diskNumber: 1, sizeMB: 16, isActive: true);

        Assert.Contains("New-Partition -DiskNumber 1 -Size 16MB -IsActive -AssignDriveLetter", script);
        // Regression guard: real-hardware bug — New-Partition -MbrType EFI is rejected
        // on some Windows/PowerShell Storage module versions. No formatting happens
        // here either — both Format-Volume and diskpart's format refuse various
        // filesystems on this removable disk; formatting is done separately via
        // format.exe (BuildFormatCommandArguments), which doesn't have that restriction.
        Assert.DoesNotContain("MbrType", script);
        Assert.DoesNotContain("Format-Volume", script);
        Assert.DoesNotContain("\"", script); // no embedded double quotes — see class comment on escaping
    }

    [Fact]
    public void BuildCreatePartitionScript_NoSize_UsesMaximumSize()
    {
        var script = Partitioner.BuildCreatePartitionScript(diskNumber: 1, sizeMB: null, isActive: false);

        Assert.Contains("New-Partition -DiskNumber 1 -UseMaximumSize -AssignDriveLetter", script);
        Assert.DoesNotContain("-IsActive", script);
    }

    [Fact]
    public void BuildSetPartitionTypeDiskpartScript_SelectsDiskAndPartitionThenSetsId()
    {
        var script = Partitioner.BuildSetPartitionTypeDiskpartScript(diskNumber: 1, partitionNumber: 2, mbrTypeHex: "ef");

        Assert.Equal("select disk 1\r\nselect partition 2\r\nset id=ef override\r\n", script);
    }

    [Fact]
    public void BuildFormatCommandArguments_ComposesFormatExeArguments()
    {
        var args = Partitioner.BuildFormatCommandArguments(driveLetter: "F:", fileSystem: "NTFS");

        Assert.Equal("F: /FS:NTFS /V:KANGBOOT /Q", args);
    }

    [Fact]
    public void ParsePartitionResult_ParsesNumberAndDriveLetter()
    {
        var (number, letter) = Partitioner.ParsePartitionResult("2|F:");

        Assert.Equal(2, number);
        Assert.Equal("F:", letter);
    }

    [Fact]
    public void ParsePartitionResult_UsesLastNonEmptyLine_IgnoringNoise()
    {
        // PowerShell output can carry leading blank lines/whitespace from cmdlet
        // pipelines that don't suppress all output — only the last non-empty line
        // (our own explicit format-string output) should be parsed.
        var output = "\nSome informational noise\n\n1|E:\n";

        var (number, letter) = Partitioner.ParsePartitionResult(output);

        Assert.Equal(1, number);
        Assert.Equal("E:", letter);
    }

    [Fact]
    public void ParsePartitionResult_EmptyOutput_Throws()
    {
        Assert.Throws<IOException>(() => Partitioner.ParsePartitionResult("   \n  \n"));
    }

    [Fact]
    public void ParsePartitionResult_MalformedInput_Throws()
    {
        Assert.Throws<IOException>(() => Partitioner.ParsePartitionResult("not-valid"));
    }
}
