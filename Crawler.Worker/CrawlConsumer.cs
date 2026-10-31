using System.Net;
using Crawler.Infrastructure;
using MassTransit;

namespace Crawler.Worker;

public sealed class CrawlConsumer : IConsumer<CrawlMessage>
{
    private const int MaxPages = 200;
    private readonly ILogger<CrawlConsumer> _logger;
    private readonly CrawlStore _store;
    private readonly IHttpClientFactory _httpClientFactory;

    public CrawlConsumer(CrawlStore store, IHttpClientFactory httpClientFactory, ILogger<CrawlConsumer> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CrawlMessage> context)
    {
        var jobId = context.Message.JobId;
        try
        {
            _logger.LogInformation("Received crawl job {JobId}", jobId);
            var job = await _store.GetJobAsync(jobId, context.CancellationToken);
            if (job is null || job.Status is "Completed" or "Failed")
                return;
            await _store.SetRunningAsync(jobId, context.CancellationToken);
            await CrawlAsync(job, context.CancellationToken);
            await _store.CompleteAsync(jobId, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crawl job {JobId} failed on attempt {RetryAttempt}",
                jobId, context.GetRetryAttempt());
            if (context.GetRetryAttempt() >= 2)
            {
                await _store.FailAsync(jobId, ex.Message, context.CancellationToken);
            }
            throw;
        }
    }

    private async Task CrawlAsync(CrawlJob job, CancellationToken ct)
    {
        var start = new Uri(job.Url);
        var queue = new Queue<(Uri Url, string? Parent, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { UrlNormalizer.Normalize(start) };
        queue.Enqueue((start, null, 0));
        while (queue.TryDequeue(out var current) && !ct.IsCancellationRequested)
        {
            var currentUrl = UrlNormalizer.Normalize(current.Url);
            if (await _store.IsPageStoredAsync(job.JobId, currentUrl, ct))
            {
                if (current.Depth < job.MaxDepth)
                {
                    var storedLinks = await _store.GetOutgoingLinksAsync(job.JobId, currentUrl, ct);
                    foreach (var link in storedLinks)
                    {
                        if (Uri.TryCreate(link, UriKind.Absolute, out var child) &&
                            string.Equals(child.Host, start.Host, StringComparison.OrdinalIgnoreCase) &&
                            seen.Add(link) && seen.Count <= MaxPages)
                            queue.Enqueue((child, currentUrl, current.Depth + 1));
                    }
                }
                continue;
            }
            using var response = await _httpClientFactory.CreateClient("crawler").GetAsync(current.Url, ct);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500)
                    response.EnsureSuccessStatusCode();
                _logger.LogWarning("Skipping HTTP {Status} for {Url} in job {JobId}",
                    (int)response.StatusCode, current.Url, job.JobId);
                continue;
            }
            if (!response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) ?? true)
                continue;
            var absolute = response.RequestMessage?.RequestUri ?? current.Url;
            if (!string.Equals(absolute.Host, start.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            var html = await response.Content.ReadAsStringAsync(ct);
            var discovered = PageParser.ExtractLinks(html, absolute);
            var normalizedChildren = discovered.Select(UrlNormalizer.Normalize).ToArray();
            var ratio = UrlNormalizer.DomainLinkRatio(discovered, start.Host);
            await _store.SavePageAsync(new CrawlPage(job.JobId, UrlNormalizer.Normalize(absolute),
                current.Parent, current.Depth, ratio), normalizedChildren, ct);
            if (current.Depth >= job.MaxDepth)
                continue;
            foreach (var child in discovered)
            {
                if (!string.Equals(child.Host, start.Host, StringComparison.OrdinalIgnoreCase))
                    continue;
                var normalized = UrlNormalizer.Normalize(child);
                if (seen.Add(normalized) && seen.Count <= MaxPages)
                    queue.Enqueue((child, UrlNormalizer.Normalize(absolute), current.Depth + 1));
            }
        }
        if (seen.Count > MaxPages)
            _logger.LogWarning("Page limit {MaxPages} reached for job {JobId}", MaxPages, job.JobId);
    }

}
