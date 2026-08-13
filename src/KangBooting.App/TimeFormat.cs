namespace KangBooting.App;

internal static class TimeFormat
{
    public static string Format(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}
