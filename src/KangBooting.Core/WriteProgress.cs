namespace KangBooting.Core;

public record WriteProgress(
    double PercentComplete,
    double BytesPerSecond,
    TimeSpan? EstimatedTimeRemaining,
    string CurrentOperation);
