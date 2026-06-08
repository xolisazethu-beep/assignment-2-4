using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Applicant")] // every action here is applicant-only
public class ApplicationsController(IApplicationService applications) : ControllerBase
{
    /// <summary>Apply to a listing. The applicant is taken from the token, not the body.</summary>
    [HttpPost("api/jobs/{jobListingId:guid}/applications")]
    public async Task<IActionResult> Apply(Guid jobListingId, ApplyRequest request, CancellationToken ct)
    {
        await applications.ApplyAsync(User.GetUserId(), jobListingId, request.CoverNote, ct);
        return StatusCode(StatusCodes.Status201Created);
    }

    /// <summary>The signed-in applicant's own application history.</summary>
    [HttpGet("api/applications/me")]
    public async Task<IReadOnlyList<MyApplicationResponse>> Mine(CancellationToken ct)
        => await applications.GetMineAsync(User.GetUserId(), ct);
}
