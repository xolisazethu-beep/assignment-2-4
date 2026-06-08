# CareerHub API — Assignment 2.4

**Query Optimisation & PostgreSQL Features**

This is the Assignment 2.3 three-layer CareerHub API hardened for production load.
It adds database-enforced check constraints, strategic indexes, a generated
`tsvector` full-text column, compiled queries for hot paths, a slow-query
interceptor, a raw-SQL window-function report, and a tuned Npgsql connection pool.

The data is South African end-to-end: ten real SA employers (Takealot, Discovery,
Standard Bank, Capitec, Naspers, Vodacom, Shoprite, Sasol, MTN, Yoco), listings
located in SA towns (Cape Town, Johannesburg, Polokwane, Gqeberha, Mbombela …),
and **all salaries in South African Rand (ZAR)**, monthly gross.

---

## Architecture (continued from 2.3)

```
Controllers/      JobsController — thin, one line per action, calls the service
Services/         JobService    — business rules (salary/expiry validation)
Repositories/     JobListingRepository, ApplicationRepository
                                — IQueryable composition, AsNoTracking, projections,
                                  compiled queries, raw SQL, full-text
Data/             CareerHubDbContext (constraints, indexes, tsvector), SeedData
Infrastructure/   SlowQueryInterceptor, ValidationExceptionHandler,
                  ServiceCollectionExtensions (composition root)
Models/           Company, Applicant, JobListing, Application
```

The service layer depends only on repository **interfaces**. Adding compiled
queries, raw SQL and full-text changed implementation files only — **no interface
signature changed** (Part 6 requirement).

> Model note vs 2.3: `JobListing` replaces the old `IsActive`/`PostedAt` pair with
> an explicit `Status` (`Draft`/`Active`/`Closed`) plus `CreatedAt` and `ExpiresAt`,
> which is what the 2.4 query layer filters on.

## Running it

```bash
docker compose up -d                      # Postgres 17 on localhost:5544
dotnet run                                # migrates + seeds ~6 000 listings, serves :5080
# Scalar UI: http://localhost:5080/scalar/v1
```

| Endpoint | Purpose |
|---|---|
| `GET /api/jobs` | Active, unexpired job board (compiled query) |
| `GET /api/jobs/filter?title=&location=&type=` | Filter the active board by job name, location and/or type (`ILIKE` substring match; all params optional) |
| `GET /api/jobs/{id}` | Full detail of one listing — long description + minimum requirements + company info (`404` if missing) |
| `GET /api/jobs/company/{companyId}` | An employer's own postings |
| `GET /api/jobs/search?q={term}` | Full-text search (GIN index, stemmed) |
| `GET /api/jobs/stats` | Per-status application counts + `RANK()` for the **authenticated employer's own** company (companyId from token, not a query param) |
| `POST /api/jobs` | Create a listing (validated; employer-only) |
| `POST /api/auth/register/applicant` · `register/employer` · `login` | Auth |
| `POST /api/jobs/{id}/applications` | Apply to a listing (applicant-only) |
| `GET /api/applications/me` | The signed-in applicant's application history |
| `GET /api/companies` | All companies with active-listing counts |

**`/filter` vs `/search`** — `/filter` does exact case-insensitive substring matching
(`ILIKE`) on title/location, ideal for faceted browsing ("jobs in Cape Town").
`/search` does stemmed full-text matching over Title + Description via the GIN index,
so `sprint` also finds `sprinting`. They are complementary, not duplicates.

> **Note on stats:** the spec's `GET /api/jobs/stats?companyId={id}` is implemented
> as `GET /api/jobs/stats` with the company taken from the employer's JWT, so one
> employer cannot read another company's pipeline. The repository method still takes
> `companyId` exactly as the spec requires — only the controller's source of that id
> changed, for security.

Seeded volume: **10 companies, 25 applicants, 6 004 job listings** (~25 % Active,
~10 % Active-and-unexpired), **85 applications** across all five statuses.

---

# Part 1 — Written Decisions

### 1. Constraint placement

The service validates `SalaryMin <= SalaryMax` in C#, but that check only runs for
writes that travel **through `JobService.CreateAsync`**. The specific bypass
scenario: during an incident an engineer runs a manual `UPDATE job_listings SET
"SalaryMin" = ...` in psql, or a nightly bulk-import / data-migration script
`COPY`s rows straight into the table, or a second service is later written against
the same database. None of these call the C# method, so none of them see the
validation.

If the database has no constraint, the consequence is **silent permanent data
corruption**: a row with `SalaryMin = 90000, SalaryMax = 50000` is accepted and
sits there. Every downstream consumer inherits the bad data — the job board shows
"R90 000 – R50 000", salary-range filters return nonsense, analytics averages are
skewed, and nobody gets an error at write time to tell them something went wrong.
A `CHECK` constraint makes the rule an invariant of the table itself: it holds no
matter who writes, so the corrupt row is rejected at the source.

### 2. Index column ordering

| Query | Index | Leading column | Why |
|---|---|---|---|
| Active for a company: `WHERE CompanyId = X AND Status = 'Active'` | `ix_job_listings_companyid_status (CompanyId, Status)` | **CompanyId** | `CompanyId` is the high-selectivity equality (one of ten companies ≈ 600 rows). Lead with it so the index seeks straight to that company's slice, then `Status` narrows within it. |
| Expiring soon, all companies: `WHERE ExpiresAt < @t AND Status = 'Active'` | `ix_job_listings_status_expiresat (Status, ExpiresAt)` | **Status** (equality) then **ExpiresAt** (range) | The rule "equality columns before range columns" — once a range scan starts on the leading column, the index can no longer use later columns for seeking. `Status` is the equality predicate both queries share, so it leads; the `ExpiresAt` range then scans contiguously inside `Status = 'Active'`. |

PostgreSQL can only seek on a composite index using a **leftmost prefix** of its
columns. A B-tree is sorted by the first column, then the second within equal
first values, and so on. So `ix_job_listings_companyid_status` accelerates
`WHERE CompanyId = X` and `WHERE CompanyId = X AND Status = Y`, but a query that
filters **only on `Status`** (the non-leftmost column) cannot seek with it — the
`Status` values are scattered across the whole index, so the planner falls back to
a sequential scan (or a different index). That is exactly why the all-companies
"expiring soon" query needs a **separate** `(Status, ExpiresAt)` index rather than
reusing the company index.

### 3. Identifying hot paths (compiled-query candidates)

**`JobListingRepository.GetActiveListingsAsync`** — the public job board's landing
query. It runs on every visit to the CareerHub home page and every "browse jobs"
pagination request, for signed-in users *and* anonymous visitors. Frequency
matters here because the cost EF pays per call is the LINQ→expression-tree→SQL
generation, and this query carries the heaviest expression tree in the app (a
filtered, ordered, projected join). Compiling it once amortises that cost across
the single highest-volume call site in the system.

**`ApplicationRepository.HasAppliedAsync`** — the duplicate-application guard. It
fires on **every** application submission (before the insert) and again every time
a listing-detail page renders for a signed-in seeker (to show the "Already
applied" badge). It is a tiny, fixed, parameterised point lookup that runs far
more often than it changes — the textbook compiled-query case: high call frequency
× a query shape that never varies means the saved per-call planning overhead
compounds.

(Estimated call rates for a 1 000-DAU deployment are in **Part 6**.)

### 4. FromSql scope

The statistics query is impractical in LINQ because it needs two PostgreSQL
features EF Core's LINQ provider **cannot translate**:

1. **`RANK() OVER (ORDER BY COUNT(*) DESC)`** — a window function computed over an
   aggregate. EF Core has no LINQ surface for `RANK()`/`OVER`, and you cannot rank
   by an aggregate you are simultaneously grouping by without a window.
2. **`COUNT(*) FILTER (WHERE status = '…')`** — conditional aggregation. Expressing
   five per-status counts in LINQ would mean five correlated sub-queries or five
   passes, which EF either refuses to translate or turns into a far worse plan.

Both are standard SQL that PostgreSQL executes in a single grouped pass, so the
query is written with `db.Database.SqlQuery<T>` (Part 8).

---

# Part 2 — Database Constraints

All four constraints are declared with the Fluent API in
`CareerHubDbContext.OnModelCreating` (no raw migration SQL, no data annotations)
and scaffolded into migration `AddSalaryAndExpiryCheckConstraints`.

| Constraint | Table | SQL | Business rule |
|---|---|---|---|
| `ck_job_listings_salary_min_positive` | job_listings | `"SalaryMin" IS NULL OR "SalaryMin" > 0` | A salary floor, if given, must be a real positive Rand amount. `NULL` allowed ("market related"). |
| `ck_job_listings_salary_max_gt_min` | job_listings | `"SalaryMax" IS NULL OR "SalaryMin" IS NULL OR "SalaryMax" > "SalaryMin"` | If both bounds are given, max must exceed min. `NULL` guards keep an absent/half-open range legal. |
| `ck_job_listings_expires_after_created` | job_listings | `"ExpiresAt" > "CreatedAt"` | A listing cannot expire before it was created. |
| `ck_applications_submitted_not_future` | applications | `"SubmittedAt" <= now()` | An application cannot be backdated into the future. |

### Constraint decisions (scenario + corruption if absent)

- **salary_min_positive** — *Bypass:* a CSV importer maps an empty cell to `0` or a
  currency-parse bug yields a negative. *Corruption:* "R0 / month" or "–R5 000"
  listings render on the board and break salary filters/averages.
- **salary_max_gt_min** — *Bypass:* an admin hot-fix `UPDATE` in psql swaps the two
  values. *Corruption:* an inverted range no UI can render sensibly and that
  silently drops the row out of `min ≤ x ≤ max` filters.
- **expires_after_created** — *Bypass:* a back-dated migration sets `ExpiresAt` from
  a stale column. *Corruption:* a listing that is "born expired" — never visible on
  the active board, invisible to its own employer, with no error to explain why.
- **submitted_not_future** — *Bypass:* a replay/import job stamps `SubmittedAt` from
  an unsynced clock. *Corruption:* applications ordered "newest first" surface
  future-dated rows permanently at the top of every employer dashboard.

### Proof — psql rejects each violation (API bypassed)

```text
1) INSERT … SalaryMin = -5000 →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_min_positive"
DETAIL: Failing row contains (…, -5000.00, 80000.00, Active, …, 'bad':1 'salari':2 'x':3).

2) INSERT … SalaryMin = 50000, SalaryMax = 40000 →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_max_gt_min"

3) INSERT … ExpiresAt = now() - 1 day →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_expires_after_created"

4) INSERT … SubmittedAt = now() + 1 day →
ERROR:  new row for relation "applications" violates check constraint "ck_applications_submitted_not_future"
```

> The `DETAIL` line in (1) even shows the **auto-generated `tsvector`**
> (`'bad':1 'salari':2 'x':3`) — proof the Part 5 generated column is computed by
> PostgreSQL on every insert. A valid row with `SalaryMin/Max = NULL` still inserts
> (`INSERT 0 1`), confirming the `NULL` guards work.

### Proof — the API rejects the same data with the correct status

```text
POST /api/jobs  { salaryMin: 90000, salaryMax: 50000, … }
→ HTTP 400
  { "title":"Validation failed", "status":400,
    "detail":"SalaryMax must be greater than SalaryMin." }

POST /api/jobs  { salaryMin: 50000, salaryMax: 90000, … }
→ HTTP 201   { "id":"949f692b-…" }
```

The script `scripts/constraint-proofs.sql` reproduces all four psql rejections.

---

# Part 3 — Index Strategy

Generated in a **single** migration `AddJobListingAndApplicationIndexes`. Every
index maps to a real repository query — none are speculative.

| Index | Method | Supports | Column-order rationale |
|---|---|---|---|
| `ix_job_listings_status_expiresat` | B-tree | `GetActiveListingsAsync` (most frequent query, every board load) | Equality (`Status`) leads, range (`ExpiresAt`) follows — see Part 1.2. |
| `ix_job_listings_companyid_status` | B-tree | `GetByCompanyAsync` (employer's own postings) | High-selectivity equality (`CompanyId`) leads. |
| `ix_job_listings_search_vector` | **GIN** | `SearchAsync` full-text over `SearchVector` | GIN is the index type for `tsvector @@ tsquery`; declared with `HasMethod("gin")`. |
| `ix_applications_applicantid_joblistingid` | B-tree | `HasAppliedAsync` (every submission / detail view) | `ApplicantId` leads — the PK leads with `JobListingId`, so it does **not** cover an applicant-first probe. |
| `ix_applications_joblistingid_submittedat` | B-tree | `GetForListingAsync` (employer dashboard, newest first) | `JobListingId` then `SubmittedAt` so rows come back already ordered — no `Sort` node. |

### What each composite can and cannot accelerate

- `ix_job_listings_status_expiresat (Status, ExpiresAt)` **accelerates**
  `WHERE Status = …`, `WHERE Status = … AND ExpiresAt <> …`, and `ORDER BY
  ExpiresAt` within a status. It **cannot** accelerate a query filtering on
  `ExpiresAt` alone — `ExpiresAt` is the non-leftmost column.
- `ix_job_listings_companyid_status (CompanyId, Status)` **accelerates**
  `WHERE CompanyId = …` and `WHERE CompanyId = … AND Status = …`. It **cannot**
  serve `WHERE Status = …` alone, which is precisely why the board query gets its
  own `(Status, ExpiresAt)` index.

### Honest note on the application indexes

The composite PK `(JobListingId, ApplicantId)` already indexes the employer
"applications for a listing" lookup via its leftmost column, so
`ix_applications_joblistingid_submittedat` is justified **only** by the added
`SubmittedAt` — it lets the dashboard's "newest first" query skip a sort, which the
bare PK cannot. `HasAppliedAsync` filters on both keys (equality), so the PK could
serve it too; the applicant-first index also serves "all of an applicant's
applications", which the PK cannot.

### Proof — `\d` shows every index

```text
job_listings indexes:
  "PK_job_listings" PRIMARY KEY, btree ("Id")
  "ix_job_listings_companyid_status"  btree ("CompanyId", "Status")
  "ix_job_listings_search_vector"     gin   ("SearchVector")
  "ix_job_listings_status_expiresat"  btree ("Status", "ExpiresAt")

applications indexes:
  "PK_applications" PRIMARY KEY, btree ("JobListingId", "ApplicantId")
  "ix_applications_applicantid_joblistingid"  btree ("ApplicantId", "JobListingId")
  "ix_applications_joblistingid_submittedat"  btree ("JobListingId", "SubmittedAt")
```

---

# Part 4 — EXPLAIN ANALYZE

Both plans were captured on the seeded **6 004-row** table (593 rows match
Active + unexpired ≈ 10 %). "Before" = the relevant index dropped; "after" =
recreated.

## Query 1 — `GetActiveListingsAsync`

**SQL EF Core generates** (captured from the slow-query interceptor):

```sql
SELECT j."Id", j."Title", j."Location", j."Type"::text, j."SalaryMin", j."SalaryMax",
       j."Status"::text, j."CreatedAt", j."ExpiresAt", j."CompanyId", c."Name", c."City"
FROM job_listings AS j
INNER JOIN companies AS c ON j."CompanyId" = c."Id"
WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now
ORDER BY j."CreatedAt" DESC
```

**BEFORE** (`ix_job_listings_status_expiresat` dropped):

```text
Sort  (actual time=7.819..7.913 rows=593)
  Sort Key: j."CreatedAt" DESC
  ->  Nested Loop  (actual time=0.058..6.645 rows=593)
        ->  Seq Scan on job_listings j  (actual time=0.020..5.753 rows=593)
              Filter: (("Status")::text = 'Active' AND "ExpiresAt" > now())
              Rows Removed by Filter: 5411
        ->  Memoize → Index Scan using "PK_companies" on companies c
Execution Time: 8.131 ms
```

**AFTER** (index recreated):

```text
Sort  (actual time=4.831..4.913 rows=593)
  Sort Key: j."CreatedAt" DESC
  ->  Nested Loop  (actual time=0.478..3.834 rows=593)
        ->  Bitmap Heap Scan on job_listings j  (actual time=0.414..2.737 rows=593)
              Recheck Cond: (("Status")::text = 'Active' AND "ExpiresAt" > now())
              Heap Blocks: exact=367
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
                    Index Cond: (("Status")::text = 'Active' AND "ExpiresAt" > now())
        ->  Memoize → Index Scan using "PK_companies" on companies c
Execution Time: 5.253 ms
```

**What changed.** The leaf node flipped from a **Seq Scan** to a **Bitmap Index
Scan** feeding a **Bitmap Heap Scan**:

- **Seq Scan** (before): PostgreSQL read **all 6 004 rows** and applied the filter
  row-by-row, throwing away 5 411 (`Rows Removed by Filter: 5411`). Work is
  proportional to the whole table.
- **Bitmap Index Scan** (after): PostgreSQL walked `ix_job_listings_status_expiresat`
  to build a bitmap of *only* the 593 matching row locations…
- **Bitmap Heap Scan**: …then fetched just those rows from the 367 heap pages that
  contain them (`Heap Blocks: exact=367`). It never touches the 5 411 non-matching
  rows.

Concretely: **scanning all 6 004 rows became scanning 593 matching rows**, and the
leaf node's time dropped from ~5.75 ms to ~2.7 ms (end-to-end 8.1 ms → 5.3 ms). The
gap widens with table size — the Seq Scan grows with the table, the index scan
grows only with the number of matches.

## Query 2 — Full-text search (`SearchAsync`)

**SQL EF Core generates:**

```sql
SELECT j."Id", j."Title", j."Location", j."Type"::text, …, c."Name", c."City"
FROM job_listings AS j
INNER JOIN companies AS c ON j."CompanyId" = c."Id"
WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now
  AND j."SearchVector" @@ to_tsquery('english', @tsQuery)
```

**BEFORE** (`ix_job_listings_search_vector` GIN dropped), searching `Kubernetes`:

```text
Nested Loop  (actual time=12.389..13.616 rows=3)
  ->  Seq Scan on companies c (rows=10)
  ->  Materialize
        ->  Bitmap Heap Scan on job_listings j  (actual time=12.335..13.532 rows=3)
              Recheck Cond: ("Status" = 'Active' AND "ExpiresAt" > now())
              Filter: ("SearchVector" @@ '''kubernet'''::tsquery)
              Rows Removed by Filter: 590
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
Execution Time: 13.763 ms
```

**AFTER** (GIN recreated):

```text
Hash Join  (actual time=0.434..0.471 rows=3)
  ->  Bitmap Heap Scan on job_listings j  (actual time=0.305..0.336 rows=3)
        Recheck Cond: ("SearchVector" @@ '''kubernet'''::tsquery
                       AND "Status" = 'Active' AND "ExpiresAt" > now())
        Heap Blocks: exact=3
        ->  BitmapAnd
              ->  Bitmap Index Scan on ix_job_listings_search_vector (rows=3)
                    Index Cond: ("SearchVector" @@ '''kubernet'''::tsquery)
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
  ->  Hash → Seq Scan on companies c (rows=10)
Execution Time: 0.685 ms
```

**What changed.** Without the GIN index, the `@@` match could only be a **Filter**:
PostgreSQL used the B-tree to fetch the 593 active rows, then evaluated the tsvector
match on each, discarding 590 (`Rows Removed by Filter: 590`). With the GIN index,
the plan adds a **Bitmap Index Scan on `ix_job_listings_search_vector`** and
**`BitmapAnd`**s it with the status bitmap — so the text match is resolved *in the
index* down to exactly 3 row locations before touching the heap (`Heap Blocks:
exact=3`). Execution dropped **13.76 ms → 0.685 ms (~20×)**. The plan shows the
required `Bitmap Index Scan` on the GIN index, not a Seq Scan.

---

# Part 5 — Full-Text Search

- `JobListing.SearchVector` is a **stored generated column**, declared with
  `entity.HasGeneratedTsVectorColumn(j => j.SearchVector, "english", j => new {
  j.Title, j.Description })`. `\d job_listings` confirms it:
  `generated always as (to_tsvector('english', "Title" || ' ' || "Description")) stored`.
  It is computed by PostgreSQL, never in C#.
- GIN index `ix_job_listings_search_vector` from Part 3.
- `SearchAsync` matches the stored column against `EF.Functions.ToTsQuery("english", …)`
  and returns the **same `JobListingResponse` projection** as `GetActiveListingsAsync`,
  filtered to Active + unexpired.
- The controller action is one line: `=> await jobs.SearchAsync(q, ct);`.

> **Why `ToTsQuery` but not a query-time `ToTsVector`.** The brief lists both
> `EF.Functions.ToTsVector` and `ToTsQuery`, but those instructions pull against the
> harder requirement that the plan show a **Bitmap Index Scan, not a Seq Scan**. The
> GIN index covers the *stored* `SearchVector` column, so to use it the query must
> match that column (`SearchVector @@ to_tsquery(...)`). Calling
> `EF.Functions.ToTsVector("english", Title + " " + Description)` *at query time* would
> recompute a fresh vector for **every row** — which the GIN index cannot answer,
> forcing a Seq Scan and failing the proof. The vector *is* produced by
> `to_tsvector('english', …)` — once, in the database, as the generated column
> (`HasGeneratedTsVectorColumn`) — which is the index-correct place for it. So
> `ToTsQuery` is used at query time; the `to_tsvector` work lives in the column
> definition, exactly as a stored generated tsvector is meant to be used.

### Proof — exactly 3 matches

`GET /api/jobs/search?q=Kubernetes` returns exactly the **3** seeded
"Senior Platform Engineer (Kubernetes)" listings — at **Takealot, Yoco and
Naspers** — and nothing else. The EXPLAIN AFTER plan above confirms the **GIN
Bitmap Index Scan** was used.

### Proof — stemming (which `LIKE` cannot do)

`GET /api/jobs/search?q=sprint` returns the **Scrum Master** listing, whose
description contains "…keeps **sprinting** towards…" — never the literal word
"sprint". The `english` configuration stems both `sprint` and `sprinting` to the
lexeme `sprint` (visible in the plans as `'''sprint'''::tsquery`), so they match.
A `LIKE '%sprint%'` would also coincidentally match here, but it would **fail** the
reverse case and on inflections like "ran"/"running" — full-text stemming is
linguistic, `LIKE` is substring.

---

# Part 6 — Compiled Queries

Two compiled queries, each a `private static readonly Func<…>` built with
`EF.CompileAsyncQuery`, delegated to by the existing method:

```csharp
// JobListingRepository
private static readonly Func<CareerHubDbContext, DateTime, IAsyncEnumerable<JobListingResponse>>
    ActiveListingsQuery = EF.CompileAsyncQuery(…);

// ApplicationRepository
private static readonly Func<CareerHubDbContext, Guid, Guid, Task<bool>>
    HasAppliedQuery = EF.CompileAsyncQuery(…);
```

The public interface methods `GetActiveListingsAsync` and `HasAppliedAsync` are
**unchanged** — the compiled `Func` is an internal detail. (`now` is passed as a
*parameter*, not captured, so the compiled active query still filters against the
current instant on every call.)

### Hot-path justification + call-rate estimate (1 000 DAU)

**`GetActiveListingsAsync`** — the job board landing/pagination query.
Estimate: 1 000 daily users × ~3 board loads each = ~3 000 loads/day, plus
anonymous traffic. Over a ~10-hour active window that is **~5 loads/min average**,
and SA traffic peaks (lunch-time, post-work) push it to **~20–30/min**. It is the
single highest-volume query and carries the heaviest expression tree — compiling
removes the tree-build + SQL-generation cost from every one of those calls.

**`HasAppliedAsync`** — duplicate-application guard + "Already applied" badge.
Estimate: ~2 000 submissions/day (the guard) **plus** every signed-in listing-detail
view (~10 listings viewed/user/day × 1 000 = ~10 000) ⇒ **~12 000 calls/day ≈
20/min average**, higher at peak. A tiny, unchanging point lookup at that frequency
is the ideal compiled-query target.

**Not compiled:** `SearchAsync`, `GetByCompanyAsync`, `GetApplicationStatsAsync`.
The stats report runs maybe a few times per hour from an employer dashboard — far
too infrequent to justify a static cached delegate, and its raw-SQL/`SqlQuery`
shape isn't a LINQ tree to compile anyway. Adding a compiled query there would be
complexity for no measurable gain.

---

# Part 7 — Slow Query Interceptor

`Infrastructure/SlowQueryInterceptor.cs` extends `DbCommandInterceptor`, overrides
both `ReaderExecuted` (sync) and `ReaderExecutedAsync`, and logs at **Warning** when
`eventData.Duration` exceeds the threshold, including elapsed ms and the full SQL.
The threshold is read from `SlowQueryThresholdMs` in config (**default 100 ms** when
absent). It is registered **Singleton** and wired into the DbContext via
`AddInterceptors` inside `AddCareerHubInfrastructure` (an `IServiceCollection`
extension), not directly in `Program.cs`.

### Proof — threshold 0 logs every query

With `"SlowQueryThresholdMs": 0` in `appsettings.Development.json`, every command is
logged:

```text
warn: …SlowQueryInterceptor[0]  Slow query: 85.0 ms (threshold 0 ms)
      SELECT j."Id", … FROM job_listings AS j INNER JOIN companies …
      WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now ORDER BY j."CreatedAt" DESC
warn: …SlowQueryInterceptor[0]  Slow query: 10.4 ms (threshold 0 ms)
      SELECT j."Id", … WHERE … AND j."SearchVector" @@ to_tsquery('english', @tsQuery)
```

### Proof — threshold 100 is silent for normal queries

Restored to `100`, hitting `GET /api/jobs` and `GET /api/jobs/search` produces
**zero** new warnings (both run in <100 ms). The only warning in the whole run is a
single startup line:

```text
Slow query: 170.4 ms (threshold 100 ms)
SELECT "MigrationId", "ProductVersion"
```

That is the **first** database round-trip after launch — a *cold* connection paying
TCP + TLS + auth. It is not a normal query, and it is the real-world motivation for
the **Minimum Pool Size** in Part 9.

---

# Part 8 — Raw SQL with `FromSql`

`GetApplicationStatsAsync(Guid companyId)` uses `db.Database.SqlQuery<JobListingStatsResponse>`
with an interpolated `FormattableString`. The SQL uses `RANK() OVER (ORDER BY
COUNT(*) DESC)` for the rank and `COUNT(*) FILTER (WHERE "Status" = '…')` for the
per-status breakdown, with `companyId` passed as a parameter.

### Proof — `GET /api/jobs/stats?companyId={Takealot}`

```json
[
  { "title":"Lead QA Engineer",            "totalApplications":18,
    "submitted":4,"underReview":3,"shortlisted":5,"rejected":2,"offered":4, "rank":1 },
  { "title":"Intermediate Frontend Engineer","totalApplications":11,
    "submitted":3,"underReview":2,"shortlisted":2,"rejected":1,"offered":3, "rank":2 },
  { "title":"Lead DevOps Engineer",        "totalApplications":5,
    "submitted":2,"underReview":2,"shortlisted":0,"rejected":0,"offered":1, "rank":3 },
  { "title":"Graduate Backend Engineer",   "totalApplications":0, …, "rank":4 },
  …
]
```

The per-status counts sum to the total (4+3+5+2+4 = 18 ✓) and `RANK()` correctly
makes the most-applied-to listing **rank 1**, with the zero-application listings
tying at rank 4.

### Parameterisation safety

- **Interpolation inside `SqlQuery<T>($"… {companyId} …")` is injection-safe**
  because EF Core treats the `FormattableString`'s holes as **parameters** — it
  sends `WHERE "CompanyId" = $1` plus the value `$1` separately, so the value is
  never parsed as SQL.
- **`string.Format`/`+` concatenation is unsafe** because it splices the value into
  the SQL *text* before EF sees it, so EF runs the whole string verbatim and a
  crafted input (e.g. `'… ; DROP TABLE applications; --`) executes as SQL.

(Here `companyId` is a `Guid` so it could not carry an injection payload anyway —
but the rule must hold for the `string` parameters this pattern will meet next.)

---

# Part 9 — Connection Pool Configuration

Pool settings live in the connection string.

**Maximum Pool Size = 30** — calculation:

```
PostgreSQL max_connections      = 100
reserve for admin / monitoring  = 10
                                ------
usable by the application       = 90
application instances           = 3
Maximum Pool Size per instance  = 90 / 3 = 30
```

3 instances × 30 = 90 ≤ 90, so even at full saturation the app never starves the
admin/monitoring reserve.

**Minimum Pool Size = 5 (production)** — keeps 5 connections open per instance
through quiet periods so the first requests after a lull skip the cold-connection
penalty. We measured that penalty directly: the first DB round-trip after launch
took **170 ms** (TCP + TLS + auth) versus <10 ms once warm (Part 7). 5 × 3 = 15 warm
connections is comfortably within the 90 budget.

**Development differs** (`appsettings.Development.json`): `Minimum Pool Size=1`,
`Maximum Pool Size=10`. The constraints are different — one developer, one
instance, a laptop Postgres — so a large warm pool would just waste connections.

### What happens when the pool is exhausted

When all 30 connections are checked out and a new request needs one, Npgsql does
**not** open a 31st — it **blocks the request**, queuing it until a connection is
returned to the pool. If none frees within the connection `Timeout` (default 15 s),
Npgsql throws *"The connection pool has been exhausted…"*.

**Observable symptom from the client:** requests that normally return in tens of
milliseconds suddenly **hang** for up to ~15 s; under sustained overload they then
fail with **HTTP 500** as the timeout fires. Latency percentiles spike and error
rate climbs while CPU may look idle — the classic signature of pool exhaustion
rather than slow queries.

---

# Proving It Works — checklist

| # | Requirement | Where |
|---|---|---|
| 1 | Each constraint rejected in psql **and** by the API (correct status) | Part 2 |
| 2 | `\d job_listings` / `\d applications` show every index | Part 3 |
| 3 | `GetActiveListingsAsync` plan: before = Seq Scan, after ≠ Seq Scan | Part 4 Q1 |
| 4 | Search term in exactly 3 listings; GIN used; `sprint` matches `sprinting` | Parts 4 Q2, 5 |
| 5 | Both compiled queries are `static readonly` fields; interfaces unchanged | Part 6 |
| 6 | Interceptor logs all at threshold 0; silent at 100 | Part 7 |
| 7 | Stats endpoint returns per-status counts + correct rank 1 | Part 8 |
| 8 | Connection string pool settings + Max Pool Size arithmetic | Part 9 |

---

## Suggested commits

1. Add check constraints for salary range and expiry date with migration
2. Add indexes for active-listing query, company scope, and application hot paths
3. Add GIN index and full-text search implementation
4. Implement compiled queries for hot-path repository methods
5. Add slow-query interceptor with configurable threshold
6. Add application-statistics endpoint using FromSql with window function
7. Configure Npgsql connection-pool settings
