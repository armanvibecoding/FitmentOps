using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum SupplierOfferRegistrationOutcome
{
    Registered,
    Replayed,
    Conflict,
    NotFound,
    InvalidRequest
}

public enum SupplierSourcingOutcome
{
    Selected,
    InsufficientSupply,
    InvalidRequest
}

public sealed record SupplierOfferCommand(
    long SupplierId,
    string ExternalOfferId,
    int ProductId,
    string OemNumber,
    string Currency,
    decimal UnitCost,
    decimal ShippingCost,
    int AvailableQuantity,
    int LeadTimeDays,
    int MinimumOrderQuantity,
    DateTime ValidUntilUtc,
    bool CanDropship,
    bool CanSupplyWarehouse);

public sealed record SupplierOfferRegistrationResult(
    SupplierOfferRegistrationOutcome Outcome,
    long? OfferId = null,
    string? Message = null);

public sealed record SupplierSourcingRequest(
    int ProductId,
    int Quantity,
    string Currency,
    bool AllowSplit = false,
    bool RequireDropship = false,
    string? OemNumber = null);

public sealed record SupplierAllocation(
    long OfferId,
    long SupplierId,
    int Quantity,
    decimal UnitCost,
    decimal ShippingCost,
    int LeadTimeDays);

public sealed record SupplierSourcingResult(
    SupplierSourcingOutcome Outcome,
    IReadOnlyList<SupplierAllocation> Allocations,
    decimal TotalLandedCost,
    string? Message = null);

public sealed class SupplierSourcingService
{
    private const int MaxExternalOfferIdLength = 100;
    private const int MaxOemNumberLength = 80;
    private const int MaxAllocations = 10;
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public SupplierSourcingService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SupplierOfferRegistrationResult> RegisterOfferAsync(
        SupplierOfferCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(command);
        if (normalized == null)
        {
            return new SupplierOfferRegistrationResult(
                SupplierOfferRegistrationOutcome.InvalidRequest,
                Message: "Supplier offer fields are invalid.");
        }

        if (normalized.ValidUntilUtc <= _timeProvider.GetUtcNow().UtcDateTime)
        {
            return new SupplierOfferRegistrationResult(
                SupplierOfferRegistrationOutcome.InvalidRequest,
                Message: "Supplier offer validity must be in the future.");
        }

        await RegistrationGate.WaitAsync(cancellationToken);
        try
        {
            var supplierExists = await _context.Set<Supplier>()
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == normalized.SupplierId, cancellationToken);
            var productExists = await _context.Products
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == normalized.ProductId, cancellationToken);
            if (!supplierExists || !productExists)
            {
                return new SupplierOfferRegistrationResult(
                    SupplierOfferRegistrationOutcome.NotFound);
            }

            var payloadHash = ComputePayloadHash(normalized);
            var existing = await FindExistingAsync(
                normalized.SupplierId,
                normalized.ExternalOfferId,
                cancellationToken);
            if (existing != null)
            {
                return ResolveExisting(existing, payloadHash);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var offer = new SupplierOffer
            {
                SupplierId = normalized.SupplierId,
                ExternalOfferId = normalized.ExternalOfferId,
                ProductId = normalized.ProductId,
                OemNumber = normalized.OemNumber,
                Currency = normalized.Currency,
                UnitCost = normalized.UnitCost,
                ShippingCost = normalized.ShippingCost,
                AvailableQuantity = normalized.AvailableQuantity,
                LeadTimeDays = normalized.LeadTimeDays,
                MinimumOrderQuantity = normalized.MinimumOrderQuantity,
                ValidUntilUtc = normalized.ValidUntilUtc,
                CanDropship = normalized.CanDropship,
                CanSupplyWarehouse = normalized.CanSupplyWarehouse,
                IsActive = true,
                PayloadHash = payloadHash,
                CreatedAtUtc = now,
                ConcurrencyToken = Guid.NewGuid()
            };
            _context.Set<SupplierOffer>().Add(offer);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return new SupplierOfferRegistrationResult(
                    SupplierOfferRegistrationOutcome.Registered,
                    offer.Id);
            }
            catch (DbUpdateException)
            {
                _context.Entry(offer).State = EntityState.Detached;
                existing = await FindExistingAsync(
                    normalized.SupplierId,
                    normalized.ExternalOfferId,
                    cancellationToken);
                if (existing != null)
                {
                    return ResolveExisting(existing, payloadHash);
                }

                throw;
            }
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    public async Task<SupplierSourcingResult> SelectAsync(
        SupplierSourcingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProductId <= 0 ||
            request.Quantity is < 1 or > 100_000 ||
            !TryNormalizeCurrency(request.Currency, out var currency) ||
            !TryNormalizeOem(request.OemNumber, optional: true, out var oemNumber))
        {
            return InvalidSourcing();
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var query = _context.Set<SupplierOffer>()
            .AsNoTracking()
            .Include(offer => offer.Supplier)
            .Where(offer =>
                offer.ProductId == request.ProductId &&
                offer.Currency == currency &&
                offer.IsActive &&
                offer.ValidUntilUtc > now &&
                offer.AvailableQuantity > 0 &&
                offer.Supplier.IsActive &&
                offer.Supplier.HealthStatus != SupplierHealthStatuses.Unhealthy &&
                (!request.RequireDropship || offer.CanDropship));
        if (oemNumber != null)
        {
            query = query.Where(offer => offer.OemNumber == oemNumber);
        }

        var offers = await query.ToListAsync(cancellationToken);
        if (!request.AllowSplit)
        {
            var selected = offers
                .Where(offer =>
                    offer.AvailableQuantity >= request.Quantity &&
                    offer.MinimumOrderQuantity <= request.Quantity)
                .OrderBy(offer => checked(offer.UnitCost * request.Quantity + offer.ShippingCost))
                .ThenBy(offer => offer.LeadTimeDays)
                .ThenBy(offer => offer.Supplier.Priority)
                .ThenBy(offer => offer.Id)
                .FirstOrDefault();
            return selected == null
                ? Insufficient()
                : Selected([
                    ToAllocation(selected, request.Quantity)
                ]);
        }

        var ranked = offers
            .Where(offer => offer.AvailableQuantity >= offer.MinimumOrderQuantity)
            .OrderBy(offer =>
                offer.UnitCost +
                offer.ShippingCost / Math.Min(offer.AvailableQuantity, request.Quantity))
            .ThenBy(offer => offer.LeadTimeDays)
            .ThenBy(offer => offer.Supplier.Priority)
            .ThenBy(offer => offer.Id)
            .Take(MaxAllocations)
            .ToList();
        var allocations = new List<SupplierAllocation>(MaxAllocations);
        var remaining = request.Quantity;
        foreach (var offer in ranked)
        {
            if (remaining == 0)
            {
                break;
            }

            var allocation = Math.Min(offer.AvailableQuantity, remaining);
            if (allocation < offer.MinimumOrderQuantity)
            {
                continue;
            }

            allocations.Add(ToAllocation(offer, allocation));
            remaining -= allocation;
        }

        return remaining == 0 ? Selected(allocations) : Insufficient();
    }

    private Task<SupplierOffer?> FindExistingAsync(
        long supplierId,
        string externalOfferId,
        CancellationToken cancellationToken) =>
        _context.Set<SupplierOffer>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                offer =>
                    offer.SupplierId == supplierId &&
                    offer.ExternalOfferId == externalOfferId,
                cancellationToken);

    private static SupplierOfferRegistrationResult ResolveExisting(
        SupplierOffer existing,
        string payloadHash) =>
        new(
            FixedTimeEquals(existing.PayloadHash, payloadHash)
                ? SupplierOfferRegistrationOutcome.Replayed
                : SupplierOfferRegistrationOutcome.Conflict,
            existing.Id,
            FixedTimeEquals(existing.PayloadHash, payloadHash)
                ? null
                : "The supplier offer id was already used with different values.");

    private static SupplierOfferCommand? Normalize(SupplierOfferCommand command)
    {
        if (command == null ||
            command.SupplierId <= 0 ||
            command.ProductId <= 0 ||
            command.UnitCost < 0 ||
            command.ShippingCost < 0 ||
            command.AvailableQuantity < 0 ||
            command.LeadTimeDays < 0 ||
            command.MinimumOrderQuantity <= 0 ||
            (!command.CanDropship && !command.CanSupplyWarehouse) ||
            string.IsNullOrWhiteSpace(command.ExternalOfferId) ||
            command.ExternalOfferId.Trim().Length > MaxExternalOfferIdLength ||
            !TryNormalizeOem(command.OemNumber, optional: false, out var oemNumber) ||
            !TryNormalizeCurrency(command.Currency, out var currency))
        {
            return null;
        }

        var validUntil = DateTime.SpecifyKind(command.ValidUntilUtc, DateTimeKind.Utc);
        return command with
        {
            ExternalOfferId = command.ExternalOfferId.Trim(),
            OemNumber = oemNumber!,
            Currency = currency,
            ValidUntilUtc = validUntil
        };
    }

    private static bool TryNormalizeCurrency(string? value, out string currency)
    {
        currency = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return currency.Length == 3 && currency.All(char.IsAsciiLetter);
    }

    private static bool TryNormalizeOem(
        string? value,
        bool optional,
        out string? oemNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            oemNumber = null;
            return optional;
        }

        oemNumber = new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray());
        return oemNumber.Length is > 0 and <= MaxOemNumberLength;
    }

    private static string ComputePayloadHash(SupplierOfferCommand command)
    {
        var canonical = string.Join('|',
            command.SupplierId.ToString(CultureInfo.InvariantCulture),
            command.ExternalOfferId,
            command.ProductId.ToString(CultureInfo.InvariantCulture),
            command.OemNumber,
            command.Currency,
            command.UnitCost.ToString("0.####", CultureInfo.InvariantCulture),
            command.ShippingCost.ToString("0.####", CultureInfo.InvariantCulture),
            command.AvailableQuantity.ToString(CultureInfo.InvariantCulture),
            command.LeadTimeDays.ToString(CultureInfo.InvariantCulture),
            command.MinimumOrderQuantity.ToString(CultureInfo.InvariantCulture),
            command.ValidUntilUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            command.CanDropship ? "1" : "0",
            command.CanSupplyWarehouse ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static SupplierAllocation ToAllocation(SupplierOffer offer, int quantity) =>
        new(
            offer.Id,
            offer.SupplierId,
            quantity,
            offer.UnitCost,
            offer.ShippingCost,
            offer.LeadTimeDays);

    private static SupplierSourcingResult Selected(
        IReadOnlyList<SupplierAllocation> allocations) =>
        new(
            SupplierSourcingOutcome.Selected,
            allocations,
            allocations.Sum(allocation =>
                checked(allocation.UnitCost * allocation.Quantity + allocation.ShippingCost)));

    private static SupplierSourcingResult Insufficient() =>
        new(
            SupplierSourcingOutcome.InsufficientSupply,
            [],
            0m,
            "No eligible supplier allocation can satisfy the requested quantity.");

    private static SupplierSourcingResult InvalidSourcing() =>
        new(
            SupplierSourcingOutcome.InvalidRequest,
            [],
            0m,
            "The sourcing request is invalid.");
}
