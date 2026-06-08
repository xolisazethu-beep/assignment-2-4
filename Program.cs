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

// Dev-only CORS so the Vite frontend (careerhub-web, served on :5173) can call
// the API from a different origin. Scoped to the local dev origin — not a
// wide-open AllowAnyOrigin — and only applied below in Development.
const string DevCorsPolicy = "CareerHubDev";
builder.Services.AddCors(options =>
    options.AddPolicy(DevCorsPolicy, policy => policy
        .WithOrigins("http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()));

// Validation/constraint failures -> HTTP 400 Problem Details.
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // Scalar UI at /scalar/v1
    app.UseCors(DevCorsPolicy);    // MUST precede auth: keeps CORS headers on 401s
}

// Authentication then authorization, in that order, for every request. Placed
// AFTER UseCors so a rejected (401/403) cross-origin call still carries the CORS
// headers the browser needs — otherwise it surfaces an opaque CORS error instead.
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
