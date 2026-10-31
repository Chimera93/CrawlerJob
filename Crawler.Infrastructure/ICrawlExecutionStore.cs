namespace Crawler.Infrastructure;

public interface ICrawlExecutionStore
{
    Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct);
    Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken ct);
    Task SetRunningAsync(Guid id, CancellationToken ct);
    Task<bool> IsPageStoredAsync(Guid id, string url, CancellationToken ct);
    Task<IReadOnlyList<string>> GetOutgoingLinksAsync(Guid id, string parentUrl, CancellationToken ct);
    Task SavePageAsync(CrawlPage page, IReadOnlyCollection<string> children, CancellationToken ct);
    Task CompleteAsync(Guid id, CancellationToken ct);
    Task FailAsync(Guid id, string reason, CancellationToken ct);
}
