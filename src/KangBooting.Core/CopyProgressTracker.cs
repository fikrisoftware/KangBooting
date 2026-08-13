using System.Diagnostics;

namespace KangBooting.Core;

// Shared byte-level progress reporting for the two IWriteEngine implementations' file
// copy loops. Previously these loops reported progress once before starting and once
// after finishing an entire multi-GB copy (e.g. "10% -> jump to 95%"), and never checked
// the CancellationToken mid-copy, so Cancel did nothing until the loop finished on its
// own. This tracks bytes copied against a known total to report smooth percent/speed/ETA
// and throws OperationCanceledException between chunks so Cancel actually takes effect.
internal sealed class CopyProgressTracker
{
    private const int CopyBufferSize = 4 * 1024 * 1024;

    private readonly IProgress<WriteProgress>? _progress;
    private readonly double _rangeStart;
    private readonly double _rangeSpan;
    private readonly string _operationLabel;
    private readonly long _totalBytes;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _bytesCopied;

    public CopyProgressTracker(
        IProgress<WriteProgress>? progress, double rangeStart, double rangeSpan, string operationLabel, long totalBytes)
    {
        _progress = progress;
        _rangeStart = rangeStart;
        _rangeSpan = rangeSpan;
        _operationLabel = operationLabel;
        _totalBytes = totalBytes;
    }

    private void Report()
    {
        if (_progress is null)
        {
            return;
        }

        double fraction = _totalBytes > 0 ? Math.Min(1.0, (double)_bytesCopied / _totalBytes) : 1.0;
        double percent = _rangeStart + (fraction * _rangeSpan);

        double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
        double bytesPerSecond = elapsedSeconds > 0 ? _bytesCopied / elapsedSeconds : 0;
        TimeSpan? eta = bytesPerSecond > 0 && _totalBytes > _bytesCopied
            ? TimeSpan.FromSeconds((_totalBytes - _bytesCopied) / bytesPerSecond)
            : null;

        _progress.Report(new WriteProgress(percent, bytesPerSecond, eta, _operationLabel));
    }

    // Copies source to destination in chunks, updating the tracker (if any) and checking
    // for cancellation after every chunk — the actual fix for Cancel not working during
    // a large file's copy.
    public static void CopyStreamWithProgress(
        Stream source, Stream destination, CopyProgressTracker? tracker, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, bytesRead);
            tracker?.AddBytesCopied(bytesRead);
        }
    }

    private void AddBytesCopied(long bytes)
    {
        _bytesCopied += bytes;
        Report();
    }
}
