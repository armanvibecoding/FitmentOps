using System.ComponentModel.DataAnnotations;
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
[Route("api/admin/channels")]
[Authorize]
public sealed class AdminChannelsController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly SalesChannelService _channelService;
    private readonly ISalesChannelAdapterRegistry _adapters;
    private readonly AdminAuditIntentService _auditIntentService;
    private readonly AdminAuditService _auditService;
    private readonly AdminAuditIntentOptions _auditOptions;
    private readonly ILogger<AdminChannelsController> _logger;

    public AdminChannelsController(
        AutoPartsDbContext context,
        SalesChannelService channelService,
        ISalesChannelAdapterRegistry adapters,
        AdminAuditIntentService auditIntentService,
        AdminAuditService auditService,
        AdminAuditIntentOptions auditOptions,
        ILogger<AdminChannelsController> logger)
    {
        _context = context;
        _channelService = channelService;
        _adapters = adapters;
        _auditIntentService = auditIntentService;
        _auditService = auditService;
        _auditOptions = auditOptions;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(Policy = AdminPolicyNames.AdminAccess)]
    public async Task<IActionResult> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _context.SalesChannels
            .AsNoTracking()
            .OrderBy(channel => channel.Id)
            .Select(channel => new
            {
                channel.Id,
                channel.Code,
                channel.DisplayName,
                channel.RequestedEnabled,
                channel.Mode,
                channel.UpdatedAtUtc,
                channel.ConcurrencyToken
            })
            .ToListAsync(cancellationToken);
        var channelIds = channels.Select(channel => channel.Id).ToArray();
        var listings = await _context.ChannelListings
            .AsNoTracking()
            .Where(listing => channelIds.Contains(listing.SalesChannelId))
            .OrderByDescending(listing => listing.DesiredAtUtc)
            .Take(1_000)
            .Select(listing => new
            {
                listing.SalesChannelId,
                listing.Id,
                listing.ProductId,
                Product = listing.Product.Name,
                listing.ExternalListingId,
                listing.Status,
                listing.DesiredPrice,
                listing.DesiredStock,
                listing.ObservedPrice,
                listing.ObservedStock,
                listing.DesiredAtUtc,
                listing.LastSuccessAtUtc,
                listing.LastFailureCode,
                HasDrift = listing.ObservedPrice != listing.DesiredPrice ||
                           listing.ObservedStock != listing.DesiredStock
            })
            .ToListAsync(cancellationToken);
        var inboxEvents = await _context.ChannelInboxEvents
            .AsNoTracking()
            .Where(inbox => channelIds.Contains(inbox.SalesChannelId))
            .OrderByDescending(inbox => inbox.ReceivedAtUtc)
            .Take(400)
            .Select(inbox => new
            {
                inbox.SalesChannelId,
                inbox.Id,
                inbox.Status,
                inbox.FailureCode,
                inbox.ReceivedAtUtc,
                inbox.ProcessedAtUtc,
                OrderId = inbox.ChannelOrderLink == null
                    ? null
                    : (int?)inbox.ChannelOrderLink.OrderId,
                OrderNumber = inbox.ChannelOrderLink == null
                    ? null
                    : inbox.ChannelOrderLink.Order.OrderNumber
            })
            .ToListAsync(cancellationToken);

        return Ok(channels.Select(channel =>
        {
            var capability = _adapters.GetCapability(channel.Code);
            var effectiveEnabled = channel.RequestedEnabled &&
                channel.Mode != SalesChannelModes.Disabled &&
                capability.IsConfigured &&
                (channel.Mode != SalesChannelModes.Sandbox || capability.SupportsSandbox) &&
                (channel.Mode != SalesChannelModes.Production || capability.SupportsProduction);
            return new
            {
                channel.Id,
                channel.Code,
                channel.DisplayName,
                channel.RequestedEnabled,
                channel.Mode,
                channel.UpdatedAtUtc,
                channel.ConcurrencyToken,
                Adapter = new
                {
                    capability.IsConfigured,
                    capability.SupportsSandbox,
                    capability.SupportsProduction,
                    capability.StatusCode,
                    EffectiveEnabled = effectiveEnabled
                },
                Listings = listings.Where(listing => listing.SalesChannelId == channel.Id),
                Inbox = inboxEvents.Where(inbox => inbox.SalesChannelId == channel.Id)
            };
        }));
    }

    [HttpPut("{id:int}/state")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public Task<IActionResult> UpdateChannelState(
        int id,
        UpdateSalesChannelStateDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var result = await _channelService.UpdateStateAsync(
                    id,
                    dto.RequestedEnabled,
                    dto.Mode,
                    dto.ConcurrencyToken,
                    token);
                var response = new
                {
                    result.ChannelId,
                    result.Mode,
                    result.EffectiveEnabled,
                    result.Message
                };
                return result.Outcome switch
                {
                    SalesChannelStateOutcome.Updated => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.SalesChannelStateChanged,
                        AdminAuditAggregateTypes.SalesChannel,
                        id,
                        AdminAuditOutcomes.Succeeded),
                    SalesChannelStateOutcome.Replayed => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.SalesChannelStateChanged,
                        AdminAuditAggregateTypes.SalesChannel,
                        id,
                        AdminAuditOutcomes.Replayed),
                    SalesChannelStateOutcome.NotFound => Mutation.NoAudit(NotFound(response)),
                    SalesChannelStateOutcome.Conflict => Mutation.NoAudit(Conflict(response)),
                    SalesChannelStateOutcome.ProviderUnavailable => Mutation.NoAudit(StatusCode(StatusCodes.Status503ServiceUnavailable, response)),
                    SalesChannelStateOutcome.InvalidRequest => Mutation.NoAudit(BadRequest(response)),
                    _ => Mutation.NoAudit(StatusCode(StatusCodes.Status500InternalServerError))
                };
            },
            cancellationToken);

    [HttpPost("{id:int}/listings/{productId:int}/refresh")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public Task<IActionResult> RefreshListing(
        int id,
        int productId,
        RefreshChannelListingDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var result = await _channelService.RefreshListingAsync(
                    id,
                    productId,
                    dto.ExternalListingId,
                    token);
                var response = new
                {
                    result.ListingId,
                    result.DesiredPrice,
                    result.DesiredStock,
                    result.Message
                };
                return result.Outcome switch
                {
                    ChannelListingRefreshOutcome.Queued => Mutation.Audited(
                        Accepted(response),
                        AdminAuditActions.ChannelListingSyncRequested,
                        AdminAuditAggregateTypes.ChannelListing,
                        result.ListingId!.Value,
                        AdminAuditOutcomes.Succeeded),
                    ChannelListingRefreshOutcome.Replayed => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.ChannelListingSyncRequested,
                        AdminAuditAggregateTypes.ChannelListing,
                        result.ListingId!.Value,
                        AdminAuditOutcomes.Replayed),
                    ChannelListingRefreshOutcome.Blocked => Mutation.Audited(
                        StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                        AdminAuditActions.ChannelListingSyncRequested,
                        AdminAuditAggregateTypes.ChannelListing,
                        result.ListingId!.Value,
                        AdminAuditOutcomes.Rejected),
                    ChannelListingRefreshOutcome.NotFound => Mutation.NoAudit(NotFound(response)),
                    ChannelListingRefreshOutcome.Conflict => Mutation.NoAudit(Conflict(response)),
                    ChannelListingRefreshOutcome.InvalidRequest => Mutation.NoAudit(BadRequest(response)),
                    _ => Mutation.NoAudit(StatusCode(StatusCodes.Status500InternalServerError))
                };
            },
            cancellationToken);

    private async Task<IActionResult> ExecuteAuditedAsync(
        Func<CancellationToken, Task<Mutation>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            var mutation = await operation(cancellationToken);
            if (!mutation.ShouldAudit)
            {
                return mutation.Result;
            }

            var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var actorRole = User.FindFirstValue(ClaimTypes.Role);
            if (!int.TryParse(actorClaim, out var actorUserId) || string.IsNullOrWhiteSpace(actorRole))
            {
                throw new InvalidOperationException("Authenticated admin audit identity is missing.");
            }

            var stage = _auditIntentService.Stage(new AdminAuditIntentStageRequest(
                Guid.NewGuid(),
                actorUserId,
                actorRole,
                mutation.Action!,
                mutation.AggregateType!,
                mutation.AggregateId,
                HttpContext.TraceIdentifier,
                mutation.Outcome!));
            if (stage.Outcome != AdminAuditIntentStageOutcome.Staged)
            {
                throw new InvalidOperationException($"Admin audit intent staging failed: {stage.ErrorCode}");
            }

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            await DispatchAuditBestEffortAsync(cancellationToken);
            return mutation.Result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "The record changed; reload and retry." });
        }
    }

    private async Task DispatchAuditBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _auditIntentService.DispatchBatchAsync(_auditService, _auditOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Sales channel admin audit dispatch deferred. ExceptionType: {ExceptionType}",
                exception.GetType().Name);
        }
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken cancellationToken) =>
        _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

    private sealed record Mutation(
        IActionResult Result,
        bool ShouldAudit,
        string? Action = null,
        string? AggregateType = null,
        long AggregateId = 0,
        string? Outcome = null)
    {
        public static Mutation NoAudit(IActionResult result) => new(result, false);

        public static Mutation Audited(
            IActionResult result,
            string action,
            string aggregateType,
            long aggregateId,
            string outcome) =>
            new(result, true, action, aggregateType, aggregateId, outcome);
    }
}

public sealed class UpdateSalesChannelStateDto
{
    public bool RequestedEnabled { get; set; }

    [Required, StringLength(20)]
    public string Mode { get; set; } = SalesChannelModes.Disabled;

    public Guid ConcurrencyToken { get; set; }
}

public sealed class RefreshChannelListingDto
{
    [StringLength(100)]
    public string? ExternalListingId { get; set; }
}
