using System.Security.Cryptography;

namespace KangBooting.Core;

public interface IChecksumService
{
    Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default);
    bool Matches(string hashA, string hashB);
}

public class ChecksumService : IChecksumService
{
    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public bool Matches(string hashA, string hashB)
    {
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }
}
