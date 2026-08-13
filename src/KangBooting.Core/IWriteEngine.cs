namespace KangBooting.Core;

public interface IWriteEngine
{
    Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default);
}
