using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class BootModeRecommenderTests
{
    private const long FourGb = 4L * 1024 * 1024 * 1024;

    [Fact]
    public void LargeImage_NoBiosBoot_RecommendsUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: FourGb + 1,
            HasBiosBootSector: false,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }

    [Fact]
    public void LargeImage_WithBiosBoot_RecommendsLegacySplit()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: FourGb + 1,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.LegacySplitFat32, result);
    }

    [Fact]
    public void NoLargeFile_DefaultsToUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: 2L * 1024 * 1024 * 1024,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }

    [Fact]
    public void NoInstallImageAtAll_DefaultsToUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: null,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }
}
