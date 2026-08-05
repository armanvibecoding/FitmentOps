using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public enum BulkQuoteOutcome
{
    Submitted,
    Replayed,
    Updated,
    NotFound,
    NotEligible,
    Conflict,
    Expired,
    InvalidRequest
}

public sealed record BulkQuoteInputLine(string Identifier, int Quantity);

public sealed record SubmitBulkQuoteCommand(
    int UserId,
    string IdempotencyKey,
    string Currency,
    IReadOnlyCollection<BulkQuoteInputLine> Lines);

public sealed record BulkQuoteOfferLine(
    long LineId,
    decimal? UnitPrice,
    int AvailableQuantity,
    int LeadTimeDays);

public sealed record BulkQuoteResult(
    BulkQuoteOutcome Outcome,
    long? RequestId = null,
    string? RequestNumber = null,
    string? Status = null,
    bool Replayed = false,
    string? Message = null);

public sealed class BulkQuoteService
{
    public const int MaxLines = 500;
    private const int MaxIdentifierLength = 80;

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public BulkQuoteService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BulkQuoteResult> SubmitAsync(
        SubmitBulkQuoteCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(command);
        if (normalized == null)
        {
            return Invalid();
        }

        var eligible = await _context.DealerApplications
            .AsNoTracking()
            .AnyAsync(application =>
                application.UserId == normalized.UserId &&
                application.Status == DealerApplicationStatuses.Approved &&
                application.CustomerGroup != null &&
                application.CustomerGroup.IsActive,
                cancellationToken);
        if (!eligible)
        {
            return new BulkQuoteResult(BulkQuoteOutcome.NotEligible);
        }

        var payloadHash = ComputePayloadHash(normalized);
        var existing = await _context.BulkQuoteRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                request => request.IdempotencyKey == normalized.IdempotencyKey,
                cancellationToken);
        if (existing != null)
        {
            return ResolveExisting(existing, payloadHash);
        }

        var identifiers = normalized.Lines
            .Select(line => line.Identifier)
            .ToArray();
        var verifiedMatches = await _context.ProductIdentifiers
            .AsNoTracking()
            .Where(identifier =>
                identifier.IsVerified &&
                identifiers.Contains(identifier.NormalizedValue))
            .Select(identifier => new { identifier.NormalizedValue, identifier.ProductId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var unambiguousProductIds = verifiedMatches
            .GroupBy(match => match.NormalizedValue, StringComparer.Ordinal)
            .Where(group => group.Select(match => match.ProductId).Distinct().Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Select(match => match.ProductId).Single(),
                StringComparer.Ordinal);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requestNumberDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"rfq|{normalized.UserId}|{normalized.IdempotencyKey}")));
        var request = new BulkQuoteRequest
        {
            RequestNumber = $"RFQ-{requestNumberDigest[..20]}",
            UserId = normalized.UserId,
            Currency = normalized.Currency,
            Status = BulkQuoteStatuses.Submitted,
            IdempotencyKey = normalized.IdempotencyKey,
            PayloadHash = payloadHash,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        var lineNumber = 0;
        foreach (var line in normalized.Lines)
        {
            var productId = unambiguousProductIds.GetValueOrDefault(line.Identifier);
            request.Lines.Add(new BulkQuoteLine
            {
                LineNumber = ++lineNumber,
                RequestedIdentifier = line.DisplayIdentifier,
                NormalizedIdentifier = line.Identifier,
                RequestedQuantity = line.Quantity,
                ProductId = productId == 0 ? null : productId,
                Status = productId == 0
                    ? BulkQuoteLineStatuses.Unmatched
                    : BulkQuoteLineStatuses.Matched
            });
        }

        _context.BulkQuoteRequests.Add(request);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return ToResult(BulkQuoteOutcome.Submitted, request);
        }
        catch (DbUpdateException)
        {
            _context.Entry(request).State = EntityState.Detached;
            existing = await _context.BulkQuoteRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (existing != null)
            {
                return ResolveExisting(existing, payloadHash);
            }

            throw;
        }
    }

    public async Task<BulkQuoteResult> PrepareQuoteAsync(
        long requestId,
        IReadOnlyCollection<BulkQuoteOfferLine> offers,
        DateTimeOffset validUntilUtc,
        CancellationToken cancellationToken = default)
    {
        if (requestId <= 0 ||
            offers == null ||
            offers.Count is < 1 or > MaxLines ||
            offers.Select(offer => offer.LineId).Distinct().Count() != offers.Count ||
            offers.Any(offer =>
                offer.LineId <= 0 ||
                offer.AvailableQuantity < 0 ||
                offer.LeadTimeDays < 0 ||
                offer.UnitPrice is <= 0))
        {
            return Invalid();
        }

        var now = _timeProvider.GetUtcNow();
        if (validUntilUtc <= now || validUntilUtc > now.AddDays(30))
        {
            return Invalid("Quote validity must be in the next 30 days.");
        }

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        var request = await _context.BulkQuoteRequests
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (request == null)
        {
            return new BulkQuoteResult(BulkQuoteOutcome.NotFound);
        }

        if (request.Status == BulkQuoteStatuses.Quoted)
        {
            return ToResult(BulkQuoteOutcome.Replayed, request, replayed: true);
        }

        if (request.Status is not BulkQuoteStatuses.Submitted and
            not BulkQuoteStatuses.UnderReview)
        {
            return ToResult(BulkQuoteOutcome.Conflict, request);
        }

        var byLine = offers.ToDictionary(offer => offer.LineId);
        if (request.Lines.Count != byLine.Count ||
            request.Lines.Any(line => !byLine.ContainsKey(line.Id)))
        {
            return Invalid("Every RFQ line must have exactly one quote decision.");
        }

        foreach (var line in request.Lines)
        {
            var offer = byLine[line.Id];
            if (offer.UnitPrice.HasValue && line.ProductId.HasValue)
            {
                line.Status = BulkQuoteLineStatuses.Quoted;
                line.QuotedUnitPrice = offer.UnitPrice;
            }
            else
            {
                line.Status = BulkQuoteLineStatuses.Unavailable;
                line.QuotedUnitPrice = null;
            }

            line.AvailableQuantity = offer.AvailableQuantity;
            line.LeadTimeDays = offer.LeadTimeDays;
        }

        request.Status = BulkQuoteStatuses.Quoted;
        request.QuotedAtUtc = now.UtcDateTime;
        request.QuoteValidUntilUtc = validUntilUtc.UtcDateTime;
        request.UpdatedAtUtc = now.UtcDateTime;
        request.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ToResult(BulkQuoteOutcome.Updated, request);
    }

    public async Task<BulkQuoteResult> AcceptAsync(
        long requestId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (requestId <= 0 || userId <= 0)
        {
            return Invalid();
        }

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        var request = await _context.BulkQuoteRequests
            .SingleOrDefaultAsync(
                candidate => candidate.Id == requestId && candidate.UserId == userId,
                cancellationToken);
        if (request == null)
        {
            return new BulkQuoteResult(BulkQuoteOutcome.NotFound);
        }

        if (request.Status == BulkQuoteStatuses.Accepted)
        {
            return ToResult(BulkQuoteOutcome.Replayed, request, replayed: true);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (request.Status != BulkQuoteStatuses.Quoted)
        {
            return ToResult(BulkQuoteOutcome.Conflict, request);
        }

        if (request.QuoteValidUntilUtc <= now)
        {
            request.Status = BulkQuoteStatuses.Expired;
            request.UpdatedAtUtc = now;
            request.ConcurrencyToken = Guid.NewGuid();
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return ToResult(BulkQuoteOutcome.Expired, request);
        }

        request.Status = BulkQuoteStatuses.Accepted;
        request.AcceptedAtUtc = now;
        request.UpdatedAtUtc = now;
        request.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ToResult(BulkQuoteOutcome.Updated, request);
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
    }

    private static NormalizedCommand? Normalize(SubmitBulkQuoteCommand command)
    {
        if (command == null ||
            command.UserId <= 0 ||
            !IsSafeKey(command.IdempotencyKey) ||
            !string.Equals(command.Currency?.Trim(), "TRY", StringComparison.OrdinalIgnoreCase) ||
            command.Lines == null ||
            command.Lines.Count is < 1 or > MaxLines)
        {
            return null;
        }

        var normalized = new List<NormalizedLine>();
        foreach (var line in command.Lines)
        {
            if (line == null ||
                line.Quantity is < 1 or > 100_000 ||
                string.IsNullOrWhiteSpace(line.Identifier) ||
                line.Identifier.Trim().Length > MaxIdentifierLength)
            {
                return null;
            }

            var identifier = NormalizeIdentifier(line.Identifier);
            if (identifier.Length == 0)
            {
                return null;
            }

            normalized.Add(new NormalizedLine(identifier, line.Identifier.Trim(), line.Quantity));
        }

        try
        {
            var merged = normalized
                .GroupBy(line => line.Identifier, StringComparer.Ordinal)
                .Select(group => new NormalizedLine(
                    group.Key,
                    group.First().DisplayIdentifier,
                    group.Sum(line => checked(line.Quantity))))
                .OrderBy(line => line.Identifier, StringComparer.Ordinal)
                .ToArray();
            return new NormalizedCommand(
                command.UserId,
                command.IdempotencyKey.Trim(),
                "TRY",
                merged);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string NormalizeIdentifier(string value) =>
        new(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray());

    private static bool IsSafeKey(string? value) =>
        value is { Length: >= 16 and <= 100 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ComputePayloadHash(NormalizedCommand command)
    {
        var canonical = new StringBuilder()
            .Append(command.UserId.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(command.Currency);
        foreach (var line in command.Lines)
        {
            canonical.Append('|')
                .Append(line.Identifier)
                .Append(':')
                .Append(line.Quantity.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static BulkQuoteResult ResolveExisting(
        BulkQuoteRequest request,
        string payloadHash)
    {
        var replay = FixedTimeEquals(request.PayloadHash, payloadHash);
        return replay
            ? ToResult(BulkQuoteOutcome.Replayed, request, replayed: true)
            : ToResult(
                BulkQuoteOutcome.Conflict,
                request,
                message: "The idempotency key was used with different RFQ lines.");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static BulkQuoteResult ToResult(
        BulkQuoteOutcome outcome,
        BulkQuoteRequest request,
        bool replayed = false,
        string? message = null) =>
        new(
            outcome,
            request.Id,
            request.RequestNumber,
            request.Status,
            replayed,
            message);

    private static BulkQuoteResult Invalid(
        string message = "Bulk quote request fields are invalid.") =>
        new(BulkQuoteOutcome.InvalidRequest, Message: message);

    private sealed record NormalizedCommand(
        int UserId,
        string IdempotencyKey,
        string Currency,
        IReadOnlyList<NormalizedLine> Lines);

    private sealed record NormalizedLine(
        string Identifier,
        string DisplayIdentifier,
        int Quantity);
}
