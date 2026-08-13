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
}
