using DiscUtils.Iso9660;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class IsoInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public IsoInspectorTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kangbooting-tests").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string BuildIso(bool includeBiosBoot, bool includeUefiBoot, int installWimSizeMb, bool includeInstallImage = true)
    {
        var builder = new CDBuilder { UseJoliet = true };

        if (includeInstallImage)
        {
            var wimBytes = new byte[installWimSizeMb * 1024 * 1024];
            builder.AddFile(@"sources\install.wim", wimBytes);
        }

        if (includeBiosBoot)
        {
            builder.AddFile(@"boot\etfsboot.com", new byte[512]);
        }

        if (includeUefiBoot)
        {
            builder.AddFile(@"efi\boot\bootx64.efi", new byte[1024]);
        }

        var isoPath = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.iso");
        using (var fs = File.Create(isoPath))
        {
            builder.Build(fs);
        }

        return isoPath;
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsLargeInstallWim()
    {
        var isoPath = BuildIso(includeBiosBoot: false, includeUefiBoot: true, installWimSizeMb: 10);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.NotNull(result.InstallImageSizeBytes);
        Assert.Equal(10 * 1024 * 1024, result.InstallImageSizeBytes);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsBiosBootSector()
    {
        var isoPath = BuildIso(includeBiosBoot: true, includeUefiBoot: true, installWimSizeMb: 1);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.True(result.HasBiosBootSector);
        Assert.True(result.HasUefiBoot);
    }

    [Fact]
    public async Task AnalyzeAsync_NoBiosBootSector_ReportsFalse()
    {
        var isoPath = BuildIso(includeBiosBoot: false, includeUefiBoot: true, installWimSizeMb: 1);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.False(result.HasBiosBootSector);
    }

    [Fact]
    public async Task AnalyzeAsync_NoInstallImage_ReturnsNull()
    {
        var isoPath = BuildIso(includeBiosBoot: false, includeUefiBoot: false, installWimSizeMb: 0, includeInstallImage: false);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.Null(result.InstallImageSizeBytes);
    }
}
