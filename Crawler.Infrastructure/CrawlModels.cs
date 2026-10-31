namespace Crawler.Infrastructure;

public sealed record CreateJobRequest(string Url, int? MaxDepth);
public sealed record CreateJobResponse(Guid JobId);
public sealed record CrawlMessage(Guid JobId);
public sealed record CrawlJob(Guid JobId, string Url, string Status, int MaxDepth, int PagesCrawled,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? FailureReason);
public sealed record CrawlPage(Guid JobId, string Url, string? ParentUrl, int Depth, double DomainLinkRatio);
public sealed record PageNode(string Url, double DomainLinkRatio, IReadOnlyList<PageNode> Children);
public sealed record HistoryResult(int Page, int PageSize, long Total, IReadOnlyList<CrawlJob> Items);

public static class UrlNormalizer
{
    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = "" };
        if ((builder.Scheme == "http" && builder.Port == 80) ||
            (builder.Scheme == "https" && builder.Port == 443))
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    public static double DomainLinkRatio(IEnumerable<Uri> links, string startingHost)
    {
        var eligible = links.Where(link => link.Scheme == Uri.UriSchemeHttp ||
            link.Scheme == Uri.UriSchemeHttps).ToArray();
        return eligible.Length == 0 ? 0 : (double)eligible.Count(link =>
            string.Equals(link.Host, startingHost, StringComparison.OrdinalIgnoreCase)) / eligible.Length;
    }
}
