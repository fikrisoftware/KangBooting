using DiscUtils.Iso9660;
using KangBooting.Core;

namespace KangBooting.Core.Tests;

public class LegacySplitWriterTests : IDisposable
{
    private readonly string _tempDir;

    public LegacySplitWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kangbooting-legacy-tests").FullName;
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
    public void ExtractIsoToDirectory_RecursesIntoNestedFolders()
    {
        // Arrange: a nested file (sources\install.wim), the exact case a
        // single-level GetDirectories/GetFiles call would silently skip.
        var isoPath = BuildIsoWithFile(@"sources\install.wim", sizeMb: 5);

        using var isoStream = File.OpenRead(isoPath);
        using var cdReader = new CDReader(isoStream, joliet: true);

        var destinationDir = Path.Combine(_tempDir, "extracted");
        Directory.CreateDirectory(destinationDir);

        // Act
        LegacySplitWriter.ExtractIsoToDirectory(cdReader, destinationDir);

        // Assert: the nested file landed at the correct path with the correct size.
        var extractedFile = Path.Combine(destinationDir, "sources", "install.wim");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal(5 * 1024 * 1024, new FileInfo(extractedFile).Length);
    }
}
