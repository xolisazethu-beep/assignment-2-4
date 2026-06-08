using CareerHub.Api.Models;

namespace CareerHub.Api.DTOs;

/// <summary>
/// The shape returned by the read-side job queries (GetActiveListingsAsync,
/// SearchAsync, GetByCompanyAsync). A flat projection — no navigation graphs —
/// so EF Core emits a single SELECT and we never serialise the whole entity.
/// Salaries are ZAR.
/// </summary>
public record JobListingResponse(
    Guid Id,
    string Title,
    string Location,
    string Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid CompanyId,
    string CompanyName,
    string CompanyCity);

/// <summary>
/// The full detail of a single listing (GET /api/jobs/{id}). Unlike the lean list
/// projection above, this includes the long-form <see cref="Description"/> and
/// <see cref="MinimumRequirements"/> — the heavy text fields a board view omits but
/// a detail page needs. Still a flat projection: one SELECT, no entity graph.
/// </summary>
public record JobListingDetailResponse(
    Guid Id,
    string Title,
    string Description,
    string MinimumRequirements,
    string Location,
    string Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid CompanyId,
    string CompanyName,
    string CompanyCity,
    string CompanyProvince,
    string CompanyWebsite);

/// <summary>
/// One row of the Part 8 statistics report: a single active listing with its
/// per-status application breakdown and its rank by total applications.
/// Counts are <c>long</c> because PostgreSQL COUNT() returns bigint.
/// </summary>
public record JobListingStatsResponse(
    Guid JobListingId,
    string Title,
    long TotalApplications,
    long Submitted,
    long UnderReview,
    long Shortlisted,
    long Rejected,
    long Offered,
    long Rank);

public record CreateJobListingRequest(
    string Title,
    string Description,
    string MinimumRequirements,
    string Location,
    JobType Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    DateTime ExpiresAt);
// NOTE: CompanyId is intentionally NOT a client input. The owning company is
// taken from the authenticated employer's token claim, so an employer can only
// ever post for their own company — a forged id in the body has nowhere to land.

/// <summary>
/// An employer as seen by a client: the profile fields plus a count of the
/// company's currently-active, unexpired listings. The frontend uses this to
/// populate company pickers (create form, company-listings view, stats view)
/// without exposing GUIDs to the user. A flat projection, like the read-side
/// job queries — one SELECT, no navigation graph serialised.
/// </summary>
public record CompanyResponse(
    Guid Id,
    string Name,
    string City,
    string Province,
    string Industry,
    string Website,
    int? FoundedYear,
    long ActiveListingCount);

// ── AUTH ─────────────────────────────────────────────────────────────────────

/// <summary>Register a job-seeker account.</summary>
public record RegisterApplicantRequest(string FullName, string Email, string Password);

/// <summary>Register a recruiter account bound to an existing company.</summary>
public record RegisterEmployerRequest(string FullName, string Email, string Password, Guid CompanyId);

public record LoginRequest(string Email, string Password);

/// <summary>
/// What every successful register/login returns. The token carries the identity
/// and role claims the server trusts; the other fields are convenience copies for
/// the client's UI (it must NOT make security decisions from them). CompanyId is
/// present only for employers.
/// </summary>
public record AuthResponse(
    string Token,
    Guid UserId,
    string Email,
    string Role,
    Guid? CompanyId);

/// <summary>
/// One row of an applicant's own application history ("track applications").
/// Flat projection joining the application to its listing + company.
/// </summary>
public record MyApplicationResponse(
    Guid JobListingId,
    string JobTitle,
    string CompanyName,
    string Status,
    DateTime SubmittedAt);

/// <summary>Body for applying to a listing — just an optional cover note.</summary>
public record ApplyRequest(string? CoverNote);
