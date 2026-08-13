namespace KangBooting.Core;

// Shared by both IWriteEngine implementations: after DiscUtils writes a new partition
// table directly to the raw disk, Windows' mount manager needs a moment (often several
// seconds, observed on real hardware) after Partitioner.RefreshPartitionTable's
// IOCTL_DISK_UPDATE_PROPERTIES to finish assigning a drive letter to the newly created
// partition — a short one-shot check isn't enough in practice.
internal static class DriveLetterResolver
{
    private const int RetryAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public static async Task<string?> ResolveWithRetryAsync(
        IDriveService driveService, string deviceId, int partitionIndex, CancellationToken ct)
    {
        for (int attempt = 0; attempt < RetryAttempts; attempt++)
        {
            var driveLetter = driveService.GetDriveLetterForPartition(deviceId, partitionIndex);
            if (driveLetter is not null)
            {
                return driveLetter;
            }

            await Task.Delay(RetryDelay, ct);
        }

        return null;
    }
}
