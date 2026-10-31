using Crawler.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace CrawlerJob.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController(CrawlStore store, IPublishEndpoint publisher) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateJobRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return BadRequest(new { error = "A valid HTTP or HTTPS URL is required." });

        var maxDepth = request.MaxDepth ?? 2;
        if (maxDepth is < 0 or > 10)
            return BadRequest(new { error = "maxDepth must be between 0 and 10." });

        var jobId = Guid.NewGuid();
        await store.CreateJobAsync(jobId, UrlNormalizer.Normalize(uri), maxDepth, cancellationToken);
        await publisher.Publish(new CrawlMessage(jobId), cancellationToken);
        return Accepted(new { jobId });
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(jobId, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("{jobId:guid}/tree")]
    public async Task<IActionResult> Tree(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.GetJobAsync(jobId, cancellationToken) is null)
            return NotFound();
        return Ok(await store.GetTreeAsync(jobId, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            return BadRequest(new { error = "page must be positive and pageSize must be between 1 and 100." });
        return Ok(await store.GetHistoryAsync(page, pageSize, cancellationToken));
    }
}
