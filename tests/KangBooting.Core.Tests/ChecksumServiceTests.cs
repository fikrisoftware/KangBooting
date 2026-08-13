using System.Text;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class ChecksumServiceTests
{
    [Fact]
    public async Task ComputeSha256Async_KnownInput_ReturnsKnownHash()
    {
        var service = new ChecksumService();
        var bytes = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(bytes);

        var hash = await service.ComputeSha256Async(stream);

        // Precomputed SHA256 of "hello world"
        Assert.Equal(
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
            hash);
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        var service = new ChecksumService();

        Assert.True(service.Matches(
            "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9",
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"));
    }

    [Fact]
    public void Matches_DifferentHashes_ReturnsFalse()
    {
        var service = new ChecksumService();

        Assert.False(service.Matches("abc123", "def456"));
    }
}
