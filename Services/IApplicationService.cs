using CareerHub.Api.DTOs;

namespace CareerHub.Api.Services;

public interface IApplicationService
{
    /// <summary>Apply the given applicant to a listing. Returns nothing; throws on conflict/404.</summary>
    Task ApplyAsync(Guid applicantId, Guid jobListingId, string? coverNote, CancellationToken ct = default);

    /// <summary>The applicant's own application history.</summary>
    Task<IReadOnlyList<MyApplicationResponse>> GetMineAsync(Guid applicantId, CancellationToken ct = default);
}
