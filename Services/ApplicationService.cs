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
        var listing = await jobs.GetEntityByIdAsync(jobListingId, ct);
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

    public async Task<ApplicationResponse?> GetAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default)
    {
        var app = await applications.GetTrackedAsync(jobListingId, applicantId, ct);
        return app is null
            ? null
            : new ApplicationResponse(app.JobListingId, app.ApplicantId, app.Status.ToString(), app.SubmittedAt, app.CoverNote);
    }

    // ── PART 5B: LEGAL STATUS-TRANSITION STATE MACHINE ───────────────────────
    // Rejected and Offered are TERMINAL (empty sets → no move out, so they can
    // never go back to Submitted). Submitted → UnderReview/Rejected. UnderReview →
    // Shortlisted/Rejected/Offered. Shortlisted → Offered/Rejected.
    private static readonly Dictionary<ApplicationStatus, HashSet<ApplicationStatus>> LegalTransitions = new()
    {
        [ApplicationStatus.Submitted]   = [ApplicationStatus.UnderReview, ApplicationStatus.Rejected],
        [ApplicationStatus.UnderReview] = [ApplicationStatus.Shortlisted, ApplicationStatus.Rejected, ApplicationStatus.Offered],
        [ApplicationStatus.Shortlisted] = [ApplicationStatus.Offered, ApplicationStatus.Rejected],
        [ApplicationStatus.Rejected]    = [],
        [ApplicationStatus.Offered]     = [],
    };

    public async Task UpdateStatusAsync(Guid jobListingId, Guid applicantId, ApplicationStatus newStatus, CancellationToken ct = default)
    {
        var app = await applications.GetTrackedAsync(jobListingId, applicantId, ct)
            ?? throw new NotFoundException("That application does not exist.");

        if (!LegalTransitions.TryGetValue(app.Status, out var allowed) || !allowed.Contains(newStatus))
            throw new ArgumentException(
                $"Illegal status transition from {app.Status} to {newStatus}. " +
                (allowed is { Count: > 0 }
                    ? $"From {app.Status} you may only move to: {string.Join(", ", allowed)}."
                    : $"{app.Status} is a terminal state and cannot be changed."));

        app.Status = newStatus;
        await applications.SaveChangesAsync(ct);
    }
}
