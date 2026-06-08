using System.Linq.Expressions;
using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Repositories;

public class JobListingRepository(CareerHubDbContext db) : IJobListingRepository
{
    // ── SHARED READ-SIDE PROJECTION ──────────────────────────────────────────
    // One definition of "what a listing looks like to a client", reused by the
    // company and search queries so the SELECT column list never drifts apart.
    private static readonly Expression<Func<JobListing, JobListingResponse>> Project = j =>
        new JobListingResponse(
            j.Id, j.Title, j.Location, j.Type.ToString(),
            j.SalaryMin, j.SalaryMax, j.Status.ToString(),
            j.CreatedAt, j.ExpiresAt,
            j.CompanyId, j.Company.Name, j.Company.City);

    // ── PART 6: COMPILED QUERY — GetActiveListingsAsync ──────────────────────
    // HOT PATH. This is the public job board's landing query: it runs on every
    // visit to the home page and every "browse all jobs" pagination request — by
    // far the highest-frequency query in CareerHub (see README "Hot path
    // justification"). Compiling it once removes the LINQ-expression-tree parse
    // and SQL-generation cost from every single one of those calls, which is the
    // exact situation EF.CompileAsyncQuery exists for. `now` is a parameter, not
    // a captured constant, so each call still filters against the current instant.
    private static readonly Func<CareerHubDbContext, DateTime, IAsyncEnumerable<JobListingResponse>>
        ActiveListingsQuery = EF.CompileAsyncQuery(
            (CareerHubDbContext ctx, DateTime now) =>
                ctx.JobListings
                   .AsNoTracking()
                   .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now)
                   .OrderByDescending(j => j.CreatedAt)
                   .Select(j => new JobListingResponse(
                       j.Id, j.Title, j.Location, j.Type.ToString(),
                       j.SalaryMin, j.SalaryMax, j.Status.ToString(),
                       j.CreatedAt, j.ExpiresAt,
                       j.CompanyId, j.Company.Name, j.Company.City)));

    public async Task<IReadOnlyList<JobListingResponse>> GetActiveListingsAsync(CancellationToken ct = default)
    {
        var results = new List<JobListingResponse>();
        await foreach (var row in ActiveListingsQuery(db, DateTime.UtcNow).WithCancellation(ct))
            results.Add(row);
        return results;
    }

    public async Task<IReadOnlyList<JobListingResponse>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        // Hits ix_job_listings_companyid_status (CompanyId leads, equality first).
        return await db.JobListings
            .AsNoTracking()
            .Where(j => j.CompanyId == companyId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(Project)
            .ToListAsync(ct);
    }

    // ── FILTERED BROWSE: title / location / type ─────────────────────────────
    // Composable IQueryable: each supplied filter narrows the query; absent ones
    // are skipped so they don't appear in the generated SQL at all. The Status +
    // ExpiresAt predicate still rides ix_job_listings_status_expiresat. ILike is a
    // PostgreSQL case-insensitive LIKE — exact substring matching, distinct from
    // the stemmed full-text SearchAsync.
    public async Task<IReadOnlyList<JobListingResponse>> BrowseAsync(
        string? title, string? location, JobType? type, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var query = db.JobListings
            .AsNoTracking()
            .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now);

        if (!string.IsNullOrWhiteSpace(title))
            query = query.Where(j => EF.Functions.ILike(j.Title, $"%{title}%"));

        if (!string.IsNullOrWhiteSpace(location))
            query = query.Where(j => EF.Functions.ILike(j.Location, $"%{location}%"));

        if (type is not null)
            query = query.Where(j => j.Type == type);

        return await query
            .OrderByDescending(j => j.CreatedAt)
            .Select(Project)
            .ToListAsync(ct);
    }

    public Task<JobListingDetailResponse?> GetDetailByIdAsync(Guid id, CancellationToken ct = default) =>
        db.JobListings
            .AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new JobListingDetailResponse(
                j.Id, j.Title, j.Description, j.MinimumRequirements, j.Location, j.Type.ToString(),
                j.SalaryMin, j.SalaryMax, j.Status.ToString(),
                j.CreatedAt, j.ExpiresAt,
                j.CompanyId, j.Company.Name, j.Company.City, j.Company.Province, j.Company.Website))
            .FirstOrDefaultAsync(ct);

    // ── PART 5: FULL-TEXT SEARCH ─────────────────────────────────────────────
    public async Task<IReadOnlyList<JobListingResponse>> SearchAsync(string searchTerm, CancellationToken ct = default)
    {
        // Build a tsquery that ANDs the user's words: "site reliability" -> 'site & reliability'.
        // ToTsQuery applies the 'english' configuration, so it stems too — a search
        // for "sprint" matches listings containing "sprinting" (LIKE cannot do this).
        var words = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tsQuery = string.Join(" & ", words);
        var now = DateTime.UtcNow;

        // Matching against the STORED SearchVector column (not a per-row to_tsvector
        // call) is what lets PostgreSQL use the GIN index — verified by EXPLAIN
        // ANALYZE showing a Bitmap Index Scan on ix_job_listings_search_vector.
        return await db.JobListings
            .AsNoTracking()
            .Where(j => j.Status == ListingStatus.Active
                        && j.ExpiresAt > now
                        && j.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery)))
            .Select(Project)
            .ToListAsync(ct);
    }

    // ── PART 8: RAW SQL WITH RANK() WINDOW FUNCTION ──────────────────────────
    public async Task<IReadOnlyList<JobListingStatsResponse>> GetApplicationStatsAsync(Guid companyId, CancellationToken ct = default)
    {
        // EF Core's LINQ provider cannot translate RANK() OVER (...) nor the
        // COUNT(*) FILTER (WHERE ...) conditional aggregation, so this is raw SQL.
        // {companyId} is interpolated into a FormattableString: EF turns it into a
        // parameterised @p0 placeholder — it is NEVER concatenated into the text,
        // so it cannot be a SQL-injection vector (see README "FromSql parameterisation").
        FormattableString sql = $"""
            SELECT
                j."Id"                                              AS "JobListingId",
                j."Title"                                           AS "Title",
                COUNT(a."ApplicantId")                              AS "TotalApplications",
                COUNT(*) FILTER (WHERE a."Status" = 'Submitted')    AS "Submitted",
                COUNT(*) FILTER (WHERE a."Status" = 'UnderReview')  AS "UnderReview",
                COUNT(*) FILTER (WHERE a."Status" = 'Shortlisted')  AS "Shortlisted",
                COUNT(*) FILTER (WHERE a."Status" = 'Rejected')     AS "Rejected",
                COUNT(*) FILTER (WHERE a."Status" = 'Offered')      AS "Offered",
                RANK() OVER (ORDER BY COUNT(a."ApplicantId") DESC)  AS "Rank"
            FROM job_listings j
            LEFT JOIN applications a ON a."JobListingId" = j."Id"
            WHERE j."CompanyId" = {companyId} AND j."Status" = 'Active'
            GROUP BY j."Id", j."Title"
            ORDER BY "Rank", j."Title"
            """;

        return await db.Database
            .SqlQuery<JobListingStatsResponse>(sql)
            .ToListAsync(ct);
    }

    public Task<JobListing?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.JobListings.FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task AddAsync(JobListing listing, CancellationToken ct = default) =>
        await db.JobListings.AddAsync(listing, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
