using CareerHub.Api.DTOs;
using CareerHub.Api.Models;

namespace CareerHub.Api.Repositories;

public interface IApplicationRepository
{
    /// <summary>True if this applicant already applied to this listing. HOT PATH.</summary>
    Task<bool> HasAppliedAsync(Guid applicantId, Guid jobListingId, CancellationToken ct = default);

    /// <summary>All applications for a listing, newest first (employer dashboard).</summary>
    Task<IReadOnlyList<Application>> GetForListingAsync(Guid jobListingId, CancellationToken ct = default);

    /// <summary>One applicant's own applications + listing/company info ("track applications").</summary>
    Task<IReadOnlyList<MyApplicationResponse>> GetByApplicantAsync(Guid applicantId, CancellationToken ct = default);

    Task AddAsync(Application application, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
