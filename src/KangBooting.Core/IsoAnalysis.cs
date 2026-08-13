namespace KangBooting.Core;

public record IsoAnalysis(
    long? InstallImageSizeBytes,
    bool HasBiosBootSector,
    bool HasUefiBoot);

public enum BootMode
{
    UefiNtfs,
    LegacySplitFat32
}
