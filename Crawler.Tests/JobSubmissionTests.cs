using Crawler.Infrastructure;
using CrawlerJob.Controllers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crawler.Tests;

public sealed class JobSubmissionTests
{
    [Fact]
    public async Task Create_normalizes_and_persists_job_before_publishing_message()
    {
        var store = new RecordingJobStore();
        var services = new ServiceCollection();
        services.AddMassTransitTestHarness();
        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var controller = new JobsController(store, provider.GetRequiredService<IBus>(), new AllowCrawlUrlPolicy());

            var response = await controller.Create(
                new CreateJobRequest("https://example.com:443/start#section", null),
                CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(response);
            Assert.Equal(202, accepted.StatusCode);
            Assert.Equal("https://example.com/start", store.Url);
            Assert.Equal(2, store.MaxDepth);
            Assert.Equal(store.JobId, Assert.IsType<CreateJobResponse>(accepted.Value).JobId);
            var published = harness.Published.Select<CrawlMessage>().Single();
            Assert.Equal(store.JobId, published.Context.Message.JobId);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Theory]
    [InlineData("not a URL", 2)]
    [InlineData("ftp://example.com/file", 2)]
    [InlineData("https://example.com", -1)]
    [InlineData("https://example.com", 11)]
    public async Task Create_rejects_invalid_url_or_depth_without_persisting_or_publishing(
        string url, int maxDepth)
    {
        var store = new RecordingJobStore();
        var services = new ServiceCollection();
        services.AddMassTransitTestHarness();
        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var controller = new JobsController(store, provider.GetRequiredService<IBus>(), new AllowCrawlUrlPolicy());

            var response = await controller.Create(
                new CreateJobRequest(url, maxDepth),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(response);
            Assert.Null(store.JobId);
            Assert.False(await harness.Published.Any<CrawlMessage>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Create_rejects_loopback_address()
    {
        var store = new RecordingJobStore();
        var services = new ServiceCollection();
        services.AddMassTransitTestHarness();
        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var controller = new JobsController(store, provider.GetRequiredService<IBus>(),
                new PublicHttpUrlPolicy());

            var response = await controller.Create(
                new CreateJobRequest("http://127.0.0.1/", 2),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(response);
            Assert.Null(store.JobId);
            Assert.False(await harness.Published.Any<CrawlMessage>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class RecordingJobStore : ICrawlJobStore
    {
        public Guid? JobId { get; private set; }
        public string? Url { get; private set; }
        public int? MaxDepth { get; private set; }

        public Task CreateJobAsync(Guid id, string url, int maxDepth, CancellationToken ct)
        {
            JobId = id;
            Url = url;
            MaxDepth = maxDepth;
            return Task.CompletedTask;
        }

        public Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<CrawlJob?>(null);

        public Task<HistoryResult> GetHistoryAsync(int page, int pageSize, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<PageNode>> GetTreeAsync(Guid jobId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> CancelJobAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class AllowCrawlUrlPolicy : ICrawlUrlPolicy
    {
        public Task EnsurePublicHttpUrlAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;
    }
}
