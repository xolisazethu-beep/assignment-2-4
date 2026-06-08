using CareerHub.Api.DTOs;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

[ApiController]
[Route("api/companies")]
public class CompaniesController(ICompanyService companies) : ControllerBase
{
    /// <summary>All employers, with each one's active-listing count.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<CompanyResponse>> GetAll(CancellationToken ct)
        => await companies.GetAllAsync(ct);
}
