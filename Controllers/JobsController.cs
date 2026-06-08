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
    /// <summary>Public job board — active, unexpired listings.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<JobListingResponse>> GetActive(CancellationToken ct)
        => await jobs.GetActiveListingsAsync(ct);

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

    /// <summary>An employer's own postings.</summary>
    [HttpGet("company/{companyId:guid}")]
    public async Task<IReadOnlyList<JobListingResponse>> GetByCompany(Guid companyId, CancellationToken ct)
        => await jobs.GetByCompanyAsync(companyId, ct);

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
}
