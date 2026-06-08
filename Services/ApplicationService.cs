using CareerHub.Api.DTOs;
using CareerHub.Api.Exceptions;
using CareerHub.Api.Models;
using CareerHub.Api.Repositories;

namespace CareerHub.Api.Services;

/// <summary>
/// The apply business rules: the listing must exist and be open, and an applicant
/// may apply at most once. The "once" rule is enforced here with a pre-check AND
/// by the composite primary key in the database — the pre-check gives a clean 409,
/// the PK is the race-safe backstop if two requests slip through together.
/// </summary>
public class ApplicationService(
    IApplicationRepository applications,
    IJobListingRepository jobs) : IApplicationService
{
    public async Task ApplyAsync(Guid applicantId, Guid jobListingId, string? coverNote, CancellationToken ct = default)
    {
        var listing = await jobs.GetByIdAsync(jobListingId, ct);
        if (listing is null)
            throw new NotFoundException("That job listing does not exist.");
        if (listing.Status != ListingStatus.Active || listing.ExpiresAt <= DateTime.UtcNow)
            throw new ArgumentException("That listing is no longer accepting applications.");

        if (await applications.HasAppliedAsync(applicantId, jobListingId, ct))
            throw new DuplicateApplicationException("You have already applied to this listing.");

        await applications.AddAsync(new Application
        {
            JobListingId = jobListingId,
            ApplicantId = applicantId,
            Status = ApplicationStatus.Submitted,
            SubmittedAt = DateTime.UtcNow,        // server-set; satisfies ck_applications_submitted_not_future
            CoverNote = coverNote ?? string.Empty
        }, ct);
        await applications.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<MyApplicationResponse>> GetMineAsync(Guid applicantId, CancellationToken ct = default) =>
        applications.GetByApplicantAsync(applicantId, ct);
}
