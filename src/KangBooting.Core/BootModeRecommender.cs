namespace KangBooting.Core;

public static class BootModeRecommender
{
    private const long FourGigabytes = 4L * 1024 * 1024 * 1024;

    public static BootMode Recommend(IsoAnalysis analysis)
    {
        bool hasLargeFile = analysis.InstallImageSizeBytes is > FourGigabytes;

        if (hasLargeFile && analysis.HasBiosBootSector)
        {
            return BootMode.LegacySplitFat32;
        }

        return BootMode.UefiNtfs;
    }
}
