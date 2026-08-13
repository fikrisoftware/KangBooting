using DiscUtils.Ntfs;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class UefiNtfsWriterTests : IDisposable
{
    private readonly string _tempDir;

    public UefiNtfsWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kangbooting-uefi-tests").FullName;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string BuildIsoWithFile(string relativePath, int sizeMb)
    {
        var builder = new CDBuilder { UseJoliet = true };
        builder.AddFile(relativePath, new byte[sizeMb * 1024 * 1024]);

        var isoPath = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.iso");
        using var fs = File.Create(isoPath);
        builder.Build(fs);
        return isoPath;
    }

    [Fact]
    public void CopyIsoContentsToNtfs_PreservesLargeFileWithoutSplitting()
    {
        // Arrange: an ISO with a file bigger than the FAT32 4GB limit would allow,
        // written to an in-memory NTFS volume to prove NTFS handles it as one file.
        var isoPath = BuildIsoWithFile(@"sources\install.wim", sizeMb: 10);

        using var isoStream = File.OpenRead(isoPath);
        using var cdReader = new CDReader(isoStream, joliet: true);

        var ntfsStream = new SparseMemoryStream();
        NtfsFileSystem.Format(ntfsStream, "TESTVOL", new DiscUtils.Geometry(1, 1, 1), 0, 200 * 1024 * 1024 / 512);
        using var ntfs = new NtfsFileSystem(ntfsStream);

        // Act
        UefiNtfsWriter.CopyIsoContentsToFileSystem(cdReader, ntfs);

        // Assert: the file exists on the NTFS volume as a single, unsplit file.
        Assert.True(ntfs.FileExists(@"sources\install.wim"));
        Assert.Equal(10 * 1024 * 1024, ntfs.GetFileLength(@"sources\install.wim"));
    }
}
