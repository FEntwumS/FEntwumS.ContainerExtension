using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

public sealed class HandlerVerdictTests
{
    [Fact]
    public void Track_RecordsALineTheHandlerRejects()
    {
        var verdict = new HandlerVerdict();
        var handler = verdict.Track(line => line != "ERROR: synthesis failed")!;

        Assert.True(handler("Info: reading design"));
        Assert.False(verdict.Rejected);

        Assert.False(handler("ERROR: synthesis failed"));
        Assert.True(verdict.Rejected);

        Assert.True(handler("Info: done"));
        Assert.True(verdict.Rejected);
    }

    [Fact]
    public void Track_LeavesAMissingHandlerMissing()
    {
        Assert.Null(new HandlerVerdict().Track(null));
    }
}
