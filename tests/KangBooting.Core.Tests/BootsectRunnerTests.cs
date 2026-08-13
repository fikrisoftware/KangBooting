using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class BootsectRunnerTests
{
    [Fact]
    public void BuildArguments_ProducesExpectedCommandLine()
    {
        var args = BootsectRunner.BuildArguments("D:");

        Assert.Equal("/nt60 \"D:\" /mbr /force", args);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-2147024784, false)]
    public void IsSuccessExitCode_OnlyZeroIsSuccess(int exitCode, bool expectedSuccess)
    {
        Assert.Equal(expectedSuccess, BootsectRunner.IsSuccessExitCode(exitCode));
    }
}
