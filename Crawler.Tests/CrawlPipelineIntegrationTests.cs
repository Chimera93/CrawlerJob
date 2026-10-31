using System.Net;
using System.Net.Http.Json;
using Crawler.Infrastructure;
using Crawler.Worker;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Crawler.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrawlPipelineCollection : ICollectionFixture<IntegrationFixture>
{
    public const string Name = "Crawl pipeline";
}

[Collection(CrawlPipelineCollection.Name)]
public sealed class CrawlPipelineIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Read_endpoints_return_persisted_job_tree_and_history()
    {
        var jobId = Guid.NewGuid();
        const string rootUrl = "https://read-path.example/";
        const string childUrl = "https://read-path.example/child";
        await fixture.Store.CreateJobAsync(jobId, rootUrl, 1, CancellationToken.None);
        await fixture.Store.SetRunningAsync(jobId, CancellationToken.None);
        await fixture.Store.SavePageAsync(
            new CrawlPage(jobId, rootUrl, null, 0, 0.5),
            [childUrl, "https://outside.example/"],
            CancellationToken.None);
        await fixture.Store.SavePageAsync(
            new CrawlPage(jobId, childUrl, rootUrl, 1, 1),
            [],
            CancellationToken.None);
        await fixture.Store.CompleteAsync(jobId, CancellationToken.None);

        await using var api = new ApiFactory(
            fixture.PostgresConnectionString, fixture.RabbitMq.GetConnectionString());
        using var client = api.CreateClient();

        using var detailResponse = await client.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<CrawlJob>();
        Assert.NotNull(detail);
        Assert.Equal(jobId, detail.JobId);
        Assert.Equal(rootUrl, detail.Url);
        Assert.Equal("Completed", detail.Status);
        Assert.Equal(1, detail.MaxDepth);
        Assert.Equal(2, detail.PagesCrawled);
        Assert.NotNull(detail.CompletedAt);

        using var treeResponse = await client.GetAsync($"/api/jobs/{jobId}/tree");
        Assert.Equal(HttpStatusCode.OK, treeResponse.StatusCode);
        var tree = await treeResponse.Content.ReadFromJsonAsync<IReadOnlyList<PageNode>>();
        var root = Assert.Single(tree!, node => node.Url == rootUrl);
        Assert.Equal(0.5, root.DomainLinkRatio);
        var child = Assert.Single(root.Children);
        Assert.Equal(childUrl, child.Url);
        Assert.Equal(1, child.DomainLinkRatio);
        Assert.Empty(child.Children);

        using var historyResponse = await client.GetAsync("/api/jobs?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await historyResponse.Content.ReadFromJsonAsync<HistoryResult>();
        Assert.NotNull(history);
        Assert.Equal(1, history.Page);
        Assert.Equal(100, history.PageSize);
        Assert.Contains(history.Items, item => item.JobId == jobId && item.Status == "Completed");

        using var missingJobResponse = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missingJobResponse.StatusCode);
        using var missingTreeResponse = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/tree");
        Assert.Equal(HttpStatusCode.NotFound, missingTreeResponse.StatusCode);
        using var invalidPageResponse = await client.GetAsync("/api/jobs?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPageResponse.StatusCode);
        using var invalidPageSizeResponse = await client.GetAsync("/api/jobs?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPageSizeResponse.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Api_submission_is_retried_crawled_and_persisted()
    {
        var rootRequests = 0;
        var site = await StartTestSiteAsync(() =>
        {
            if (Interlocked.Increment(ref rootRequests) == 1)
                return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
            return Results.Content("<a href='/child'>child</a>", "text/html");
        });

        try
        {
            await using var provider = CreateWorkerProvider(fixture.Store, fixture.RabbitMq.GetConnectionString());
            var bus = provider.GetRequiredService<IBusControl>();
            await bus.StartAsync();
            try
            {
                await using var api = new ApiFactory(
                    fixture.PostgresConnectionString, fixture.RabbitMq.GetConnectionString());
                using var client = api.CreateClient();
                var response = await client.PostAsJsonAsync("/api/jobs", new
                {
                    url = site.RootUrl,
                    maxDepth = 1
                });
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                using var responseBody = await response.Content.ReadAsStreamAsync();
                using var json = await System.Text.Json.JsonDocument.ParseAsync(responseBody);
                var jobId = json.RootElement.GetProperty("jobId").GetGuid();

                var job = await WaitForTerminalJobAsync(fixture.Store, jobId);

                Assert.Equal("Completed", job.Status);
                Assert.Equal(site.RootUrl, job.Url);
                Assert.Equal(1, job.MaxDepth);
                Assert.Equal(2, job.PagesCrawled);
                Assert.Equal(2, Volatile.Read(ref rootRequests));

                var tree = await fixture.Store.GetTreeAsync(jobId, CancellationToken.None);
                var root = Assert.Single(tree, node => node.Url == site.RootUrl);
                var child = Assert.Single(root.Children);
                Assert.Equal(new Uri(new Uri(site.RootUrl), "/child").AbsoluteUri, child.Url);
                Assert.Equal(1d, root.DomainLinkRatio);

                var history = await fixture.Store.GetHistoryAsync(1, 100, CancellationToken.None);
                Assert.Contains(history.Items, item => item.JobId == jobId && item.Status == "Completed");
            }
            finally
            {
                await bus.StopAsync();
            }
        }
        finally
        {
            await site.App.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Repeated_delivery_of_completed_job_does_not_repeat_crawl()
    {
        var rootRequests = 0;
        var childRequests = 0;
        var site = await StartTestSiteAsync(() =>
        {
            Interlocked.Increment(ref rootRequests);
            return Results.Content("<a href='/child'>child</a>", "text/html");
        }, () =>
        {
            Interlocked.Increment(ref childRequests);
            return Results.Content("<p>child page</p>", "text/html");
        });

        try
        {
            var trackingStore = new TerminalReadSignalingStore(fixture.Store);
            await using var provider = CreateWorkerProvider(trackingStore, fixture.RabbitMq.GetConnectionString());
            var bus = provider.GetRequiredService<IBusControl>();
            await bus.StartAsync();
            try
            {
                await using var api = new ApiFactory(
                    fixture.PostgresConnectionString, fixture.RabbitMq.GetConnectionString());
                using var client = api.CreateClient();
                var jobId = await SubmitJobAsync(client, site.RootUrl, 1);
                var completedJob = await WaitForTerminalJobAsync(fixture.Store, jobId);
                Assert.Equal("Completed", completedJob.Status);

                await bus.Publish(new CrawlMessage(jobId));
                await trackingStore.TerminalRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var job = await fixture.Store.GetJobAsync(jobId, CancellationToken.None);
                Assert.NotNull(job);
                Assert.Equal("Completed", job.Status);
                Assert.Equal(2, job.PagesCrawled);
                Assert.Equal(1, Volatile.Read(ref rootRequests));
                Assert.Equal(1, Volatile.Read(ref childRequests));
            }
            finally
            {
                await bus.StopAsync();
            }
        }
        finally
        {
            await site.App.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Retry_after_partial_persistence_resumes_without_duplicate_pages_or_edges()
    {
        var rootRequests = 0;
        var childRequests = 0;
        var site = await StartTestSiteAsync(() =>
        {
            Interlocked.Increment(ref rootRequests);
            return Results.Content("<a href='/child'>child</a>", "text/html");
        }, () =>
        {
            if (Interlocked.Increment(ref childRequests) == 1)
                return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
            return Results.Content("<p>child page</p>", "text/html");
        });

        try
        {
            await using var provider = CreateWorkerProvider(fixture.Store, fixture.RabbitMq.GetConnectionString());
            var bus = provider.GetRequiredService<IBusControl>();
            await bus.StartAsync();
            try
            {
                await using var api = new ApiFactory(
                    fixture.PostgresConnectionString, fixture.RabbitMq.GetConnectionString());
                using var client = api.CreateClient();
                var jobId = await SubmitJobAsync(client, site.RootUrl, 1);
                var job = await WaitForTerminalJobAsync(fixture.Store, jobId);

                Assert.Equal("Completed", job.Status);
                Assert.Equal(2, job.PagesCrawled);
                Assert.Equal(1, Volatile.Read(ref rootRequests));
                Assert.Equal(2, Volatile.Read(ref childRequests));

                var tree = await fixture.Store.GetTreeAsync(jobId, CancellationToken.None);
                var root = Assert.Single(tree, node => node.Url == site.RootUrl);
                Assert.Single(root.Children);
                Assert.Equal(1, root.DomainLinkRatio);
            }
            finally
            {
                await bus.StopAsync();
            }
        }
        finally
        {
            await site.App.DisposeAsync();
        }
    }

    private static async Task<Guid> SubmitJobAsync(HttpClient client, string url, int maxDepth)
    {
        var response = await client.PostAsJsonAsync("/api/jobs", new { url, maxDepth });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var responseBody = await response.Content.ReadAsStreamAsync();
        using var json = await System.Text.Json.JsonDocument.ParseAsync(responseBody);
        return json.RootElement.GetProperty("jobId").GetGuid();
    }

    private static ServiceProvider CreateWorkerProvider(ICrawlExecutionStore store, string rabbitMqConnection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICrawlExecutionStore>(store);
        services.AddHttpClient("crawler").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddMassTransit(registration =>
        {
            registration.AddConsumer<CrawlConsumer>();
            registration.UsingRabbitMq((context, configurator) =>
            {
                configurator.Host(new Uri(rabbitMqConnection));
                configurator.ConfigureCrawlEndpoint(context);
            });
        });
        return services.BuildServiceProvider();
    }

    private static async Task<(WebApplication App, string RootUrl)> StartTestSiteAsync(
        Func<IResult> rootResponse, Func<IResult>? childResponse = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/", () => rootResponse());
        app.MapGet("/child", () => childResponse?.Invoke() ?? Results.Content("<p>child page</p>", "text/html"));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("The test HTTP server did not publish its address.");
        var address = Assert.Single(addresses);
        return (app, new Uri(new Uri(address), "/").AbsoluteUri);
    }

    private static async Task<CrawlJob> WaitForTerminalJobAsync(CrawlStore store, Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var job = await store.GetJobAsync(jobId, timeout.Token)
                ?? throw new InvalidOperationException($"Crawl job {jobId} disappeared.");
            if (job.Status is "Completed" or "Failed")
                return job;
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private sealed class ApiFactory(string postgresConnectionString, string rabbitMqConnectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Crawler"] = postgresConnectionString,
                    ["RabbitMq:Uri"] = rabbitMqConnectionString
                }));
        }
    }

    private sealed class TerminalReadSignalingStore(ICrawlExecutionStore inner) : ICrawlExecutionStore
    {
        public TaskCompletionSource<CrawlJob> TerminalRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct) => SignalTerminalAsync(id, ct);

        private async Task<CrawlJob?> SignalTerminalAsync(Guid id, CancellationToken ct)
        {
            var job = await inner.GetJobAsync(id, ct);
            if (job?.Status is "Completed" or "Failed")
                TerminalRead.TrySetResult(job);
            return job;
        }

        public Task SetRunningAsync(Guid id, CancellationToken ct) => inner.SetRunningAsync(id, ct);
        public Task<bool> IsPageStoredAsync(Guid id, string url, CancellationToken ct) =>
            inner.IsPageStoredAsync(id, url, ct);
        public Task<IReadOnlyList<string>> GetOutgoingLinksAsync(Guid id, string parentUrl, CancellationToken ct) =>
            inner.GetOutgoingLinksAsync(id, parentUrl, ct);
        public Task SavePageAsync(CrawlPage page, IReadOnlyCollection<string> children, CancellationToken ct) =>
            inner.SavePageAsync(page, children, ct);
        public Task CompleteAsync(Guid id, CancellationToken ct) => inner.CompleteAsync(id, ct);
        public Task FailAsync(Guid id, string reason, CancellationToken ct) => inner.FailAsync(id, reason, ct);
    }
}

public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("crawler_tests")
        .WithUsername("crawler")
        .WithPassword("crawler_tests")
        .Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3-management-alpine")
        .WithUsername("crawler")
        .WithPassword("crawler_tests")
        .Build();

    public CrawlStore Store { get; private set; } = null!;
    public RabbitMqContainer RabbitMq => _rabbitMq;
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Crawler"] = PostgresConnectionString
            })
            .Build();
        Store = new CrawlStore(configuration);
        await Store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }
}
