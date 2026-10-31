using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Crawler.Infrastructure;
using Crawler.Worker;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crawler.Tests;

public sealed class CrawlConsumerTests
{
    [Fact]
    public async Task Crawl_respects_depth_deduplicates_urls_and_records_link_ratio()
    {
        var jobId = Guid.NewGuid();
        var store = new RecordingCrawlStore(new CrawlJob(jobId, "https://example.com/", "Pending", 1, 0,
            DateTimeOffset.UtcNow, null, null, null));
        var responses = new Dictionary<string, string>
        {
            ["/"] = """
                <a href="/first#one">first</a>
                <a href="/first#two">duplicate</a>
                <a href="/second">second</a>
                <a href="https://outside.test/page">outside</a>
                <a href="mailto:test@example.com">email</a>
                """,
            ["/first"] = "<a href='/too-deep'>deep</a>",
            ["/second"] = "<p>terminal page</p>"
        };
        var requested = new ConcurrentBag<string>();

        await RunConsumerAsync(store, url =>
        {
            requested.Add(url.AbsolutePath);
            return HtmlResponse(responses[url.AbsolutePath], url);
        });

        Assert.Equal(new[] { "/", "/first", "/second" }, requested.OrderBy(path => path));
        Assert.Equal(3, store.Pages.Count);
        var root = Assert.Single(store.Pages, page => page.Url == "https://example.com/");
        Assert.Equal(0, root.Depth);
        Assert.Null(root.ParentUrl);
        Assert.Equal(2d / 3d, root.DomainLinkRatio, 5);
        Assert.Contains("https://example.com/too-deep", store.SavedChildren["https://example.com/first"]);
        Assert.Equal("Completed", store.Status);
    }

    [Fact]
    public async Task Crawl_attempts_discovered_unreachable_urls_without_saving_them()
    {
        var jobId = Guid.NewGuid();
        var store = new RecordingCrawlStore(new CrawlJob(jobId, "https://example.com/", "Pending", 2, 0,
            DateTimeOffset.UtcNow, null, null, null));
        var requested = new ConcurrentBag<string>();

        await RunConsumerAsync(store, url =>
        {
            requested.Add(url.AbsolutePath);
            return url.AbsolutePath == "/"
                ? HtmlResponse("<a href='/unavailable'>unavailable</a>", url)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, url) };
        });

        Assert.Contains("/unavailable", requested);
        Assert.DoesNotContain(store.Pages, page => page.Url.EndsWith("/unavailable", StringComparison.Ordinal));
        Assert.Equal("Completed", store.Status);
    }

    [Fact]
    public async Task Crawl_never_registers_more_than_200_unique_urls()
    {
        var jobId = Guid.NewGuid();
        var store = new RecordingCrawlStore(new CrawlJob(jobId, "https://example.com/", "Pending", 1, 0,
            DateTimeOffset.UtcNow, null, null, null));
        var rootHtml = string.Join("", Enumerable.Range(1, 250)
            .Select(index => $"<a href='/page-{index}'>page</a>"));
        var requests = 0;

        await RunConsumerAsync(store, url =>
        {
            Interlocked.Increment(ref requests);
            return HtmlResponse(url.AbsolutePath == "/" ? rootHtml : "", url);
        });

        Assert.Equal(200, requests);
        Assert.Equal(200, store.Pages.Count);
        Assert.Equal(200, store.KnownUrls.Count);
    }

    private static async Task RunConsumerAsync(RecordingCrawlStore store,
        Func<Uri, HttpResponseMessage> respond)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICrawlExecutionStore>(store);
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(respond));
        services.AddMassTransitTestHarness(config => config.AddConsumer<CrawlConsumer>());
        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new CrawlMessage(store.Job.JobId));
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static HttpResponseMessage HtmlResponse(string html, Uri url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, url)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        return response;
    }

    private sealed class TestHttpClientFactory(Func<Uri, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new DelegateHttpMessageHandler(respond));
    }

    private sealed class DelegateHttpMessageHandler(Func<Uri, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri
                ?? throw new InvalidOperationException("The crawler sent a request without a URI.")));
    }

    private sealed class RecordingCrawlStore(CrawlJob job) : ICrawlExecutionStore
    {
        private readonly HashSet<string> _knownUrls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> _outgoingLinks = new(StringComparer.Ordinal);

        public CrawlJob Job { get; } = job;
        public List<CrawlPage> Pages { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> SavedChildren { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> KnownUrls => _knownUrls;
        public string Status { get; private set; } = "Pending";

        public Task CreateJobAsync(Guid id, string url, int maxDepth, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<CrawlJob?>(id == Job.JobId ? Job with { Status = Status } : null);

        public Task<HistoryResult> GetHistoryAsync(int page, int pageSize, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<PageNode>> GetTreeAsync(Guid jobId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetRunningAsync(Guid id, CancellationToken ct)
        {
            Status = "Running";
            return Task.CompletedTask;
        }

        public Task<bool> IsPageStoredAsync(Guid id, string url, CancellationToken ct) =>
            Task.FromResult(_knownUrls.Contains(url));

        public Task<IReadOnlyList<string>> GetOutgoingLinksAsync(Guid id, string parentUrl, CancellationToken ct) =>
            Task.FromResult(_outgoingLinks.GetValueOrDefault(parentUrl, Array.Empty<string>()));

        public Task SavePageAsync(CrawlPage page, IReadOnlyCollection<string> children, CancellationToken ct)
        {
            Pages.Add(page);
            _knownUrls.Add(page.Url);
            SavedChildren[page.Url] = children.ToArray();
            _outgoingLinks[page.Url] = children.ToArray();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid id, CancellationToken ct)
        {
            Status = "Completed";
            Completed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid id, string reason, CancellationToken ct)
        {
            Status = "Failed";
            Completed.TrySetException(new InvalidOperationException(reason));
            return Task.CompletedTask;
        }
    }
}
