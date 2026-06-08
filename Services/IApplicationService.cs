using CareerHub.Api.DTOs;
using CareerHub.Api.Models;

namespace CareerHub.Api.Services;

public interface IApplicationService
{
    /// <summary>Apply the given applicant to a listing. Returns nothing; throws on conflict/404.</summary>
    Task ApplyAsync(Guid applicantId, Guid jobListingId, string? coverNote, CancellationToken ct = default);

    /// <summary>The applicant's own application history.</summary>
    Task<IReadOnlyList<MyApplicationResponse>> GetMineAsync(Guid applicantId, CancellationToken ct = default);

    /// <summary>PART 7: a single application by composite key, or null. For the ETag GET.</summary>
    Task<ApplicationResponse?> GetAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default);

    /// <summary>
    /// PART 5B: move an application to a new status, enforcing the legal-transition
    /// state machine. Throws NotFoundException (404) if it does not exist, or
    /// ArgumentException (400) for an illegal transition.
    /// </summary>
    Task UpdateStatusAsync(Guid jobListingId, Guid applicantId, ApplicationStatus newStatus, CancellationToken ct = default);
}
