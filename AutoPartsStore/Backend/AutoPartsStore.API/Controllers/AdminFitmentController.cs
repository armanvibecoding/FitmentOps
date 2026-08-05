using System.Data;
using System.Security.Claims;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Admin/fitment")]
[Authorize(Policy = AdminPolicyNames.Catalog)]
public sealed class AdminFitmentController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly FitmentService _fitmentService;
    private readonly AdminAuditIntentService _auditIntentService;
    private readonly AdminAuditService _auditService;
    private readonly AdminAuditIntentOptions _auditIntentOptions;
    private readonly TimeProvider _timeProvider;

    public AdminFitmentController(
        AutoPartsDbContext context,
        FitmentService fitmentService,
        AdminAuditIntentService auditIntentService,
        AdminAuditService auditService,
        AdminAuditIntentOptions auditIntentOptions,
        TimeProvider timeProvider)
    {
        _context = context;
        _fitmentService = fitmentService;
        _auditIntentService = auditIntentService;
        _auditService = auditService;
        _auditIntentOptions = auditIntentOptions;
        _timeProvider = timeProvider;
        _auditIntentOptions.Validate();
    }

    [HttpGet("quality")]
    public async Task<ActionResult<FitmentQualityDto>> GetQuality(
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiringAt = now.AddDays(30);
        var totalProducts = await _context.Products.AsNoTracking().CountAsync(cancellationToken);
        var verifiedActive = _context.ProductFitments
            .AsNoTracking()
            .Where(fitment =>
                fitment.IsVerified &&
                fitment.SourceKind != FitmentSourceKind.UnverifiedImport &&
                fitment.ValidFromUtc <= now &&
                (fitment.ValidToUtc == null || now < fitment.ValidToUtc));
        var coveredProducts = await verifiedActive
            .Select(fitment => fitment.ProductId)
            .Distinct()
            .CountAsync(cancellationToken);
        var verifiedOemProducts = await _context.ProductIdentifiers
            .AsNoTracking()
            .Where(identifier =>
                identifier.Kind == PartIdentifierKind.Oem &&
                identifier.IsVerified &&
                identifier.SourceKind != FitmentSourceKind.UnverifiedImport &&
                identifier.ValidFromUtc <= now &&
                (identifier.ValidToUtc == null || now < identifier.ValidToUtc))
            .Select(identifier => identifier.ProductId)
            .Distinct()
            .CountAsync(cancellationToken);
        var activeVerifiedCount = await verifiedActive.CountAsync(cancellationToken);
        var activeUnverifiedCount = await _context.ProductFitments
            .AsNoTracking()
            .CountAsync(
                fitment =>
                    (!fitment.IsVerified || fitment.SourceKind == FitmentSourceKind.UnverifiedImport) &&
                    fitment.ValidFromUtc <= now &&
                    (fitment.ValidToUtc == null || now < fitment.ValidToUtc),
                cancellationToken);
        var belowConfidenceThreshold = await verifiedActive.CountAsync(
            fitment =>
                (fitment.AssertionKind == FitmentAssertionKind.Exact &&
                 fitment.Confidence < FitmentConfidencePolicy.MinimumExact) ||
                (fitment.AssertionKind == FitmentAssertionKind.Compatible &&
                 fitment.Confidence < FitmentConfidencePolicy.MinimumCompatible),
            cancellationToken);
        var expired = await _context.ProductFitments
            .AsNoTracking()
            .CountAsync(fitment => fitment.ValidToUtc <= now, cancellationToken);
        var expiringSoon = await _context.ProductFitments
            .AsNoTracking()
            .CountAsync(
                fitment => fitment.ValidToUtc > now && fitment.ValidToUtc <= expiringAt,
                cancellationToken);
        var sourceCounts = await verifiedActive
            .GroupBy(fitment => fitment.SourceKind)
            .Select(group => new { SourceKind = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ToListAsync(cancellationToken);
        var sources = sourceCounts
            .Select(item => new FitmentSourceQualityDto(item.SourceKind.ToString(), item.Count))
            .ToList();

        return Ok(new FitmentQualityDto(
            totalProducts,
            activeVerifiedCount,
            activeUnverifiedCount,
            belowConfidenceThreshold,
            Math.Max(0, totalProducts - coveredProducts),
            Math.Max(0, totalProducts - verifiedOemProducts),
            expired,
            expiringSoon,
            sources,
            now));
    }

    [HttpPost("vehicles")]
    public async Task<IActionResult> UpsertVehicle(
        VehicleTreeUpsertRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAuditedAsync(
            token => _fitmentService.UpsertVehicleTreeAsync(request, token),
            result => (result.Outcome, result.Vehicle?.Id, result.Message),
            AdminAuditActions.VehicleUpserted,
            AdminAuditAggregateTypes.Vehicle,
            cancellationToken);
    }

    [HttpPost("links")]
    public async Task<IActionResult> UpsertProductFitment(
        ProductFitmentUpsertRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAuditedAsync(
            token => _fitmentService.UpsertProductFitmentAsync(request, token),
            result => (result.Outcome, result.Fitment?.Id, result.Message),
            AdminAuditActions.ProductFitmentUpserted,
            AdminAuditAggregateTypes.ProductFitment,
            cancellationToken);
    }

    [HttpPost("identifiers")]
    public async Task<IActionResult> UpsertProductIdentifier(
        ProductIdentifierUpsertRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAuditedAsync(
            token => _fitmentService.UpsertProductIdentifierAsync(request, token),
            result => (result.Outcome, result.Identifier?.Id, result.Message),
            AdminAuditActions.ProductIdentifierUpserted,
            AdminAuditAggregateTypes.ProductIdentifier,
            cancellationToken);
    }

    private IActionResult MapResult(
        FitmentWriteOutcome outcome,
        long? id,
        string? message)
    {
        var response = new { id, outcome = outcome.ToString() };
        return outcome switch
        {
            FitmentWriteOutcome.Created => StatusCode(StatusCodes.Status201Created, response),
            FitmentWriteOutcome.Replayed => Ok(response),
            FitmentWriteOutcome.InvalidRequest => BadRequest(new { message }),
            FitmentWriteOutcome.NotFound => NotFound(new { message }),
            FitmentWriteOutcome.Conflict => Conflict(new { message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private async Task<IActionResult> ExecuteAuditedAsync<TResult>(
        Func<CancellationToken, Task<TResult>> mutation,
        Func<TResult, (FitmentWriteOutcome Outcome, long? AggregateId, string? Message)> project,
        string action,
        string aggregateType,
        CancellationToken cancellationToken)
    {
        var identity = GetAuditIdentity();
        await using var transaction = await _context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        TResult result;
        (FitmentWriteOutcome Outcome, long? AggregateId, string? Message) projection;
        try
        {
            result = await mutation(cancellationToken);
            projection = project(result);
            if (projection.Outcome is not (
                    FitmentWriteOutcome.Created or
                    FitmentWriteOutcome.Replayed))
            {
                await RollbackAsync(transaction);
                return MapResult(
                    projection.Outcome,
                    projection.AggregateId,
                    projection.Message);
            }

            if (projection.AggregateId is null or <= 0)
            {
                throw new InvalidOperationException(
                    "Successful fitment mutation did not return an aggregate identity.");
            }

            var stageResult = _auditIntentService.Stage(
                new AdminAuditIntentStageRequest(
                    Guid.NewGuid(),
                    identity.ActorUserId,
                    identity.ActorRole,
                    action,
                    aggregateType,
                    projection.AggregateId.Value,
                    HttpContext.TraceIdentifier,
                    projection.Outcome == FitmentWriteOutcome.Replayed
                        ? AdminAuditOutcomes.Replayed
                        : AdminAuditOutcomes.Succeeded));
            if (stageResult.Outcome != AdminAuditIntentStageOutcome.Staged)
            {
                throw new InvalidOperationException(
                    $"Admin audit intent staging failed: {stageResult.ErrorCode}");
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }

        await TryDispatchAuditAsync(cancellationToken);
        return MapResult(
            projection.Outcome,
            projection.AggregateId,
            projection.Message);
    }

    private (int ActorUserId, string ActorRole) GetAuditIdentity()
    {
        var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorRole = User.FindFirstValue(ClaimTypes.Role);
        if (!int.TryParse(actorClaim, out var actorUserId) ||
            string.IsNullOrWhiteSpace(actorRole))
        {
            throw new InvalidOperationException(
                "Authenticated admin audit identity is missing.");
        }

        return (actorUserId, actorRole);
    }

    private async Task TryDispatchAuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _auditIntentService.DispatchBatchAsync(
                _auditService,
                _auditIntentOptions,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The committed durable intent remains available to the background worker.
        }
        catch
        {
            // Immediate dispatch is best-effort; the committed intent is the durable hand-off.
        }
    }

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the mutation/staging exception; disposing the transaction is the final guard.
        }

        _context.ChangeTracker.Clear();
    }
}

public sealed record FitmentSourceQualityDto(string SourceKind, int Count);

public sealed record FitmentQualityDto(
    int TotalProducts,
    int ActiveVerifiedFitments,
    int ActiveUnverifiedFitments,
    int BelowConfidenceThreshold,
    int ProductsWithoutVerifiedFitment,
    int ProductsWithoutVerifiedOem,
    int ExpiredFitments,
    int ExpiringWithin30Days,
    IReadOnlyList<FitmentSourceQualityDto> Sources,
    DateTime ObservedAtUtc);
