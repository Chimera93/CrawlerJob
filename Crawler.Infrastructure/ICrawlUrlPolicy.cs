namespace Crawler.Infrastructure;

public interface ICrawlUrlPolicy
{
    Task EnsurePublicHttpUrlAsync(Uri uri, CancellationToken ct);
}
