# CareerHub API — Setup & Scalar Guide

A step-by-step guide to running the CareerHub API and exercising every endpoint
from the **Scalar** API reference UI (the modern replacement for Swagger UI that
ships with .NET's `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore`).

---

## 1. Prerequisites

| Tool | Version used | Check |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --version` |
| Docker Desktop | any recent | `docker --version` |
| EF Core CLI | 10.0.x | `dotnet ef --version` (install: `dotnet tool install --global dotnet-ef`) |

You do **not** need a local PostgreSQL install — it runs in Docker.

---

## 2. Start PostgreSQL

```bash
docker compose up -d
```

This starts **PostgreSQL 17** on `localhost:5544` (see `docker-compose.yml`). The
development connection string in `appsettings.Development.json` already points there:

```
Host=localhost;Port=5544;Database=CareerHub24;Username=postgres;Password=password123
```

Verify it is up:

```bash
docker ps          # look for the careerhub24-pg container, status "Up"
```

---

## 3. Run the API

```bash
dotnet run
```

On startup the app automatically:

1. **Applies all migrations** (`db.Database.MigrateAsync()`) — creates the schema,
   check constraints, indexes, the generated `tsvector` column and the GIN index.
2. **Seeds a realistic South African dataset** (`SeedData`):
   - **10 real SA employers** — Takealot, Discovery, Standard Bank, Capitec, Naspers,
     Vodacom, Shoprite, Sasol, MTN, Yoco.
   - **~6 000 job listings** with salaries in **Rand (ZAR)**, SA locations, generated
     descriptions and **minimum-requirements** text (Matric, tertiary qualification,
     work authorisation, Employment Equity language).
   - **25 applicants** with SA names, plus **applications** across all five pipeline
     statuses.
3. Seeds two **login-ready demo accounts** (see §5).

First run takes ~20–30 s because it inserts thousands of rows. Subsequent runs skip
seeding (it is idempotent — guarded on "are there already companies?").

When you see `Now listening on: http://localhost:5080`, it is ready.

> **Reset the data:** `dotnet ef database drop --force && dotnet run` re-seeds from
> scratch.

---

## 4. Open Scalar

Browse to:

```
http://localhost:5080/scalar/v1
```

(The `http` launch profile opens this automatically.) Scalar is only mapped in the
**Development** environment — see `Program.cs`:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();              // serves the OpenAPI document at /openapi/v1.json
    app.MapScalarApiReference();   // serves the Scalar UI at /scalar/v1
}
```

You will see every endpoint grouped by controller (Auth, Jobs, Applications,
Companies). Each one has a **Test Request** panel on the right where you can fill in
parameters / a JSON body and click **Send**.

---

## 5. Authenticating in Scalar (JWT Bearer)

Most write endpoints are protected. Public reads (`GET /api/jobs`, `/filter`,
`/{id}`, `/search`, `/company/{id}`, `/api/companies`) need **no** token.

**Demo accounts** (seeded with real password hashes):

| Role | Email | Password |
|---|---|---|
| Applicant (job seeker) | `demo.applicant@careerhub.co.za` | `DemoPass123!` |
| Employer (Takealot recruiter) | `demo.employer@takealot.co.za` | `DemoPass123!` |

### Steps

1. In Scalar, open **`POST /api/auth/login`** → **Test Request**.
2. Send this body:
   ```json
   { "email": "demo.employer@takealot.co.za", "password": "DemoPass123!" }
   ```
3. Copy the `token` value from the response.
4. Click the **Authentication** (lock 🔒) area at the top of Scalar, choose the
   **Bearer** scheme, and paste the token. Scalar now adds
   `Authorization: Bearer <token>` to every subsequent request.

> Log in as the **employer** to use `POST /api/jobs` and `GET /api/jobs/stats`.
> Log in as the **applicant** to use `POST /api/jobs/{id}/applications` and
> `GET /api/applications/me`. The role is baked into the token, so the two account
> types unlock different endpoints.

---

## 6. A guided walkthrough

### Public job board (no auth)

| Try | Endpoint | Expected |
|---|---|---|
| Browse the board | `GET /api/jobs` | List of active, unexpired listings (lean projection, no long description) |
| Filter by name | `GET /api/jobs/filter?title=engineer` | Only active listings whose **title** contains "engineer" |
| Filter by location | `GET /api/jobs/filter?location=Cape Town` | Only active listings located in Cape Town |
| Combine filters | `GET /api/jobs/filter?title=Scrum&location=Durban` | The Durban "Scrum Master" listing |
| Filter by type | `GET /api/jobs/filter?type=Internship` | Active internships only (`type` ∈ FullTime, PartTime, Contract, Internship, Learnership) |
| Get one listing | `GET /api/jobs/{id}` | **Full** detail — long description, **minimum requirements**, company city/province/website. `404` for an unknown id |

> Copy any `id` from the `GET /api/jobs` response and paste it into `GET /api/jobs/{id}`
> to see the rich description + minimum-requirements text.

### Full-text search proofs (no auth)

| Try | Endpoint | Expected |
|---|---|---|
| Exact-count proof | `GET /api/jobs/search?q=Kubernetes` | Exactly **3** listings (Takealot, Naspers, Yoco) |
| **Stemming** proof | `GET /api/jobs/search?q=sprint` | Matches the **"Scrum Master"** listing whose description says *sprint**ing*** — something `LIKE 'sprint'` could never do |

### Employer flow (log in as employer first)

| Try | Endpoint | Expected |
|---|---|---|
| Post a job | `POST /api/jobs` | `201 Created`; company comes from your token, not the body |
| Pipeline stats | `GET /api/jobs/stats` | Per-status counts (`Submitted`, `UnderReview`, …) and a `RANK()` — Takealot's most-applied listing is **rank 1** |

Sample `POST /api/jobs` body:

```json
{
  "title": "Senior Backend Engineer",
  "description": "Build and scale our order platform in C# and PostgreSQL.",
  "minimumRequirements": "Matric; degree in CS; 5+ years C#/.NET; valid SA work permit.",
  "location": "Sandton, Gauteng",
  "type": "FullTime",
  "salaryMin": 75000,
  "salaryMax": 110000,
  "expiresAt": "2026-12-31T00:00:00Z"
}
```

### Applicant flow (log in as applicant first)

| Try | Endpoint | Expected |
|---|---|---|
| Apply | `POST /api/jobs/{id}/applications` (body `{ "coverNote": "..." }`) | `201`; a second apply to the same listing is rejected (one application per person per listing) |
| My applications | `GET /api/applications/me` | Your application history with job titles and statuses |

### Constraint proof via Scalar (Part 2)

Try `POST /api/jobs` (as employer) with `"salaryMin": 100000, "salaryMax": 50000`.
The API returns **`400 Bad Request`** (service validation), and the database
`ck_job_listings_salary_max_gt_min` check constraint would reject the same row even
if the service were bypassed. See `scripts/constraint-proofs.sql` for the raw-`psql`
proofs.

---

## 7. Useful psql commands

```bash
# open a shell in the database
docker exec -it careerhub24-pg psql -U postgres -d CareerHub24

# inside psql:
\d job_listings      -- show columns, check constraints and all Part 3 indexes
\d applications      -- show the application indexes
\q                   -- quit
```

To confirm the GIN index is actually used by search:

```sql
EXPLAIN ANALYZE
SELECT * FROM job_listings
WHERE "SearchVector" @@ to_tsquery('english', 'kubernetes')
  AND "Status" = 'Active' AND "ExpiresAt" > now();
-- look for "Bitmap Index Scan on ix_job_listings_search_vector"
```

---

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| `Npgsql ... connection refused` | `docker compose up -d` — Postgres isn't running |
| Scalar UI is `404` | You're not in Development; run `dotnet run` (the `http` profile sets `ASPNETCORE_ENVIRONMENT=Development`) |
| `401 Unauthorized` on a write | Log in (§5) and paste the Bearer token into Scalar's auth dialog |
| `403 Forbidden` on a write | You're logged in as the wrong role (employer-only vs applicant-only) |
| Port 5080 already in use | Stop the other process, or edit `Properties/launchSettings.json` |
| Want a clean dataset | `dotnet ef database drop --force` then `dotnet run` |

---

## 9. Where the OpenAPI document lives

Scalar renders the OpenAPI JSON that .NET generates. You can fetch it directly:

```
http://localhost:5080/openapi/v1.json
```

Import that URL into Postman, Insomnia, or another Scalar instance if you prefer a
different client.

---

# 10. Demonstrating the Assignment Proofs ("Proving It Works")

This is a click-by-click / command-by-command script for the 8 required proofs. Each
one lists **what it proves**, **how to run it**, and the **expected output** (captured
from this seeded database — yours will match aside from random GUIDs).

> **Two terminals help:** one running `dotnet run`, one for `psql`. Open a psql shell with:
> ```bash
> docker exec -it careerhub24-pg psql -U postgres -d CareerHub24
> ```
> The command blocks below prefix each statement with `docker exec … -c "…"` so you can
> paste them without opening a shell, but inside an interactive `psql` you'd paste only
> the SQL part.

---

## Proof 1 — Constraint enforcement (DB rejects, then API rejects)

**Proves:** the four Part 2 check constraints reject bad data *even when the API is
bypassed*, and the API returns `400` for the same data.

### 1a. Bypass the API — raw `psql` inserts (all four must ERROR)

```bash
# grab a real company / applicant / listing id to satisfy the foreign keys
CID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM companies LIMIT 1")
AID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM applicants LIMIT 1")
LID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM job_listings LIMIT 1")

# SalaryMin must be > 0
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',0,100,'Active',now(),now()+interval '10 days','$CID');"

# SalaryMax must be > SalaryMin
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',100,50,'Active',now(),now()+interval '10 days','$CID');"

# ExpiresAt must be after CreatedAt
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',100,200,'Active',now(),now()-interval '1 day','$CID');"

# SubmittedAt must not be in the future
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO applications (\"JobListingId\",\"ApplicantId\",\"Status\",\"SubmittedAt\",\"CoverNote\") VALUES ('$LID','$AID','Submitted',now()+interval '2 days','x');"
```

**Expected — every insert is rejected:**
```
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_min_positive"
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_max_gt_min"
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_expires_after_created"
ERROR:  new row for relation "applications" violates check constraint "ck_applications_submitted_not_future"
```

### 1b. Same data through the API — `400 Bad Request` (in Scalar)

Log in as the **employer** (§5), then `POST /api/jobs` with:
```json
{ "title": "Bad", "description": "d", "minimumRequirements": "r", "location": "x",
  "type": "FullTime", "salaryMin": 100, "salaryMax": 50, "expiresAt": "2026-12-31T00:00:00Z" }
```
**Expected:** HTTP **`400 Bad Request`** with a Problem Details body — the `JobService`
validation rejects it before it ever reaches the database.

---

## Proof 2 — Index verification (`\d`)

**Proves:** every Part 3 index physically exists.

```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "\d job_listings"
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "\d applications"
```

**Expected — Indexes sections include:**
```
job_listings:
    "ix_job_listings_companyid_status"  btree ("CompanyId", "Status")
    "ix_job_listings_search_vector"     gin   ("SearchVector")
    "ix_job_listings_status_expiresat"  btree ("Status", "ExpiresAt")
applications:
    "ix_applications_applicantid_joblistingid"  btree ("ApplicantId", "JobListingId")
    "ix_applications_joblistingid_submittedat"  btree ("JobListingId", "SubmittedAt")
```
The `\d job_listings` output also shows the four check constraints and the generated
column: `"SearchVector" tsvector GENERATED ALWAYS AS (to_tsvector('english', ...)) STORED`.

---

## Proof 3 — EXPLAIN ANALYZE before vs after (active-listings query)

**Proves:** the index turns a full-table `Seq Scan` into an `Index Scan`.

### AFTER (index present — current state)
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now();"
```
**Expected:** `Bitmap Index Scan on ix_job_listings_status_expiresat` (no Seq Scan).

### BEFORE (drop the index, observe the Seq Scan, then put it back)
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"DROP INDEX ix_job_listings_status_expiresat; EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now();"

# restore it immediately
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"CREATE INDEX ix_job_listings_status_expiresat ON job_listings (\"Status\",\"ExpiresAt\");"
```
**Expected BEFORE:**
```
Seq Scan on job_listings j  (cost=0.00..1607.21 rows=603 ...)
   Filter: ((("Status")::text = 'Active'::text) AND ("ExpiresAt" > now()))
   Rows Removed by Filter: 5401
```
**What changed:** BEFORE, PostgreSQL reads all ~6 000 rows and discards 5 401 with a
filter (`Seq Scan`). AFTER, it walks `ix_job_listings_status_expiresat` to find only the
611 matching rows, then fetches just those heap pages (`Bitmap Index Scan` →
`Bitmap Heap Scan`). The README Part 4 section has both full plans side by side.

> The migration recreates this index on any fresh DB, but if you forget the restore
> step, `dotnet ef database drop --force && dotnet run` rebuilds everything.

---

## Proof 4 — Full-text search (exact matches + stemming + GIN index)

**Proves:** search returns only matching listings, stems words, and uses the GIN index.

### Matching listings (3 of the 6 000)
```bash
# via the API:
curl "http://localhost:5080/api/jobs/search?q=Kubernetes"
# or in psql:
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"SELECT j.\"Title\", c.\"Name\" FROM job_listings j JOIN companies c ON c.\"Id\"=j.\"CompanyId\" WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now() AND j.\"SearchVector\" @@ to_tsquery('english','kubernetes');"
```
**Expected:** exactly **3** rows — "Senior Platform Engineer (Kubernetes)" at **Takealot,
Yoco, Naspers**.

### Stemming (`LIKE` cannot do this)
```bash
curl "http://localhost:5080/api/jobs/search?q=sprint"
```
**Expected:** the **Scrum Master** listing, whose description says "…keeps **sprinting**
towards…". `sprint` and `sprinting` both stem to the lexeme `sprint`, so they match.

### GIN index confirmation
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now() AND j.\"SearchVector\" @@ to_tsquery('english','kubernetes');"
```
**Expected:** `Bitmap Index Scan on ix_job_listings_search_vector` (the GIN index), with
the term stemmed to `'kubernet'::tsquery`. **No Seq Scan.**

---

## Proof 5 — Compiled query confirmation

**Proves:** both hot paths use `EF.CompileAsyncQuery` as `static readonly` fields, and
the public signatures never changed.

```bash
grep -n "static readonly\|EF.CompileAsyncQuery" Repositories/JobListingRepository.cs
grep -n "static readonly\|EF.CompileAsyncQuery" Repositories/ApplicationRepository.cs
grep -n "GetActiveListingsAsync\|HasAppliedAsync" Repositories/IJobListingRepository.cs Repositories/IApplicationRepository.cs
```
**Expected:**
- `JobListingRepository.ActiveListingsQuery = EF.CompileAsyncQuery(...)` — a
  `private static readonly Func<...>` field; `GetActiveListingsAsync` enumerates it.
- `ApplicationRepository.HasAppliedQuery = EF.CompileAsyncQuery(...)` — same shape;
  `HasAppliedAsync` delegates to it.
- The interface methods are unchanged (no compiled-query types leak into the contract).

These two are the only compiled queries — the README Part 6 section justifies why each
is a genuine hot path (per-page-load board query; per-submission duplicate guard) and
estimates the call rate at 1 000 daily users.

---

## Proof 6 — Slow query interceptor

**Proves:** the interceptor logs every command over the configured threshold, and is
silent below it.

### Log everything (threshold 0)
1. In `appsettings.Development.json`, set `"SlowQueryThresholdMs": 0`.
2. `dotnet run`, then `curl http://localhost:5080/api/jobs` (or hit it in Scalar).
3. In the `dotnet run` console, every query appears as a **Warning**:
```
warn: CareerHub.Api.Infrastructure.SlowQueryInterceptor[0]
      Slow query: 12.4 ms (threshold 0 ms)
      SELECT j."Id", j."Title", ... FROM job_listings AS j
      WHERE j."Status" = 'Active' AND j."ExpiresAt" > @__now_0 ...
```

### Silent for normal queries (threshold 100)
4. Restore `"SlowQueryThresholdMs": 100`, restart, and `curl` again.
5. **Expected:** no `Slow query` warnings — every query finishes under 100 ms.

> The threshold is read from configuration, so this is a config change only — no code
> edit, no rebuild needed beyond restarting the app.

---

## Proof 7 — Raw SQL statistics (RANK() + per-status counts)

**Proves:** the `FromSql`/`SqlQuery` endpoint returns per-status counts and a correct
`RANK()`, with the most-applied listing as rank 1.

### Via the API (employer-scoped)
Log in as the **employer** (Takealot demo account), then call `GET /api/jobs/stats`.

### Via psql (the same query the repository runs)
```bash
TID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM companies WHERE \"Name\"='Takealot'")
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "
SELECT j.\"Title\", COUNT(a.\"ApplicantId\") AS total,
  COUNT(*) FILTER (WHERE a.\"Status\"='Submitted')  AS submitted,
  COUNT(*) FILTER (WHERE a.\"Status\"='UnderReview') AS underreview,
  COUNT(*) FILTER (WHERE a.\"Status\"='Shortlisted') AS shortlisted,
  COUNT(*) FILTER (WHERE a.\"Status\"='Rejected')    AS rejected,
  COUNT(*) FILTER (WHERE a.\"Status\"='Offered')     AS offered,
  RANK() OVER (ORDER BY COUNT(a.\"ApplicantId\") DESC) AS rank
FROM job_listings j LEFT JOIN applications a ON a.\"JobListingId\"=j.\"Id\"
WHERE j.\"CompanyId\"='$TID' AND j.\"Status\"='Active'
GROUP BY j.\"Id\", j.\"Title\" ORDER BY rank, j.\"Title\" LIMIT 5;"
```
**Expected:** the top row is the listing with the most applications at **rank 1** (the
seed gives Takealot a clear 18 / 11 / 5 spread), with the five status columns adding up
to the total. Listings with no applications share the last rank.

---

## Proof 8 — Connection pool configuration

**Proves:** pool sizes are set and justified.

```bash
grep -i "pool" appsettings.json
grep -i "pool" appsettings.Development.json
```
**Expected:**
- **Production** (`appsettings.json`): `Minimum Pool Size=5;Maximum Pool Size=30`.
- **Development** (`appsettings.Development.json`): `Minimum Pool Size=1;Maximum Pool Size=10`.

**Maximum Pool Size calculation (production):**
```
max_connections (PostgreSQL)        = 100
reserve for admin/monitoring        = 10
usable for the application          = 90
app instances                       = 3
=> Maximum Pool Size per instance   = 90 / 3 = 30
```
**Pool exhaustion behaviour:** when all 30 connections are checked out and another
request arrives, Npgsql does **not** error immediately — the request **waits** for a
connection up to `Timeout` (default 30 s). If one frees up, it proceeds; if the wait
elapses, the caller gets a `NpgsqlException` ("The connection pool has been exhausted").
**Observable symptom from the client:** requests hang and then fail with a 500/timeout
under sustained load — the signal to raise `Maximum Pool Size` or add capacity.

---

### Proof checklist at a glance

| # | Proof | Where |
|---|---|---|
| 1 | Check constraints reject bad data (psql + API 400) | psql + Scalar |
| 2 | All Part 3 indexes exist | `\d` |
| 3 | Seq Scan → Index Scan | `EXPLAIN ANALYZE` |
| 4 | Full-text: 3 matches, stemming, GIN index | API + `EXPLAIN ANALYZE` |
| 5 | Two compiled queries, signatures unchanged | `grep` |
| 6 | Interceptor logs > threshold, silent below | `dotnet run` console |
| 7 | RANK() + per-status counts, rank 1 correct | API + psql |
| 8 | Pool sizes set + calculation | `appsettings*.json` |
