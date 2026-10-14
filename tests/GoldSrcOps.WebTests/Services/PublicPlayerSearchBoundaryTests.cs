using AwesomeAssertions;
using GoldSrcOps.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.WebTests.Services;

public sealed class PublicPlayerSearchBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Slow_form_stops_before_antiforgery_and_page_processing(bool callerCancels)
    {
        using var services = new ServiceCollection().AddSingleton(new PublicPlayerSearchOptions(true)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var reachedPage = false;
        app.UsePublicPlayerSearchBoundary();
        app.Run(_ => { reachedPage = true; return Task.CompletedTask; });
        using var body = new SlowBody();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = cancellation.Token };
        context.Request.Method = "POST";
        context.Request.Path = "/leaderboard";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = body;
        context.Response.Body = output;
        var pending = app.Build()(context);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (callerCancels)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            context.Response.StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
        }
        reachedPage.Should().BeFalse();
    }

    private sealed class SlowBody : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
