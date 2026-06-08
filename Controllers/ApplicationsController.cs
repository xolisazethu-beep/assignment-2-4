using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

// Auth is per-action here: applying and "my applications" are applicant-only,
// but the Part 5B status transition is employer-only — so a single class-level
// [Authorize(Roles=...)] (which would AND the roles) cannot express both.
[ApiController]
[Authorize]
public class ApplicationsController(IApplicationService applications) : ControllerBase
{
    /// <summary>Apply to a listing. The applicant is taken from the token, not the body.</summary>
    [HttpPost("api/jobs/{jobListingId:guid}/applications")]
    [Authorize(Roles = "Applicant")]
    public async Task<IActionResult> Apply(Guid jobListingId, ApplyRequest request, CancellationToken ct)
    {
        await applications.ApplyAsync(User.GetUserId(), jobListingId, request.CoverNote, ct);
        return StatusCode(StatusCodes.Status201Created);
    }

    /// <summary>The signed-in applicant's own application history.</summary>
    [HttpGet("api/applications/me")]
    [Authorize(Roles = "Applicant")]
    public async Task<IReadOnlyList<MyApplicationResponse>> Mine(CancellationToken ct)
        => await applications.GetMineAsync(User.GetUserId(), ct);

    /// <summary>
    /// PART 5B: transition an application's status, enforcing the legal-transition
    /// state machine (illegal moves → 400). Employer-only. Applications are
    /// identified by their composite key (jobListingId, applicantId) since the
    /// entity has no surrogate id. One line → service.
    /// </summary>
    [HttpPatch("api/applications/{jobListingId:guid}/{applicantId:guid}/status")]
    [Authorize(Roles = "Employer")]
    public async Task<IActionResult> UpdateStatus(
        Guid jobListingId, Guid applicantId, UpdateApplicationStatusRequest request, CancellationToken ct)
    {
        await applications.UpdateStatusAsync(jobListingId, applicantId, request.Status, ct);
        return NoContent();
    }
}
