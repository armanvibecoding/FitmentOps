using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Fitment")]
[AllowAnonymous]
public sealed class FitmentController : ControllerBase
{
    private const int MaxTreeItems = 500;
    private readonly AutoPartsDbContext _context;
    private readonly FitmentService _fitmentService;

    public FitmentController(AutoPartsDbContext context, FitmentService fitmentService)
    {
        _context = context;
        _fitmentService = fitmentService;
    }

    [HttpGet("vehicles/makes")]
    public async Task<ActionResult<IEnumerable<VehicleOptionDto>>> GetMakes(
        CancellationToken cancellationToken) =>
        Ok(await _context.VehicleMakes
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Take(MaxTreeItems)
            .Select(item => new VehicleOptionDto(item.Id, item.Name))
            .ToListAsync(cancellationToken));

    [HttpGet("vehicles/models")]
    public async Task<ActionResult<IEnumerable<VehicleOptionDto>>> GetModels(
        [FromQuery] int makeId,
        CancellationToken cancellationToken)
    {
        if (makeId <= 0) return BadRequest(new { message = "makeId must be positive." });
        return Ok(await _context.VehicleModels
            .AsNoTracking()
            .Where(item => item.MakeId == makeId)
            .OrderBy(item => item.Name)
            .Take(MaxTreeItems)
            .Select(item => new VehicleOptionDto(item.Id, item.Name))
            .ToListAsync(cancellationToken));
    }

    [HttpGet("vehicles/generations")]
    public async Task<ActionResult<IEnumerable<VehicleGenerationOptionDto>>> GetGenerations(
        [FromQuery] int modelId,
        CancellationToken cancellationToken)
    {
        if (modelId <= 0) return BadRequest(new { message = "modelId must be positive." });
        return Ok(await _context.VehicleGenerations
            .AsNoTracking()
            .Where(item => item.ModelId == modelId)
            .OrderBy(item => item.ProductionStartYear)
            .ThenBy(item => item.Name)
            .Take(MaxTreeItems)
            .Select(item => new VehicleGenerationOptionDto(
                item.Id,
                item.Name,
                item.ProductionStartYear,
                item.ProductionEndYear))
            .ToListAsync(cancellationToken));
    }

    [HttpGet("vehicles/engines")]
    public async Task<ActionResult<IEnumerable<VehicleEngineOptionDto>>> GetEngines(
        [FromQuery] int generationId,
        CancellationToken cancellationToken)
    {
        if (generationId <= 0) return BadRequest(new { message = "generationId must be positive." });
        return Ok(await _context.VehicleEngines
            .AsNoTracking()
            .Where(item => item.GenerationId == generationId)
            .OrderBy(item => item.Name)
            .Take(MaxTreeItems)
            .Select(item => new VehicleEngineOptionDto(
                item.Id,
                item.Name,
                item.EngineCode,
                item.FuelType,
                item.DisplacementCc,
                item.PowerKw))
            .ToListAsync(cancellationToken));
    }

    [HttpGet("vehicles/configurations")]
    public async Task<ActionResult<IEnumerable<VehicleConfigurationOptionDto>>> GetConfigurations(
        [FromQuery] int engineId,
        CancellationToken cancellationToken)
    {
        if (engineId <= 0) return BadRequest(new { message = "engineId must be positive." });
        return Ok(await _context.Vehicles
            .AsNoTracking()
            .Where(item => item.EngineId == engineId)
            .OrderBy(item => item.DisplayName)
            .Take(MaxTreeItems)
            .Select(item => new VehicleConfigurationOptionDto(
                item.Id,
                item.DisplayName,
                item.BodyStyle,
                item.Transmission,
                item.DriveType,
                item.Market,
                item.ProductionStartYear,
                item.ProductionEndYear))
            .ToListAsync(cancellationToken));
    }

    [HttpGet("check")]
    public async Task<ActionResult<PublicFitmentCheckDto>> Check(
        [FromQuery] int productId,
        [FromQuery] int vehicleId,
        [FromQuery] DateTime? effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        var effectiveAt = effectiveAtUtc ?? DateTime.UtcNow;
        var result = await _fitmentService.CheckAsync(
            new FitmentCheckQuery(productId, vehicleId, effectiveAt),
            cancellationToken);
        return Ok(new PublicFitmentCheckDto(
            result.Match.ToString(),
            result.IsVerified,
            result.Confidence,
            FitmentConfidencePolicy.Band(result.Confidence),
            result.SourceName,
            result.ValidFromUtc,
            result.ValidToUtc,
            result.Message));
    }

    [HttpGet("products/{productId:int}")]
    public async Task<ActionResult<PublicFitmentPageDto>> GetForProduct(
        int productId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] DateTime? effectiveAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var page = await _fitmentService.QueryAsync(
            new FitmentReadQuery(
                productId,
                null,
                effectiveAtUtc ?? DateTime.UtcNow,
                offset,
                limit,
                VerifiedOnly: true),
            cancellationToken);
        if (page.ValidationError != null)
        {
            return BadRequest(new { message = page.ValidationError });
        }

        return Ok(new PublicFitmentPageDto(
            page.Items.Select(item => new PublicFitmentItemDto(
                item.VehicleId,
                item.VehicleName,
                item.MakeName,
                item.ModelName,
                item.GenerationName,
                item.EngineName,
                item.AssertionKind.ToString(),
                item.Confidence,
                FitmentConfidencePolicy.Band(item.Confidence),
                item.SourceName,
                item.ValidFromUtc,
                item.ValidToUtc)).ToList(),
            page.Offset,
            page.Limit,
            page.HasMore));
    }

    [HttpGet("identifiers/{value}")]
    public async Task<ActionResult<IEnumerable<PublicIdentifierMatchDto>>> FindByIdentifier(
        string value,
        [FromQuery] PartIdentifierKind? kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 ||
            kind.HasValue && !Enum.IsDefined(kind.Value))
        {
            return BadRequest(new { message = "A valid identifier and optional kind are required." });
        }

        var normalized = FitmentService.NormalizePartIdentifier(value);
        if (normalized.Length == 0)
        {
            return BadRequest(new { message = "Identifier contains no searchable characters." });
        }

        var now = DateTime.UtcNow;
        var query = _context.ProductIdentifiers
            .AsNoTracking()
            .Where(item =>
                item.NormalizedValue == normalized &&
                item.IsVerified &&
                item.SourceKind != FitmentSourceKind.UnverifiedImport &&
                item.ValidFromUtc <= now &&
                (item.ValidToUtc == null || now < item.ValidToUtc));
        if (kind.HasValue)
        {
            query = query.Where(item => item.Kind == kind.Value);
        }

        return Ok(await query
            .OrderBy(item => item.ProductId)
            .Take(50)
            .Select(item => new PublicIdentifierMatchDto(
                item.ProductId,
                item.Product.Name,
                item.Kind.ToString(),
                item.SchemeAuthority,
                item.Value,
                item.SourceName))
            .ToListAsync(cancellationToken));
    }
}

public sealed record VehicleOptionDto(int Id, string Name);
public sealed record VehicleGenerationOptionDto(int Id, string Name, int? StartYear, int? EndYear);
public sealed record VehicleEngineOptionDto(
    int Id,
    string Name,
    string? EngineCode,
    string? FuelType,
    int? DisplacementCc,
    decimal? PowerKw);
public sealed record VehicleConfigurationOptionDto(
    int Id,
    string Name,
    string? BodyStyle,
    string? Transmission,
    string? DriveType,
    string? Market,
    int? StartYear,
    int? EndYear);
public sealed record PublicFitmentCheckDto(
    string Match,
    bool IsVerified,
    decimal? Confidence,
    string ConfidenceBand,
    string? SourceName,
    DateTime? ValidFromUtc,
    DateTime? ValidToUtc,
    string? Message);
public sealed record PublicFitmentItemDto(
    int VehicleId,
    string VehicleName,
    string MakeName,
    string ModelName,
    string GenerationName,
    string EngineName,
    string Match,
    decimal Confidence,
    string ConfidenceBand,
    string SourceName,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc);
public sealed record PublicFitmentPageDto(
    IReadOnlyList<PublicFitmentItemDto> Items,
    int Offset,
    int Limit,
    bool HasMore);
public sealed record PublicIdentifierMatchDto(
    int ProductId,
    string ProductName,
    string Kind,
    string SchemeAuthority,
    string Value,
    string SourceName);
