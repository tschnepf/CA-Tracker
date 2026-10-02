using SmcCaTracker.App.Services;

namespace SmcCaTracker.Core.Tests;

public sealed class PageWaitsTests
{
    [Fact]
    public async Task AStepThatFinishesInTimeGivesItsResult() =>
        Assert.Equal(7, await PageWaits.WithTimeout(Task.FromResult(7), TimeSpan.FromSeconds(5), "starting"));

    [Fact]
    public async Task AStuckStepEndsWithItsName()
    {
        var stuck = new TaskCompletionSource<int>().Task;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => PageWaits.WithTimeout(stuck, TimeSpan.FromMilliseconds(50), "opening the page"));
        Assert.Equal("The built-in browser got stuck opening the page.", error.Message);
    }

    [Fact]
    public async Task StoppingEndsTheWaitAtOnce()
    {
        using var stop = new CancellationTokenSource();
        var waiting = PageWaits.WithTimeout(new TaskCompletionSource<int>().Task, TimeSpan.FromMinutes(5), "running a script", stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
