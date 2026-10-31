namespace Crawler.Infrastructure;

public interface ICrawlJobStore
{
    Task CreateJobAsync(Guid id, string url, int maxDepth, CancellationToken ct);
    Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct);
    Task<HistoryResult> GetHistoryAsync(int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<PageNode>> GetTreeAsync(Guid jobId, CancellationToken ct);
    Task<bool> CancelJobAsync(Guid id, CancellationToken ct);
}
