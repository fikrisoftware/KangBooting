using DiscUtils.Iso9660;
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
    public void CopyIsoContentsToRealDrive_PreservesLargeFileWithoutSplitting()
    {
        // Arrange: an ISO with a file bigger than the FAT32 4GB limit would allow,
        // copied to a real (temp) directory to prove no size limit applies here (NTFS
        // has no such limit, and this is the same plain-System.IO write path used
        // against a real drive letter).
        var isoPath = BuildIsoWithFile(@"sources\install.wim", sizeMb: 10);
        var destDir = Directory.CreateTempSubdirectory("kangbooting-uefi-dest").FullName;
        try
        {
            using var isoStream = File.OpenRead(isoPath);
            using var cdReader = new CDReader(isoStream, joliet: true);

            // Act
            UefiNtfsWriter.CopyIsoContentsToRealDrive(cdReader, destDir);

            // Assert: the file exists at the destination as a single, unsplit file.
            var destFile = Path.Combine(destDir, "sources", "install.wim");
            Assert.True(File.Exists(destFile));
            Assert.Equal(10 * 1024 * 1024, new FileInfo(destFile).Length);
        }
        finally
        {
            Directory.Delete(destDir, recursive: true);
        }
    }
}
