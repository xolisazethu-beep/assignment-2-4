using System.Text;
using System.Text.Json.Serialization;
using CareerHub.Api.Data;
using CareerHub.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// All wiring lives in IServiceCollection extension methods (Part 7 requirement):
// the DbContext (with the slow-query interceptor), repositories and services.
builder.Services.AddCareerHubInfrastructure(builder.Configuration);
builder.Services.AddCareerHubRepositories();
builder.Services.AddCareerHubServices();

// Serialise/accept enums as their NAMES ("FullTime") rather than integers, so the
// JSON the API reads (CreateJobListingRequest.Type) matches what it writes
// (JobListingResponse already stringifies enums) and what the frontend sends.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// ── JWT BEARER AUTHENTICATION ────────────────────────────────────────────────
// Tokens are signed/validated with the symmetric key from configuration (see
// appsettings "Jwt"; the key is a secret overridden out-of-band in real envs).
var jwt = builder.Configuration.GetSection("Jwt");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
        };
    });
builder.Services.AddAuthorization();

// ── PART 2: CORS ─────────────────────────────────────────────────────────────
// A named policy the SPA frontend uses to call the API cross-origin. We list the
// explicit origins (local dev + a placeholder prod host) rather than
// AllowAnyOrigin() because AllowAnyOrigin() + AllowCredentials() is an illegal
// combination the CORS middleware THROWS on at startup (see README Part 2): the
// spec forbids the wildcard "*" from being echoed back together with
// Access-Control-Allow-Credentials: true, since that would let any site on the
// internet make credentialed requests. Listing origins lets us keep credentials.
const string CorsPolicy = "CareerHubFrontend";
builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins("http://localhost:3000", "https://careerhub.example.com")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        // Custom response headers a browser script can read off the response.
        // Without this list they are stripped from cross-origin reads.
        .WithExposedHeaders("X-Total-Count", "ETag", "api-supported-versions", "Retry-After")));

// Validation/constraint failures -> HTTP 400 Problem Details.
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // Scalar UI at /scalar/v1
}

// ── PART 2: CORS runs FIRST in the pipeline ──────────────────────────────────
// UseCors MUST sit before UseAuthentication/UseAuthorization so a rejected
// (401/403) cross-origin call still carries the Access-Control-* headers the
// browser needs — otherwise the browser surfaces an opaque CORS error and the
// SPA can never see the real status.
app.UseCors(CorsPolicy);

// Authentication then authorization, in that order, for every request.
app.UseAuthentication();
app.UseAuthorization();

// Apply migrations and seed sample SA data on startup (dev convenience).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CareerHubDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.SeedAsync(db);
    await SeedData.SeedDemoAccountsAsync(db); // login-ready demo accounts (idempotent)
}

app.MapControllers();

app.Run();

// Exposed so integration tests / EF tooling can reference the entry assembly.
public partial class Program;
