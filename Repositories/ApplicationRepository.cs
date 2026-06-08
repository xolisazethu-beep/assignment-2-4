using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Repositories;

public class ApplicationRepository(CareerHubDbContext db) : IApplicationRepository
{
    // ── PART 6: COMPILED QUERY — HasAppliedAsync ─────────────────────────────
    // HOT PATH. Runs on EVERY application submission (the duplicate-application
    // guard before the INSERT) and again whenever a listing page is rendered for
    // a signed-in seeker to show the "Already applied" badge. It is a tiny,
    // unchanging, high-frequency point lookup — the ideal compiled-query
    // candidate (see README "Hot path justification"). Compiling removes the
    // expression-tree build from a query that fires thousands of times an hour.
    // Hits ix_applications_applicantid_joblistingid.
    private static readonly Func<CareerHubDbContext, Guid, Guid, Task<bool>>
        HasAppliedQuery = EF.CompileAsyncQuery(
            (CareerHubDbContext ctx, Guid applicantId, Guid jobListingId) =>
                ctx.Applications.Any(a => a.ApplicantId == applicantId
                                          && a.JobListingId == jobListingId));

    public Task<bool> HasAppliedAsync(Guid applicantId, Guid jobListingId, CancellationToken ct = default) =>
        HasAppliedQuery(db, applicantId, jobListingId);

    public async Task<IReadOnlyList<Application>> GetForListingAsync(Guid jobListingId, CancellationToken ct = default)
    {
        // Hits ix_applications_joblistingid_submittedat — already ordered, no Sort.
        return await db.Applications
            .AsNoTracking()
            .Where(a => a.JobListingId == jobListingId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MyApplicationResponse>> GetByApplicantAsync(Guid applicantId, CancellationToken ct = default)
    {
        // Hits ix_applications_applicantid_joblistingid (applicant leads). Flat
        // projection across the listing + company so EF emits one SELECT and we
        // never serialise entity graphs — same pattern as the read-side job queries.
        return await db.Applications
            .AsNoTracking()
            .Where(a => a.ApplicantId == applicantId)
            .OrderByDescending(a => a.SubmittedAt)
            .Select(a => new MyApplicationResponse(
                a.JobListingId,
                a.JobListing.Title,
                a.JobListing.Company.Name,
                a.Status.ToString(),
                a.SubmittedAt))
            .ToListAsync(ct);
    }

    public Task<Application?> GetTrackedAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default) =>
        db.Applications.FirstOrDefaultAsync(
            a => a.JobListingId == jobListingId && a.ApplicantId == applicantId, ct);

    public async Task AddAsync(Application application, CancellationToken ct = default) =>
        await db.Applications.AddAsync(application, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
