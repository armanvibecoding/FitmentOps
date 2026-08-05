using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public sealed record SalesChannelAdapterCapability(
    string ChannelCode,
    bool IsConfigured,
    bool SupportsSandbox,
    bool SupportsProduction,
    string StatusCode);

public interface ISalesChannelAdapterRegistry
{
    SalesChannelAdapterCapability GetCapability(string channelCode);
}

public sealed class DisabledSalesChannelAdapterRegistry : ISalesChannelAdapterRegistry
{
    public SalesChannelAdapterCapability GetCapability(string channelCode) =>
        new(channelCode, false, false, false, "adapter-not-configured");
}

public enum SalesChannelStateOutcome
{
    Updated,
    Replayed,
    NotFound,
    Conflict,
    ProviderUnavailable,
    InvalidRequest
}

public sealed record SalesChannelStateResult(
    SalesChannelStateOutcome Outcome,
    int? ChannelId = null,
    string? Mode = null,
    bool EffectiveEnabled = false,
    string? Message = null);

public enum ChannelListingRefreshOutcome
{
    Queued,
    Replayed,
    Blocked,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record ChannelListingRefreshResult(
    ChannelListingRefreshOutcome Outcome,
    long? ListingId = null,
    decimal? DesiredPrice = null,
    int? DesiredStock = null,
    string? Message = null);

public enum ChannelOrderImportOutcome
{
    Imported,
    Replayed,
    Conflict,
    ChannelDisabled,
    NotFound,
    InventoryUnavailable,
    InvalidRequest
}

public sealed record ChannelOrderImportLine(int ProductId, int Quantity, decimal UnitPrice);

public sealed record ChannelOrderImportCommand(
    string ChannelCode,
    string ExternalEventId,
    string ExternalOrderId,
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string ShippingAddress,
    string City,
    string PostalCode,
    string Currency,
    decimal PaidTotal,
    IReadOnlyCollection<ChannelOrderImportLine> Lines)
{
    public override string ToString() =>
        $"{nameof(ChannelOrderImportCommand)} {{ ChannelCode = {ChannelCode}, Sensitive = true }}";
}

public sealed record ChannelOrderImportResult(
    ChannelOrderImportOutcome Outcome,
    int? OrderId = null,
    string? OrderNumber = null,
    string? Message = null);

public sealed class SalesChannelService
{
    private const int MaxOrderLines = 200;
    private readonly AutoPartsDbContext _context;
    private readonly ISalesChannelAdapterRegistry _adapters;
    private readonly TimeProvider _timeProvider;

    public SalesChannelService(
        AutoPartsDbContext context,
        ISalesChannelAdapterRegistry adapters,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _adapters = adapters;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SalesChannelStateResult> UpdateStateAsync(
        int channelId,
        bool requestedEnabled,
        string mode,
        Guid concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0 || concurrencyToken == Guid.Empty)
        {
            return new(SalesChannelStateOutcome.InvalidRequest, Message: "Channel state fields are invalid.");
        }

        var channel = await _context.SalesChannels.SingleOrDefaultAsync(
            candidate => candidate.Id == channelId,
            cancellationToken);
        if (channel == null)
        {
            return new(SalesChannelStateOutcome.NotFound);
        }

        if (channel.ConcurrencyToken != concurrencyToken)
        {
            return new(SalesChannelStateOutcome.Conflict, channel.Id, channel.Mode, Message: "Channel changed; reload and retry.");
        }

        var normalizedMode = mode?.Trim() ?? string.Empty;
        if (!requestedEnabled)
        {
            normalizedMode = SalesChannelModes.Disabled;
        }
        else if (normalizedMode is not (SalesChannelModes.Sandbox or SalesChannelModes.Production))
        {
            return new(SalesChannelStateOutcome.InvalidRequest, channel.Id, channel.Mode, Message: "Enabled channels require Sandbox or Production mode.");
        }

        var capability = _adapters.GetCapability(channel.Code);
        if (requestedEnabled &&
            (!capability.IsConfigured ||
             (normalizedMode == SalesChannelModes.Sandbox && !capability.SupportsSandbox) ||
             (normalizedMode == SalesChannelModes.Production && !capability.SupportsProduction)))
        {
            return new(
                SalesChannelStateOutcome.ProviderUnavailable,
                channel.Id,
                channel.Mode,
                Message: capability.StatusCode);
        }

        if (channel.RequestedEnabled == requestedEnabled && channel.Mode == normalizedMode)
        {
            return new(SalesChannelStateOutcome.Replayed, channel.Id, channel.Mode, IsEffective(channel, capability));
        }

        channel.RequestedEnabled = requestedEnabled;
        channel.Mode = normalizedMode;
        channel.UpdatedAtUtc = UtcNow();
        channel.ConcurrencyToken = Guid.NewGuid();
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(SalesChannelStateOutcome.Conflict, channel.Id, channel.Mode, Message: "Channel changed; reload and retry.");
        }

        return new(SalesChannelStateOutcome.Updated, channel.Id, channel.Mode, IsEffective(channel, capability));
    }

    public async Task<ChannelListingRefreshResult> RefreshListingAsync(
        int channelId,
        int productId,
        string? externalListingId,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0 || productId <= 0 || externalListingId?.Length > 100)
        {
            return new(ChannelListingRefreshOutcome.InvalidRequest, Message: "Listing fields are invalid.");
        }

        var channel = await _context.SalesChannels.SingleOrDefaultAsync(
            candidate => candidate.Id == channelId,
            cancellationToken);
        var product = await _context.Products.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == productId,
            cancellationToken);
        if (channel == null || product == null)
        {
            return new(ChannelListingRefreshOutcome.NotFound);
        }

        var now = UtcNow();
        var capability = _adapters.GetCapability(channel.Code);
        var effectiveEnabled = IsEffective(channel, capability);
        var normalizedExternalId = string.IsNullOrWhiteSpace(externalListingId)
            ? null
            : externalListingId.Trim();
        var listing = await _context.ChannelListings.SingleOrDefaultAsync(
            candidate => candidate.SalesChannelId == channelId && candidate.ProductId == productId,
            cancellationToken);
        var changed = listing == null ||
            listing.DesiredPrice != product.Price ||
            listing.DesiredStock != product.Stock ||
            listing.ExternalListingId != normalizedExternalId;

        if (listing == null)
        {
            listing = new ChannelListing
            {
                SalesChannelId = channelId,
                ProductId = productId,
                ExternalListingId = normalizedExternalId,
                DesiredPrice = product.Price,
                DesiredStock = product.Stock,
                DesiredAtUtc = now
            };
            _context.ChannelListings.Add(listing);
        }
        else if (changed)
        {
            listing.ExternalListingId = normalizedExternalId;
            listing.DesiredPrice = product.Price;
            listing.DesiredStock = product.Stock;
            listing.DesiredAtUtc = now;
            listing.ConcurrencyToken = Guid.NewGuid();
        }

        if (!effectiveEnabled)
        {
            listing.Status = ChannelListingStatuses.Blocked;
            listing.LastAttemptAtUtc = now;
            listing.LastFailureCode = capability.IsConfigured
                ? "channel-disabled"
                : capability.StatusCode;
            await _context.SaveChangesAsync(cancellationToken);
            return new(
                ChannelListingRefreshOutcome.Blocked,
                listing.Id,
                listing.DesiredPrice,
                listing.DesiredStock,
                listing.LastFailureCode);
        }

        if (!changed && listing.Status == ChannelListingStatuses.Pending)
        {
            return new(ChannelListingRefreshOutcome.Replayed, listing.Id, listing.DesiredPrice, listing.DesiredStock);
        }

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        listing.Status = ChannelListingStatuses.Pending;
        listing.LastAttemptAtUtc = now;
        listing.LastFailureCode = null;
        await _context.SaveChangesAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            channelListingId = listing.Id,
            channelCode = channel.Code,
            productId = product.Id
        });
        _context.OutboxMessages.Add(new OutboxMessage
        {
            EventId = Guid.NewGuid(),
            Type = "sales-channel.listing.sync-requested",
            AggregateId = listing.Id.ToString(CultureInfo.InvariantCulture),
            Payload = payload,
            CreatedAt = now
        });
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new(ChannelListingRefreshOutcome.Queued, listing.Id, listing.DesiredPrice, listing.DesiredStock);
    }

    public async Task<ChannelListingRefreshResult> RecordListingObservationAsync(
        long listingId,
        decimal observedPrice,
        int observedStock,
        CancellationToken cancellationToken = default)
    {
        if (listingId <= 0 || observedPrice <= 0 || observedStock < 0)
        {
            return new(ChannelListingRefreshOutcome.InvalidRequest);
        }

        var listing = await _context.ChannelListings.SingleOrDefaultAsync(
            candidate => candidate.Id == listingId,
            cancellationToken);
        if (listing == null)
        {
            return new(ChannelListingRefreshOutcome.NotFound);
        }

        listing.ObservedPrice = observedPrice;
        listing.ObservedStock = observedStock;
        listing.LastSuccessAtUtc = UtcNow();
        listing.Status = listing.DesiredPrice == observedPrice && listing.DesiredStock == observedStock
            ? ChannelListingStatuses.Active
            : ChannelListingStatuses.Error;
        listing.LastFailureCode = listing.Status == ChannelListingStatuses.Error
            ? "stock-price-drift"
            : null;
        listing.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        return listing.Status == ChannelListingStatuses.Active
            ? new(ChannelListingRefreshOutcome.Queued, listing.Id, listing.DesiredPrice, listing.DesiredStock)
            : new(ChannelListingRefreshOutcome.Conflict, listing.Id, listing.DesiredPrice, listing.DesiredStock, listing.LastFailureCode);
    }

    public async Task<ChannelOrderImportResult> ImportOrderAsync(
        ChannelOrderImportCommand command,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateImport(command);
        if (validation != null)
        {
            return new(ChannelOrderImportOutcome.InvalidRequest, Message: validation);
        }

        if (_context.Database.CurrentTransaction != null)
        {
            return new(ChannelOrderImportOutcome.InvalidRequest, Message: "Channel order import requires its own transaction boundary.");
        }

        var channelCode = command.ChannelCode.Trim();
        var externalEventId = command.ExternalEventId.Trim();
        var externalOrderId = command.ExternalOrderId.Trim();
        var lines = command.Lines
            .GroupBy(line => line.ProductId)
            .Select(group =>
            {
                var prices = group.Select(line => line.UnitPrice).Distinct().ToArray();
                return new NormalizedLine(
                    group.Key,
                    group.Sum(line => line.Quantity),
                    prices.Length == 1 ? prices[0] : -1m);
            })
            .OrderBy(line => line.ProductId)
            .ToArray();
        if (lines.Any(line => line.UnitPrice <= 0 || line.Quantity <= 0) ||
            lines.Sum(line => line.Quantity * line.UnitPrice) != command.PaidTotal)
        {
            return new(ChannelOrderImportOutcome.InvalidRequest, Message: "Channel order totals or duplicate line prices are invalid.");
        }

        var payloadHash = ComputeImportHash(command, lines);
        IDbContextTransaction? transaction = null;
        try
        {
            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            }

            var channel = await _context.SalesChannels.SingleOrDefaultAsync(
                candidate => candidate.Code == channelCode,
                cancellationToken);
            if (channel == null)
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new ChannelOrderImportResult(ChannelOrderImportOutcome.NotFound),
                    cancellationToken);
            }

            var capability = _adapters.GetCapability(channel.Code);
            if (!IsEffective(channel, capability))
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new ChannelOrderImportResult(
                        ChannelOrderImportOutcome.ChannelDisabled,
                        Message: capability.IsConfigured ? "channel-disabled" : capability.StatusCode),
                    cancellationToken);
            }

            var existingEvent = await _context.ChannelInboxEvents
                .AsNoTracking()
                .Include(candidate => candidate.ChannelOrderLink)
                .ThenInclude(link => link!.Order)
                .SingleOrDefaultAsync(
                    candidate => candidate.SalesChannelId == channel.Id &&
                                 candidate.ExternalEventId == externalEventId,
                    cancellationToken);
            if (existingEvent != null)
            {
                var outcome = existingEvent.PayloadHash == payloadHash && existingEvent.ChannelOrderLink?.Order != null
                    ? new ChannelOrderImportResult(
                        ChannelOrderImportOutcome.Replayed,
                        existingEvent.ChannelOrderLink.OrderId,
                        existingEvent.ChannelOrderLink.Order.OrderNumber)
                    : new ChannelOrderImportResult(ChannelOrderImportOutcome.Conflict, Message: "Event replay payload differs.");
                return await CommitAndReturnAsync(transaction, outcome, cancellationToken);
            }

            var existingOrder = await _context.ChannelOrderLinks
                .Include(candidate => candidate.Order)
                .SingleOrDefaultAsync(
                    candidate => candidate.SalesChannelId == channel.Id &&
                                 candidate.ExternalOrderId == externalOrderId,
                    cancellationToken);
            if (existingOrder != null)
            {
                var payloadMatches = await _context.ChannelInboxEvents.AsNoTracking().AnyAsync(
                    inbox => inbox.ChannelOrderLinkId == existingOrder.Id &&
                             inbox.PayloadHash == payloadHash,
                    cancellationToken);
                if (!payloadMatches)
                {
                    return await CommitAndReturnAsync(
                        transaction,
                        new ChannelOrderImportResult(
                            ChannelOrderImportOutcome.Conflict,
                            existingOrder.OrderId,
                            existingOrder.Order.OrderNumber,
                            "Existing channel order payload differs."),
                        cancellationToken);
                }

                _context.ChannelInboxEvents.Add(new ChannelInboxEvent
                {
                    SalesChannelId = channel.Id,
                    ExternalEventId = externalEventId,
                    PayloadHash = payloadHash,
                    ChannelOrderLinkId = existingOrder.Id,
                    Status = ChannelInboxStatuses.Processed,
                    ReceivedAtUtc = UtcNow(),
                    ProcessedAtUtc = UtcNow()
                });
                await _context.SaveChangesAsync(cancellationToken);
                return await CommitAndReturnAsync(
                    transaction,
                    new ChannelOrderImportResult(
                        ChannelOrderImportOutcome.Replayed,
                        existingOrder.OrderId,
                        existingOrder.Order.OrderNumber),
                    cancellationToken);
            }

            var productIds = lines.Select(line => line.ProductId).ToArray();
            var products = await _context.Products
                .Where(product => productIds.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id, cancellationToken);
            if (products.Count != productIds.Length)
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new ChannelOrderImportResult(ChannelOrderImportOutcome.NotFound, Message: "Channel order product was not found."),
                    cancellationToken);
            }

            if (lines.Any(line => products[line.ProductId].Stock < line.Quantity))
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new ChannelOrderImportResult(ChannelOrderImportOutcome.InventoryUnavailable),
                    cancellationToken);
            }

            foreach (var line in lines)
            {
                products[line.ProductId].Stock -= line.Quantity;
            }

            var now = UtcNow();
            var stableHash = StableHash(externalOrderId);
            var order = new Order
            {
                OrderNumber = $"CH-{channel.Id}-{stableHash[..20]}",
                CheckoutIdempotencyKey = $"channel:{channel.Id}:{stableHash[..40]}",
                CustomerName = command.CustomerName.Trim(),
                CustomerEmail = command.CustomerEmail.Trim().ToLowerInvariant(),
                CustomerPhone = command.CustomerPhone.Trim(),
                ShippingAddress = command.ShippingAddress.Trim(),
                City = command.City.Trim(),
                PostalCode = command.PostalCode.Trim(),
                TotalAmount = command.PaidTotal,
                Status = OrderStatuses.Processing,
                OrderDate = now,
                OrderItems = lines.Select(line => new OrderItem
                {
                    ProductId = line.ProductId,
                    Quantity = line.Quantity,
                    Price = line.UnitPrice
                }).ToList(),
                Payment = new Payment
                {
                    Provider = channel.Code,
                    Method = PaymentMethods.Marketplace,
                    Status = PaymentStatuses.Paid,
                    Amount = command.PaidTotal,
                    Currency = "TRY",
                    IdempotencyKey = $"channel-payment:{channel.Id}:{stableHash[..32]}",
                    ProviderPaymentId = externalOrderId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    PaidAt = now,
                    ConcurrencyToken = Guid.NewGuid()
                }
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync(cancellationToken);

            var link = new ChannelOrderLink
            {
                SalesChannelId = channel.Id,
                ExternalOrderId = externalOrderId,
                OrderId = order.Id,
                CreatedAtUtc = now
            };
            _context.ChannelOrderLinks.Add(link);
            await _context.SaveChangesAsync(cancellationToken);
            _context.ChannelInboxEvents.Add(new ChannelInboxEvent
            {
                SalesChannelId = channel.Id,
                ExternalEventId = externalEventId,
                PayloadHash = payloadHash,
                ChannelOrderLinkId = link.Id,
                Status = ChannelInboxStatuses.Processed,
                ReceivedAtUtc = now,
                ProcessedAtUtc = now
            });
            _context.OutboxMessages.Add(new OutboxMessage
            {
                EventId = Guid.NewGuid(),
                Type = "sales-channel.order.imported",
                AggregateId = link.Id.ToString(CultureInfo.InvariantCulture),
                Payload = JsonSerializer.Serialize(new { channelOrderLinkId = link.Id, orderId = order.Id }),
                CreatedAt = now
            });
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new(ChannelOrderImportOutcome.Imported, order.Id, order.OrderNumber);
        }
        catch (DbUpdateException)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            _context.ChangeTracker.Clear();
            var resolved = await ResolveConcurrentImportAsync(
                channelCode,
                externalEventId,
                externalOrderId,
                payloadHash,
                cancellationToken);
            if (resolved != null)
            {
                return resolved;
            }
            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<ChannelOrderImportResult?> ResolveConcurrentImportAsync(
        string channelCode,
        string externalEventId,
        string externalOrderId,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var channelId = await _context.SalesChannels
            .Where(channel => channel.Code == channelCode)
            .Select(channel => (int?)channel.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (!channelId.HasValue)
        {
            return null;
        }

        var inbox = await _context.ChannelInboxEvents
            .AsNoTracking()
            .Include(candidate => candidate.ChannelOrderLink)
            .ThenInclude(link => link!.Order)
            .SingleOrDefaultAsync(
                candidate => candidate.SalesChannelId == channelId &&
                             candidate.ExternalEventId == externalEventId,
                cancellationToken);
        if (inbox != null)
        {
            return inbox.PayloadHash == payloadHash && inbox.ChannelOrderLink?.Order != null
                ? new(ChannelOrderImportOutcome.Replayed, inbox.ChannelOrderLink.OrderId, inbox.ChannelOrderLink.Order.OrderNumber)
                : new(ChannelOrderImportOutcome.Conflict, Message: "Event replay payload differs.");
        }

        var link = await _context.ChannelOrderLinks
            .AsNoTracking()
            .Include(candidate => candidate.Order)
            .SingleOrDefaultAsync(
                candidate => candidate.SalesChannelId == channelId &&
                             candidate.ExternalOrderId == externalOrderId,
                cancellationToken);
        return link == null
            ? null
            : new(ChannelOrderImportOutcome.Replayed, link.OrderId, link.Order.OrderNumber);
    }

    private static string? ValidateImport(ChannelOrderImportCommand command)
    {
        if (command == null ||
            !SalesChannelCodes.All.Contains(command.ChannelCode?.Trim() ?? string.Empty) ||
            string.IsNullOrWhiteSpace(command.ExternalEventId) || command.ExternalEventId.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(command.ExternalOrderId) || command.ExternalOrderId.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(command.CustomerName) || command.CustomerName.Trim().Length is < 2 or > 200 ||
            string.IsNullOrWhiteSpace(command.CustomerEmail) || command.CustomerEmail.Trim().Length > 200 ||
            string.IsNullOrWhiteSpace(command.CustomerPhone) || command.CustomerPhone.Trim().Length > 20 ||
            string.IsNullOrWhiteSpace(command.ShippingAddress) || command.ShippingAddress.Trim().Length is < 10 or > 500 ||
            string.IsNullOrWhiteSpace(command.City) || command.City.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(command.PostalCode) || command.PostalCode.Trim().Length > 10 ||
            !string.Equals(command.Currency?.Trim(), "TRY", StringComparison.Ordinal) ||
            command.PaidTotal <= 0 ||
            command.Lines is not { Count: > 0 and <= MaxOrderLines } ||
            command.Lines.Any(line => line.ProductId <= 0 || line.Quantity <= 0 || line.UnitPrice <= 0))
        {
            return "Channel order fields are invalid.";
        }

        return null;
    }

    private static string ComputeImportHash(
        ChannelOrderImportCommand command,
        IReadOnlyCollection<NormalizedLine> lines)
    {
        var canonical = new StringBuilder()
            .Append(command.ChannelCode.Trim()).Append('|')
            .Append(command.ExternalOrderId.Trim()).Append('|')
            .Append(command.CustomerName.Trim()).Append('|')
            .Append(command.CustomerEmail.Trim().ToLowerInvariant()).Append('|')
            .Append(command.CustomerPhone.Trim()).Append('|')
            .Append(command.ShippingAddress.Trim()).Append('|')
            .Append(command.City.Trim()).Append('|')
            .Append(command.PostalCode.Trim()).Append('|')
            .Append(command.Currency.Trim()).Append('|')
            .Append(command.PaidTotal.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var line in lines)
        {
            canonical.Append('|')
                .Append(line.ProductId).Append(':')
                .Append(line.Quantity).Append(':')
                .Append(line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture));
        }
        return StableHash(canonical.ToString());
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsEffective(
        SalesChannel channel,
        SalesChannelAdapterCapability capability) =>
        channel.RequestedEnabled &&
        channel.Mode != SalesChannelModes.Disabled &&
        capability.IsConfigured &&
        (channel.Mode != SalesChannelModes.Sandbox || capability.SupportsSandbox) &&
        (channel.Mode != SalesChannelModes.Production || capability.SupportsProduction);

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken cancellationToken) =>
        _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

    private static async Task<ChannelOrderImportResult> RollbackAndReturnAsync(
        IDbContextTransaction? transaction,
        ChannelOrderImportResult result,
        CancellationToken cancellationToken)
    {
        if (transaction != null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        return result;
    }

    private static async Task<ChannelOrderImportResult> CommitAndReturnAsync(
        IDbContextTransaction? transaction,
        ChannelOrderImportResult result,
        CancellationToken cancellationToken)
    {
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return result;
    }

    private sealed record NormalizedLine(int ProductId, int Quantity, decimal UnitPrice);
}
