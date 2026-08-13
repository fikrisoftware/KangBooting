using DiscUtils.Fat;
using DiscUtils.Ntfs;

namespace KangBooting.Core;

public record PartitionHandle(string DeviceId, int PartitionIndex);

public interface IPartitioner
{
    Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);

    Task<PartitionHandle> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);

    Task WriteBootloaderImageAsync(
        PartitionHandle partition, string imagePath, CancellationToken ct = default);

    NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition);

    FatFileSystem OpenFat32FileSystem(PartitionHandle partition);

    // Releases any physical-disk handles opened by Open*FileSystem above. Callers
    // (IWriteEngine implementations) must call this once done with the returned
    // filesystem(s) — in a finally block, so it runs on both success and failure —
    // otherwise a leaked handle from one write blocks a subsequent retry within the
    // same process from reopening the same physical disk.
    void ReleaseOpenDisks();
}
