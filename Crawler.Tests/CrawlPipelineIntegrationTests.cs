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

public sealed class CrawlPipelineIntegrationTests(IntegrationFixture fixture)
    : IClassFixture<IntegrationFixture>
{
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

    private static ServiceProvider CreateWorkerProvider(CrawlStore store, string rabbitMqConnection)
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
                configurator.ConfigureCrawlEndpoint(context, $"crawl-jobs-{Guid.NewGuid():N}");
            });
        });
        return services.BuildServiceProvider();
    }

    private static async Task<(WebApplication App, string RootUrl)> StartTestSiteAsync(
        Func<IResult> rootResponse)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/", () => rootResponse());
        app.MapGet("/child", () => Results.Content("<p>child page</p>", "text/html"));
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
