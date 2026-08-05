using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum DealerApplicationOutcome
{
    Submitted,
    Replayed,
    Updated,
    NotFound,
    Conflict,
    InvalidRequest
}

public enum DealerReviewDecision
{
    Approve,
    Reject,
    Suspend,
    Reactivate
}

public sealed record DealerApplicationCommand(
    int UserId,
    string IdempotencyKey,
    string CompanyName,
    string TaxNumber,
    string ContactName,
    string ContactEmail,
    string ContactPhone);

public sealed record DealerApplicationResult(
    DealerApplicationOutcome Outcome,
    long? ApplicationId = null,
    string? Status = null,
    string? Message = null);

public sealed class DealerApplicationService
{
    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public DealerApplicationService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DealerApplicationResult> SubmitAsync(
        DealerApplicationCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(command);
        if (normalized == null)
        {
            return InvalidApplication();
        }

        var userExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == normalized.UserId && user.IsActive, cancellationToken);
        if (!userExists)
        {
            return new DealerApplicationResult(DealerApplicationOutcome.NotFound);
        }

        var payloadHash = ComputePayloadHash(normalized);
        var existingByKey = await _context.DealerApplications
            .AsNoTracking()
            .SingleOrDefaultAsync(
                application => application.IdempotencyKey == normalized.IdempotencyKey,
                cancellationToken);
        if (existingByKey != null)
        {
            return ResolveExisting(existingByKey, payloadHash);
        }

        var existingUserApplication = await _context.DealerApplications
            .AsNoTracking()
            .SingleOrDefaultAsync(
                application => application.UserId == normalized.UserId,
                cancellationToken);
        if (existingUserApplication != null)
        {
            return new DealerApplicationResult(
                DealerApplicationOutcome.Conflict,
                existingUserApplication.Id,
                existingUserApplication.Status,
                "The user already has a dealer application.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var application = new DealerApplication
        {
            UserId = normalized.UserId,
            IdempotencyKey = normalized.IdempotencyKey,
            PayloadHash = payloadHash,
            CompanyName = normalized.CompanyName,
            TaxNumber = normalized.TaxNumber,
            ContactName = normalized.ContactName,
            ContactEmail = normalized.ContactEmail,
            ContactPhone = normalized.ContactPhone,
            Status = DealerApplicationStatuses.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        _context.DealerApplications.Add(application);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new DealerApplicationResult(
                DealerApplicationOutcome.Submitted,
                application.Id,
                application.Status);
        }
        catch (DbUpdateException)
        {
            _context.Entry(application).State = EntityState.Detached;
            existingByKey = await _context.DealerApplications
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (existingByKey != null)
            {
                return ResolveExisting(existingByKey, payloadHash);
            }

            throw;
        }
    }

    public async Task<DealerApplicationResult> ReviewAsync(
        long applicationId,
        DealerReviewDecision decision,
        long? customerGroupId,
        CancellationToken cancellationToken = default)
    {
        if (applicationId <= 0)
        {
            return InvalidApplication();
        }

        var application = await _context.DealerApplications
            .SingleOrDefaultAsync(candidate => candidate.Id == applicationId, cancellationToken);
        if (application == null)
        {
            return new DealerApplicationResult(DealerApplicationOutcome.NotFound);
        }

        string targetStatus;
        switch (decision)
        {
            case DealerReviewDecision.Approve
                when application.Status == DealerApplicationStatuses.Pending:
            case DealerReviewDecision.Reactivate
                when application.Status == DealerApplicationStatuses.Suspended:
                if (customerGroupId is not > 0 ||
                    !await _context.CustomerGroups.AsNoTracking().AnyAsync(
                        group => group.Id == customerGroupId && group.IsActive,
                        cancellationToken))
                {
                    return InvalidApplication("An active customer group is required.");
                }

                targetStatus = DealerApplicationStatuses.Approved;
                break;
            case DealerReviewDecision.Reject
                when application.Status == DealerApplicationStatuses.Pending:
                targetStatus = DealerApplicationStatuses.Rejected;
                customerGroupId = null;
                break;
            case DealerReviewDecision.Suspend
                when application.Status == DealerApplicationStatuses.Approved:
                targetStatus = DealerApplicationStatuses.Suspended;
                break;
            default:
                return new DealerApplicationResult(
                    DealerApplicationOutcome.Conflict,
                    application.Id,
                    application.Status,
                    "The dealer application transition is not allowed.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        application.Status = targetStatus;
        application.CustomerGroupId = customerGroupId;
        application.ReviewedAtUtc = now;
        application.UpdatedAtUtc = now;
        application.ConcurrencyToken = Guid.NewGuid();
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new DealerApplicationResult(
                DealerApplicationOutcome.Updated,
                application.Id,
                application.Status);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(application).State = EntityState.Detached;
            return new DealerApplicationResult(
                DealerApplicationOutcome.Conflict,
                applicationId,
                Message: "The dealer application was changed concurrently.");
        }
    }

    private static DealerApplicationCommand? Normalize(DealerApplicationCommand command)
    {
        if (command == null ||
            command.UserId <= 0 ||
            !IsSafeKey(command.IdempotencyKey) ||
            !HasLength(command.CompanyName, 2, 160) ||
            !HasLength(command.ContactName, 2, 100) ||
            !HasLength(command.ContactEmail, 3, 200) ||
            !command.ContactEmail.Contains('@', StringComparison.Ordinal) ||
            !HasLength(command.ContactPhone, 1, 20))
        {
            return null;
        }

        var taxNumber = new string((command.TaxNumber ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (taxNumber.Length is < 5 or > 32)
        {
            return null;
        }

        return command with
        {
            IdempotencyKey = command.IdempotencyKey.Trim(),
            CompanyName = command.CompanyName.Trim(),
            TaxNumber = taxNumber,
            ContactName = command.ContactName.Trim(),
            ContactEmail = command.ContactEmail.Trim().ToLowerInvariant(),
            ContactPhone = command.ContactPhone.Trim()
        };
    }

    private static bool IsSafeKey(string? value) =>
        value is { Length: >= 16 and <= 100 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool HasLength(string? value, int minimum, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minimum && value.Trim().Length <= maximum;

    private static string ComputePayloadHash(DealerApplicationCommand command)
    {
        var canonical = string.Join('|',
            command.UserId.ToString(CultureInfo.InvariantCulture),
            command.CompanyName,
            command.TaxNumber,
            command.ContactName,
            command.ContactEmail,
            command.ContactPhone);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DealerApplicationResult ResolveExisting(
        DealerApplication existing,
        string payloadHash)
    {
        var replay = FixedTimeEquals(existing.PayloadHash, payloadHash);
        return new DealerApplicationResult(
            replay ? DealerApplicationOutcome.Replayed : DealerApplicationOutcome.Conflict,
            existing.Id,
            existing.Status,
            replay ? null : "The idempotency key was used with different application data.");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static DealerApplicationResult InvalidApplication(
        string message = "Dealer application fields are invalid.") =>
        new(DealerApplicationOutcome.InvalidRequest, Message: message);
}

public enum B2bPriceOutcome
{
    Priced,
    BasePrice,
    NotEligible,
    NotFound,
    InvalidRequest
}

public sealed record B2bPriceRequest(
    int UserId,
    int ProductId,
    int Quantity,
    decimal PeriodRevenue,
    string Currency,
    DateTimeOffset? AtUtc = null);

public sealed record B2bPriceResult(
    B2bPriceOutcome Outcome,
    decimal UnitPrice,
    decimal LineTotal,
    string Currency,
    long? AppliedRuleId = null);

public sealed class B2bPricingService
{
    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public B2bPricingService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<B2bPriceResult> CalculateAsync(
        B2bPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.UserId <= 0 ||
            request.ProductId <= 0 ||
            request.Quantity is < 1 or > 100_000 ||
            request.PeriodRevenue < 0 ||
            !string.Equals(request.Currency?.Trim(), "TRY", StringComparison.OrdinalIgnoreCase))
        {
            return new B2bPriceResult(B2bPriceOutcome.InvalidRequest, 0m, 0m, "TRY");
        }

        var product = await _context.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ProductId, cancellationToken);
        if (product == null)
        {
            return new B2bPriceResult(B2bPriceOutcome.NotFound, 0m, 0m, "TRY");
        }

        var baseResult = new B2bPriceResult(
            B2bPriceOutcome.BasePrice,
            product.Price,
            checked(product.Price * request.Quantity),
            "TRY");
        var application = await _context.DealerApplications
            .AsNoTracking()
            .Include(candidate => candidate.CustomerGroup)
            .SingleOrDefaultAsync(candidate => candidate.UserId == request.UserId, cancellationToken);
        if (application?.Status != DealerApplicationStatuses.Approved ||
            application.CustomerGroup is not { IsActive: true })
        {
            return baseResult with { Outcome = B2bPriceOutcome.NotEligible };
        }

        var atUtc = (request.AtUtc ?? _timeProvider.GetUtcNow()).UtcDateTime;
        var rules = await _context.PriceRules
            .AsNoTracking()
            .Include(rule => rule.PriceList)
            .Where(rule =>
                rule.IsActive &&
                rule.PriceList.IsActive &&
                rule.PriceList.CustomerGroupId == application.CustomerGroupId &&
                rule.PriceList.Currency == "TRY" &&
                rule.PriceList.ValidFromUtc <= atUtc &&
                (rule.PriceList.ValidToUtc == null || rule.PriceList.ValidToUtc > atUtc) &&
                rule.ValidFromUtc <= atUtc &&
                (rule.ValidToUtc == null || rule.ValidToUtc > atUtc) &&
                rule.MinimumQuantity <= request.Quantity &&
                rule.MinimumPeriodRevenue <= request.PeriodRevenue &&
                (rule.ProductId == null || rule.ProductId == product.Id) &&
                (rule.BrandId == null || rule.BrandId == product.BrandId) &&
                (rule.CategoryId == null || rule.CategoryId == product.CategoryId))
            .ToListAsync(cancellationToken);
        var selected = rules
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(Specificity)
            .ThenByDescending(rule => rule.MinimumQuantity)
            .ThenByDescending(rule => rule.MinimumPeriodRevenue)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();
        if (selected == null)
        {
            return baseResult;
        }

        var unitPrice = selected.FixedUnitPrice ??
            product.Price * (1m - selected.DiscountPercentage!.Value / 100m);
        unitPrice = decimal.Round(unitPrice, 2, MidpointRounding.AwayFromZero);
        if (unitPrice <= 0)
        {
            return baseResult;
        }

        return new B2bPriceResult(
            B2bPriceOutcome.Priced,
            unitPrice,
            checked(unitPrice * request.Quantity),
            "TRY",
            selected.Id);
    }

    private static int Specificity(PriceRule rule) =>
        (rule.ProductId.HasValue ? 4 : 0) +
        (rule.BrandId.HasValue ? 2 : 0) +
        (rule.CategoryId.HasValue ? 1 : 0);
}
