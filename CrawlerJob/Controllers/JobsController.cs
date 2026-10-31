using Crawler.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace CrawlerJob.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController(ICrawlJobStore store, IPublishEndpoint publisher,
    ICrawlUrlPolicy urlPolicy) : ControllerBase
{
    /// <summary>Creates a crawl job and queues it for asynchronous processing.</summary>
    /// <response code="202">The job was accepted and queued.</response>
    /// <response code="400">The URL or maxDepth is invalid, or the URL is not publicly routable.</response>
    [HttpPost]
    [ProducesResponseType(typeof(CreateJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateJobRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return BadRequest(new { error = "A valid HTTP or HTTPS URL is required." });

        var maxDepth = request.MaxDepth ?? 2;
        if (maxDepth is < 0 or > 10)
            return BadRequest(new { error = "maxDepth must be between 0 and 10." });

        try
        {
            await urlPolicy.EnsurePublicHttpUrlAsync(uri, cancellationToken);
        }
        catch (UnsafeCrawlUrlException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var jobId = Guid.NewGuid();
        await store.CreateJobAsync(jobId, UrlNormalizer.Normalize(uri), maxDepth, cancellationToken);
        await publisher.Publish(new CrawlMessage(jobId), cancellationToken);
        return Accepted(new CreateJobResponse(jobId));
    }

    /// <summary>Requests cooperative cancellation of a pending or running crawl.</summary>
    /// <response code="204">The job was canceled.</response>
    /// <response code="404">No job exists with the supplied ID.</response>
    /// <response code="409">The job is already terminal and cannot be canceled.</response>
    [HttpPost("{jobId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.GetJobAsync(jobId, cancellationToken) is null)
            return NotFound();
        return await store.CancelJobAsync(jobId, cancellationToken) ? NoContent() : Conflict();
    }

    /// <summary>Gets a job's status, timestamps, progress, and failure reason.</summary>
    /// <response code="200">The requested crawl job.</response>
    /// <response code="404">No job exists with the supplied ID.</response>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(CrawlJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(jobId, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Gets the discovered page tree and each page's domain link ratio.</summary>
    /// <response code="200">The page tree for the requested job.</response>
    /// <response code="404">No job exists with the supplied ID.</response>
    [HttpGet("{jobId:guid}/tree")]
    [ProducesResponseType(typeof(IReadOnlyList<PageNode>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Tree(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.GetJobAsync(jobId, cancellationToken) is null)
            return NotFound();
        return Ok(await store.GetTreeAsync(jobId, cancellationToken));
    }

    /// <summary>Lists crawl jobs, most recent first.</summary>
    /// <response code="200">A page of crawl history.</response>
    /// <response code="400">The requested page or pageSize is outside the supported range.</response>
    [HttpGet]
    [ProducesResponseType(typeof(HistoryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            return BadRequest(new { error = "page must be positive and pageSize must be between 1 and 100." });
        return Ok(await store.GetHistoryAsync(page, pageSize, cancellationToken));
    }
}
