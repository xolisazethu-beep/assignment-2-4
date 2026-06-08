using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Models;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController(IJobService jobs) : ControllerBase
{
    /// <summary>
    /// Public job board — active, unexpired listings, PAGINATED, FILTERED and
    /// SORTED (Parts 3 + 4). All query params are optional: filter by
    /// location/employmentType/salaryMin/salaryMax/companyId/q/postedSince/remoteOnly,
    /// sort by sort+dir, page with page+pageSize. pageSize is clamped to ≤ 100.
    /// Writes the total match count to the <c>X-Total-Count</c> header.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetActive([FromQuery] JobListingFilterQuery query, CancellationToken ct)
    {
        query.PageSize = Math.Clamp(query.PageSize, 1, 100);
        return Paged(await jobs.GetActiveListingsPagedAsync(query, ct));
    }

    /// <summary>
    /// Filter the active job board by any combination of job name (title),
    /// location and type. e.g. /api/jobs/filter?title=engineer&amp;location=cape%20town&amp;type=FullTime.
    /// All parameters are optional; omitting them all returns the whole active board.
    /// </summary>
    [HttpGet("filter")]
    public async Task<IReadOnlyList<JobListingResponse>> Filter(
        [FromQuery] string? title, [FromQuery] string? location,
        [FromQuery] JobType? type, CancellationToken ct)
        => await jobs.BrowseAsync(title, location, type, ct);

    /// <summary>Full detail of a single listing by id. 404 if it does not exist.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JobListingDetailResponse>> GetById(Guid id, CancellationToken ct)
        => await jobs.GetDetailByIdAsync(id, ct) is { } listing
            ? Ok(listing)
            : NotFound();

    /// <summary>An employer's own postings (any status), PAGINATED + FILTERED + SORTED.</summary>
    [HttpGet("company/{companyId:guid}")]
    public async Task<IActionResult> GetByCompany(Guid companyId, [FromQuery] JobListingFilterQuery query, CancellationToken ct)
    {
        query.PageSize = Math.Clamp(query.PageSize, 1, 100);
        return Paged(await jobs.GetByCompanyPagedAsync(companyId, query, ct));
    }

    // ── PART 5: full-text search. One line — service -> repository. ───────────
    [HttpGet("search")]
    public async Task<IReadOnlyList<JobListingResponse>> Search([FromQuery] string q, CancellationToken ct)
        => await jobs.SearchAsync(q, ct);

    // ── PART 8: application statistics (raw SQL with RANK()). ─────────────────
    // Employer-only, and scoped to the caller's OWN company: the companyId comes
    // from the token, not a query param, so one employer cannot read another's stats.
    [HttpGet("stats")]
    [Authorize(Roles = "Employer")]
    public async Task<IReadOnlyList<JobListingStatsResponse>> Stats(CancellationToken ct)
        => await jobs.GetApplicationStatsAsync(User.GetCompanyId(), ct);

    /// <summary>Publish a listing. Employer-only; always posted under the employer's own company.</summary>
    [HttpPost]
    [Authorize(Roles = "Employer")]
    public async Task<IActionResult> Create(CreateJobListingRequest request, CancellationToken ct)
    {
        var companyId = User.GetCompanyId();
        var id = await jobs.CreateAsync(request, companyId, ct);
        return CreatedAtAction(nameof(GetByCompany), new { companyId }, new { id });
    }

    /// <summary>PART 5A: partial update of a listing. Employer-only, same as create. One line → service.</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Roles = "Employer")]
    public async Task<IActionResult> Patch(Guid id, UpdateJobListingRequest request, CancellationToken ct)
    {
        await jobs.PatchAsync(id, request, ct);
        return NoContent();
    }

    // ── PART 3: paged envelope helper ────────────────────────────────────────
    // Writes X-Total-Count on every paginated response and attaches the
    // HATEOAS-lite navigation links built from the current request.
    private OkObjectResult Paged(PagedResponse<JobListingResponse> page)
    {
        Response.Headers["X-Total-Count"] = page.TotalCount.ToString();
        return Ok(page with { Links = PaginationLinks.Build(Request, page) });
    }
}
