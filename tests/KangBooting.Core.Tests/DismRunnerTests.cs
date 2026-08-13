using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class DismRunnerTests
{
    [Fact]
    public void BuildSplitArguments_ProducesExpectedCommandLine()
    {
        var args = DismRunner.BuildSplitArguments(
            wimPath: @"D:\staging\sources\install.wim",
            outputSwmPath: @"D:\staging\sources\install.swm",
            maxSizeMb: 4000);

        Assert.Equal(
            "/Split-Image /ImageFile:\"D:\\staging\\sources\\install.wim\" " +
            "/SWMFile:\"D:\\staging\\sources\\install.swm\" /FileSize:4000",
            args);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(87, false)]
    [InlineData(-2147024784, false)]
    public void IsSuccessExitCode_OnlyZeroIsSuccess(int exitCode, bool expectedSuccess)
    {
        Assert.Equal(expectedSuccess, DismRunner.IsSuccessExitCode(exitCode));
    }
}
