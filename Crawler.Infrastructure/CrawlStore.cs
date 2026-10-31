using Npgsql;
using Microsoft.Extensions.Configuration;

namespace Crawler.Infrastructure;

public sealed class CrawlStore(IConfiguration configuration) : ICrawlJobStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Crawler")
        ?? "Host=localhost;Port=5432;Database=crawler;Username=crawler;Password=crawler";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS crawl_jobs (
                job_id UUID PRIMARY KEY, url TEXT NOT NULL, status TEXT NOT NULL,
                max_depth INT NOT NULL, pages_crawled INT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL, started_at TIMESTAMPTZ NULL,
                completed_at TIMESTAMPTZ NULL, failure_reason TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS crawl_pages (
                page_id BIGSERIAL PRIMARY KEY, job_id UUID NOT NULL REFERENCES crawl_jobs(job_id) ON DELETE CASCADE,
                url TEXT NOT NULL, depth INT NOT NULL, domain_link_ratio DOUBLE PRECISION NOT NULL,
                UNIQUE(job_id, url)
            );
            CREATE TABLE IF NOT EXISTS crawl_edges (
                job_id UUID NOT NULL REFERENCES crawl_jobs(job_id) ON DELETE CASCADE,
                parent_url TEXT NOT NULL, child_url TEXT NOT NULL,
                PRIMARY KEY(job_id, parent_url, child_url)
            );
            CREATE INDEX IF NOT EXISTS ix_crawl_jobs_created ON crawl_jobs(created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_crawl_pages_job ON crawl_pages(job_id);
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateJobAsync(Guid id, string url, int maxDepth, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO crawl_jobs(job_id,url,status,max_depth,created_at)
            VALUES (@id,@url,'Pending',@depth,@created)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("url", url);
        command.Parameters.AddWithValue("depth", maxDepth);
        command.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CrawlJob?> GetJobAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT job_id,url,status,max_depth,pages_crawled,created_at,started_at,completed_at,failure_reason
            FROM crawl_jobs WHERE job_id=@id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<HistoryResult> GetHistoryAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM crawl_jobs", connection);
        var total = (long)(await count.ExecuteScalarAsync(ct) ?? 0L);
        await using var command = new NpgsqlCommand("""
            SELECT job_id,url,status,max_depth,pages_crawled,created_at,started_at,completed_at,failure_reason
            FROM crawl_jobs ORDER BY created_at DESC LIMIT @size OFFSET @offset
            """, connection);
        command.Parameters.AddWithValue("size", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var items = new List<CrawlJob>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadJob(reader));
        return new HistoryResult(page, pageSize, total, items);
    }

    public async Task SetRunningAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE crawl_jobs SET status='Running',started_at=COALESCE(started_at,@now)
            WHERE job_id=@id AND status IN ('Pending','Running')
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsPageStoredAsync(Guid id, string url, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM crawl_pages WHERE job_id=@id AND url=@url)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("url", url);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<IReadOnlyList<string>> GetOutgoingLinksAsync(Guid id, string parentUrl, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT child_url FROM crawl_edges WHERE job_id=@id AND parent_url=@url", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("url", parentUrl);
        var children = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            children.Add(reader.GetString(0));
        return children;
    }

    public async Task SavePageAsync(CrawlPage page, IReadOnlyCollection<string> children, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO crawl_pages(job_id,url,depth,domain_link_ratio)
            VALUES (@id,@url,@depth,@ratio) ON CONFLICT(job_id,url) DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", page.JobId);
            command.Parameters.AddWithValue("url", page.Url);
            command.Parameters.AddWithValue("depth", page.Depth);
            command.Parameters.AddWithValue("ratio", page.DomainLinkRatio);
            await command.ExecuteNonQueryAsync(ct);
        }
        if (page.ParentUrl is not null)
            await InsertEdgeAsync(connection, transaction, page.JobId, page.ParentUrl, page.Url, ct);
        foreach (var child in children)
            await InsertEdgeAsync(connection, transaction, page.JobId, page.Url, child, ct);
        await using (var progress = new NpgsqlCommand(
            "UPDATE crawl_jobs SET pages_crawled=(SELECT COUNT(*) FROM crawl_pages WHERE job_id=@id) WHERE job_id=@id",
            connection, transaction))
        {
            progress.Parameters.AddWithValue("id", page.JobId);
            await progress.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct)
        => await UpdateTerminalAsync(id, "Completed", null, ct);

    public async Task FailAsync(Guid id, string reason, CancellationToken ct)
        => await UpdateTerminalAsync(id, "Failed", reason, ct);

    public async Task<IReadOnlyList<PageNode>> GetTreeAsync(Guid jobId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        var pages = new Dictionary<string, (double Ratio, List<string> Children)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT p.url,p.domain_link_ratio,e.child_url
            FROM crawl_pages p LEFT JOIN crawl_edges e ON e.job_id=p.job_id AND e.parent_url=p.url
            WHERE p.job_id=@id ORDER BY p.url,e.child_url
            """, connection))
        {
            command.Parameters.AddWithValue("id", jobId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var url = reader.GetString(0);
                var entry = pages.GetValueOrDefault(url, (reader.GetDouble(1), new List<string>()));
                if (!reader.IsDBNull(2))
                    entry.Item2.Add(reader.GetString(2));
                pages[url] = entry;
            }
        }
        if (pages.Count == 0)
            return [];
        var children = pages.Values.SelectMany(p => p.Children).ToHashSet(StringComparer.Ordinal);
        var roots = pages.Keys.Where(url => !children.Contains(url)).ToArray();
        if (roots.Length == 0)
            roots = [pages.Keys.First()];
        return roots.Select(url => Build(url, new HashSet<string>(StringComparer.Ordinal))).ToArray();

        PageNode Build(string url, HashSet<string> path)
        {
            if (!pages.TryGetValue(url, out var page) || !path.Add(url))
                return new PageNode(url, 0, []);
            var nodes = page.Children.Where(pages.ContainsKey).Select(child => Build(child, new(path))).ToArray();
            return new PageNode(url, page.Ratio, nodes);
        }
    }

    private async Task UpdateTerminalAsync(Guid id, string status, string? reason, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE crawl_jobs SET status=@status,completed_at=@now,failure_reason=@reason WHERE job_id=@id
            """, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertEdgeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, string parent, string child, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO crawl_edges(job_id,parent_url,child_url) VALUES (@id,@parent,@child)
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("parent", parent);
        command.Parameters.AddWithValue("child", child);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static CrawlJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));
}
