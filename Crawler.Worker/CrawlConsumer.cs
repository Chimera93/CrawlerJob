using System.Net;
using Crawler.Infrastructure;
using MassTransit;

namespace Crawler.Worker;

public sealed class CrawlConsumer : IConsumer<CrawlMessage>
{
    private const int MaxPages = 200;
    private readonly ILogger<CrawlConsumer> _logger;
    private readonly ICrawlExecutionStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICrawlUrlPolicy _urlPolicy;

    public CrawlConsumer(ICrawlExecutionStore store, IHttpClientFactory httpClientFactory,
        ICrawlUrlPolicy urlPolicy, ILogger<CrawlConsumer> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _urlPolicy = urlPolicy;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CrawlMessage> context)
    {
        var jobId = context.Message.JobId;
        try
        {
            _logger.LogInformation("Received crawl job {JobId}", jobId);
            var job = await _store.GetJobAsync(jobId, context.CancellationToken);
            if (job is null || job.Status is "Completed" or "Failed" or "Canceled")
                return;
            await _store.SetRunningAsync(jobId, context.CancellationToken);
            if (!await CrawlAsync(job, context.CancellationToken))
                await _store.CompleteAsync(jobId, context.CancellationToken);
        }
        catch (Exception ex)
        {
            if (await _store.IsCancellationRequestedAsync(jobId, context.CancellationToken))
            {
                _logger.LogInformation("Stopping canceled crawl job {JobId}", jobId);
                return;
            }
            _logger.LogError(ex, "Crawl job {JobId} failed on attempt {RetryAttempt}",
                jobId, context.GetRetryAttempt());
            if (context.GetRetryAttempt() >= 2)
            {
                await _store.FailAsync(jobId, ex.Message, context.CancellationToken);
            }
            throw;
        }
    }

    private async Task<bool> CrawlAsync(CrawlJob job, CancellationToken ct)
    {
        var start = new Uri(job.Url);
        var queue = new Queue<(Uri Url, string? Parent, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { UrlNormalizer.Normalize(start) };
        var limitReached = false;
        queue.Enqueue((start, null, 0));
        while (queue.TryDequeue(out var current) && !ct.IsCancellationRequested)
        {
            if (await _store.IsCancellationRequestedAsync(job.JobId, ct))
                return true;
            var currentUrl = UrlNormalizer.Normalize(current.Url);
            if (await _store.IsPageStoredAsync(job.JobId, currentUrl, ct))
            {
                if (current.Depth < job.MaxDepth)
                {
                    var storedLinks = await _store.GetOutgoingLinksAsync(job.JobId, currentUrl, ct);
                    foreach (var link in storedLinks)
                    {
                        if (Uri.TryCreate(link, UriKind.Absolute, out var child) &&
                            string.Equals(child.Host, start.Host, StringComparison.OrdinalIgnoreCase))
                            TryQueue(child, currentUrl, current.Depth + 1);
                    }
                }
                continue;
            }
            using var response = await GetResponseAsync(current.Url, start, job.JobId, ct);
            if (response is null)
                continue;
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
                TryQueue(child, UrlNormalizer.Normalize(absolute), current.Depth + 1);
            }
        }
        if (limitReached)
            _logger.LogWarning("Page limit {MaxPages} reached for job {JobId}", MaxPages, job.JobId);

        return await _store.IsCancellationRequestedAsync(job.JobId, ct);

        void TryQueue(Uri url, string parent, int depth)
        {
            var normalized = UrlNormalizer.Normalize(url);
            if (seen.Contains(normalized))
                return;
            if (seen.Count >= MaxPages)
            {
                limitReached = true;
                return;
            }
            seen.Add(normalized);
            queue.Enqueue((url, parent, depth));
        }
    }

    private async Task<HttpResponseMessage?> GetResponseAsync(Uri initialUrl, Uri start, Guid jobId,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("crawler");
        var current = initialUrl;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            try
            {
                await _urlPolicy.EnsurePublicHttpUrlAsync(current, ct);
            }
            catch (UnsafeCrawlUrlException exception)
            {
                _logger.LogWarning("Blocked unsafe crawl URL {Url} for job {JobId}: {Reason}",
                    current, jobId, exception.Message);
                return null;
            }

            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null || redirectCount == 5)
                return response;

            var destination = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
            response.Dispose();
            if (!string.Equals(destination.Host, start.Host, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Blocked cross-host redirect to {Url} for job {JobId}", destination, jobId);
                return null;
            }
            current = destination;
        }

        return null;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

}
