using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum RefundTransitionOutcome
{
    Created,
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record RefundTransitionResult(
    RefundTransitionOutcome Outcome,
    Refund? Refund = null,
    string? Message = null);

public sealed class RefundService
{
    private const int MaxProviderLength = 50;
    private const int MaxIdempotencyKeyLength = 100;
    private const int MaxProviderRefundIdLength = 200;
    private const int MaxFailureCodeLength = 100;
    private const decimal MaxMoneyAmount = 9_999_999_999_999_999.99m;

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public RefundService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RefundTransitionResult> RequestRefundAsync(
        int paymentId,
        long? paymentTransactionId,
        decimal amount,
        string currency,
        string idempotencyKey,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(
            paymentId,
            paymentTransactionId,
            amount,
            currency,
            idempotencyKey,
            provider);
        if (validationError != null)
        {
            return InvalidRequest(validationError);
        }

        var normalizedIdempotencyKey = idempotencyKey.Trim();
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var existingRefund = await _context.Refunds
            .AsNoTracking()
            .SingleOrDefaultAsync(
                refund => refund.IdempotencyKey == normalizedIdempotencyKey,
                cancellationToken);
        if (existingRefund != null)
        {
            return ResolveRequestReplay(
                existingRefund,
                paymentId,
                paymentTransactionId,
                amount,
                currency,
                normalizedProvider);
        }

        var payment = await _context.Payments
            .SingleOrDefaultAsync(candidate => candidate.Id == paymentId, cancellationToken);
        if (payment == null)
        {
            return new RefundTransitionResult(RefundTransitionOutcome.NotFound);
        }

        if (payment.Status is not (PaymentStatuses.Paid or PaymentStatuses.PartiallyRefunded))
        {
            return Conflict(
                null,
                $"A payment in {payment.Status} status cannot accept a refund request.");
        }

        if (!string.Equals(payment.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(null, "The refund provider does not match the payment provider.");
        }

        if (!string.Equals(payment.Currency, currency, StringComparison.Ordinal))
        {
            return Conflict(null, "The refund currency does not exactly match the payment currency.");
        }

        PaymentTransaction? paymentTransaction = null;
        if (paymentTransactionId.HasValue)
        {
            paymentTransaction = await _context.PaymentTransactions
                .SingleOrDefaultAsync(
                    transaction => transaction.Id == paymentTransactionId.Value,
                    cancellationToken);
            if (paymentTransaction == null)
            {
                return new RefundTransitionResult(RefundTransitionOutcome.NotFound);
            }

            if (paymentTransaction.PaymentId != paymentId)
            {
                return Conflict(null, "The payment transaction does not belong to the payment.");
            }

            if (!string.Equals(paymentTransaction.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(null, "The refund provider does not match the payment transaction provider.");
            }

            if (!string.Equals(paymentTransaction.Currency, currency, StringComparison.Ordinal))
            {
                return Conflict(
                    null,
                    "The refund currency does not exactly match the payment transaction currency.");
            }
        }

        var reservedPaymentAmount = await ReservedPaymentAmountAsync(
            paymentId,
            cancellationToken);
        if (reservedPaymentAmount + amount > payment.Amount)
        {
            return Conflict(null, "The refund would exceed the remaining paid amount.");
        }

        if (paymentTransaction != null)
        {
            var reservedTransactionAmount = await ReservedTransactionAmountAsync(
                paymentId,
                paymentTransaction.Id,
                cancellationToken);
            var accountedTransactionAmount = Math.Max(
                reservedTransactionAmount,
                paymentTransaction.RefundedAmount + await ActiveAmountAsync(
                    paymentId,
                    paymentTransaction.Id,
                    cancellationToken));

            if (accountedTransactionAmount + amount > paymentTransaction.PaidAmount)
            {
                return Conflict(null, "The refund would exceed the payment transaction paid amount.");
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var refund = new Refund
        {
            PaymentId = paymentId,
            PaymentTransactionId = paymentTransactionId,
            Provider = normalizedProvider,
            IdempotencyKey = normalizedIdempotencyKey,
            Status = RefundStatuses.Requested,
            Amount = amount,
            Currency = currency,
            RequestedAt = now,
            UpdatedAt = now
        };

        // Updating the payment's concurrency token in the same SaveChanges call
        // makes the aggregate reservation check safe across racing DbContexts.
        // The losing write rolls back its Refund insert with the implicit transaction.
        payment.ConcurrencyToken = Guid.NewGuid();
        _context.Refunds.Add(refund);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new RefundTransitionResult(RefundTransitionOutcome.Created, refund);
        }
        catch (DbUpdateConcurrencyException)
        {
            DetachRequestEntries(payment, refund);

            existingRefund = await _context.Refunds
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.IdempotencyKey == normalizedIdempotencyKey,
                    cancellationToken);
            if (existingRefund != null)
            {
                return ResolveRequestReplay(
                    existingRefund,
                    paymentId,
                    paymentTransactionId,
                    amount,
                    currency,
                    normalizedProvider);
            }

            return Conflict(
                null,
                "The payment changed while refund capacity was being reserved; retry with a new request.");
        }
        catch (DbUpdateException)
        {
            DetachRequestEntries(payment, refund);

            existingRefund = await _context.Refunds
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.IdempotencyKey == normalizedIdempotencyKey,
                    cancellationToken);
            if (existingRefund != null)
            {
                return ResolveRequestReplay(
                    existingRefund,
                    paymentId,
                    paymentTransactionId,
                    amount,
                    currency,
                    normalizedProvider);
            }

            throw;
        }
    }

    public async Task<RefundTransitionResult> MarkProcessingAsync(
        long refundId,
        CancellationToken cancellationToken = default)
    {
        if (refundId <= 0)
        {
            return InvalidRequest("The refund identifier must be positive.");
        }

        var refund = await _context.Refunds.FindAsync([refundId], cancellationToken);
        if (refund == null)
        {
            return new RefundTransitionResult(RefundTransitionOutcome.NotFound);
        }

        if (refund.Status == RefundStatuses.Processing)
        {
            return new RefundTransitionResult(RefundTransitionOutcome.Replayed, refund);
        }

        if (refund.Status != RefundStatuses.Requested)
        {
            return Conflict(refund, $"A refund in {refund.Status} status cannot enter Processing.");
        }

        refund.Status = RefundStatuses.Processing;
        refund.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        refund.ConcurrencyToken = Guid.NewGuid();
        return await SaveSimpleTransitionAsync(refund, cancellationToken);
    }

    public async Task<RefundTransitionResult> MarkSucceededAsync(
        long refundId,
        string providerRefundId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateSuccess(refundId, providerRefundId, completedAt);
        if (validationError != null)
        {
            return InvalidRequest(validationError);
        }

        var normalizedProviderRefundId = providerRefundId.Trim();
        await using var databaseTransaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);

        var refund = await _context.Refunds
            .Include(candidate => candidate.Payment)
            .Include(candidate => candidate.PaymentTransaction)
            .SingleOrDefaultAsync(candidate => candidate.Id == refundId, cancellationToken);
        if (refund == null)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            return new RefundTransitionResult(RefundTransitionOutcome.NotFound);
        }

        if (refund.Status == RefundStatuses.Succeeded)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            if (string.Equals(
                    refund.ProviderRefundId,
                    normalizedProviderRefundId,
                    StringComparison.Ordinal))
            {
                return new RefundTransitionResult(RefundTransitionOutcome.Replayed, refund);
            }

            return Conflict(refund, "The refund already succeeded with a different provider identifier.");
        }

        if (refund.Status is not (
                RefundStatuses.Requested or
                RefundStatuses.Processing or
                RefundStatuses.Unknown))
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            return Conflict(refund, $"A refund in {refund.Status} status cannot enter Succeeded.");
        }

        var succeededBefore = await _context.Refunds
            .Where(candidate =>
                candidate.PaymentId == refund.PaymentId &&
                candidate.Id != refund.Id &&
                candidate.Status == RefundStatuses.Succeeded)
            .SumAsync(candidate => (decimal?)candidate.Amount, cancellationToken) ?? 0m;
        if (succeededBefore + refund.Amount > refund.Payment.Amount)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            return Conflict(refund, "The successful refund total would exceed the payment amount.");
        }

        if (refund.PaymentTransaction != null)
        {
            if (refund.PaymentTransaction.RefundedAmount + refund.Amount >
                refund.PaymentTransaction.PaidAmount)
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                return Conflict(refund, "The successful refund would exceed the transaction paid amount.");
            }

            refund.PaymentTransaction.RefundedAmount += refund.Amount;
            refund.PaymentTransaction.UpdatedAt = completedAt.UtcDateTime;
        }

        refund.Status = RefundStatuses.Succeeded;
        refund.ProviderRefundId = normalizedProviderRefundId;
        refund.FailureCode = null;
        refund.CompletedAt = completedAt.UtcDateTime;
        refund.UpdatedAt = completedAt.UtcDateTime;
        refund.ConcurrencyToken = Guid.NewGuid();

        var successfulTotal = succeededBefore + refund.Amount;
        refund.Payment.Status = successfulTotal == refund.Payment.Amount
            ? PaymentStatuses.Refunded
            : PaymentStatuses.PartiallyRefunded;
        refund.Payment.UpdatedAt = completedAt.UtcDateTime;
        refund.Payment.ConcurrencyToken = Guid.NewGuid();

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(cancellationToken);
            return new RefundTransitionResult(RefundTransitionOutcome.Updated, refund);
        }
        catch (DbUpdateConcurrencyException)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            DetachChangedEntries();
            return Conflict(null, "The refund or payment was concurrently updated.");
        }
        catch (DbUpdateException)
        {
            var provider = refund.Provider;
            await databaseTransaction.RollbackAsync(cancellationToken);
            DetachChangedEntries();
            await databaseTransaction.DisposeAsync();

            var providerIdentifierConflict = await _context.Refunds
                .AsNoTracking()
                .AnyAsync(
                    candidate =>
                        candidate.Id != refundId &&
                        candidate.Provider == provider &&
                        candidate.ProviderRefundId == normalizedProviderRefundId,
                    cancellationToken);
            if (providerIdentifierConflict)
            {
                return Conflict(
                    null,
                    "The provider refund identifier conflicts with an existing refund.");
            }

            throw;
        }
    }

    public Task<RefundTransitionResult> MarkFailedAsync(
        long refundId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        return MarkTerminalFailureAsync(
            refundId,
            failureCode,
            RefundStatuses.Failed,
            cancellationToken);
    }

    public Task<RefundTransitionResult> MarkUnknownAsync(
        long refundId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        return MarkTerminalFailureAsync(
            refundId,
            failureCode,
            RefundStatuses.Unknown,
            cancellationToken);
    }

    private async Task<RefundTransitionResult> MarkTerminalFailureAsync(
        long refundId,
        string failureCode,
        string targetStatus,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateFailure(refundId, failureCode);
        if (validationError != null)
        {
            return InvalidRequest(validationError);
        }

        var normalizedFailureCode = failureCode.Trim();
        var refund = await _context.Refunds.FindAsync([refundId], cancellationToken);
        if (refund == null)
        {
            return new RefundTransitionResult(RefundTransitionOutcome.NotFound);
        }

        if (refund.Status == targetStatus)
        {
            return string.Equals(refund.FailureCode, normalizedFailureCode, StringComparison.Ordinal)
                ? new RefundTransitionResult(RefundTransitionOutcome.Replayed, refund)
                : Conflict(refund, $"The refund already entered {targetStatus} with a different code.");
        }

        var canEnterTerminalState =
            refund.Status is RefundStatuses.Requested or RefundStatuses.Processing ||
            refund.Status == RefundStatuses.Unknown && targetStatus == RefundStatuses.Failed;
        if (!canEnterTerminalState)
        {
            return Conflict(refund, $"A refund in {refund.Status} status cannot enter {targetStatus}.");
        }

        refund.Status = targetStatus;
        refund.FailureCode = normalizedFailureCode;
        refund.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        refund.ConcurrencyToken = Guid.NewGuid();
        return await SaveSimpleTransitionAsync(refund, cancellationToken);
    }

    private async Task<RefundTransitionResult> SaveSimpleTransitionAsync(
        Refund refund,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new RefundTransitionResult(RefundTransitionOutcome.Updated, refund);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(refund).State = EntityState.Detached;
            return Conflict(null, "The refund was concurrently updated.");
        }
    }

    private async Task<decimal> ReservedPaymentAmountAsync(
        int paymentId,
        CancellationToken cancellationToken)
    {
        return await _context.Refunds
            .Where(refund =>
                refund.PaymentId == paymentId &&
                (refund.Status == RefundStatuses.Requested ||
                 refund.Status == RefundStatuses.Processing ||
                 refund.Status == RefundStatuses.Unknown ||
                 refund.Status == RefundStatuses.Succeeded))
            .SumAsync(refund => (decimal?)refund.Amount, cancellationToken) ?? 0m;
    }

    private async Task<decimal> ReservedTransactionAmountAsync(
        int paymentId,
        long paymentTransactionId,
        CancellationToken cancellationToken)
    {
        return await _context.Refunds
            .Where(refund =>
                refund.PaymentId == paymentId &&
                refund.PaymentTransactionId == paymentTransactionId &&
                (refund.Status == RefundStatuses.Requested ||
                 refund.Status == RefundStatuses.Processing ||
                 refund.Status == RefundStatuses.Unknown ||
                 refund.Status == RefundStatuses.Succeeded))
            .SumAsync(refund => (decimal?)refund.Amount, cancellationToken) ?? 0m;
    }

    private async Task<decimal> ActiveAmountAsync(
        int paymentId,
        long paymentTransactionId,
        CancellationToken cancellationToken)
    {
        return await _context.Refunds
            .Where(refund =>
                refund.PaymentId == paymentId &&
                refund.PaymentTransactionId == paymentTransactionId &&
                (refund.Status == RefundStatuses.Requested ||
                 refund.Status == RefundStatuses.Processing ||
                 refund.Status == RefundStatuses.Unknown))
            .SumAsync(refund => (decimal?)refund.Amount, cancellationToken) ?? 0m;
    }

    private static RefundTransitionResult ResolveRequestReplay(
        Refund existingRefund,
        int paymentId,
        long? paymentTransactionId,
        decimal amount,
        string currency,
        string provider)
    {
        var samePayload =
            existingRefund.PaymentId == paymentId &&
            existingRefund.PaymentTransactionId == paymentTransactionId &&
            existingRefund.Amount == amount &&
            string.Equals(existingRefund.Currency, currency, StringComparison.Ordinal) &&
            string.Equals(existingRefund.Provider, provider, StringComparison.Ordinal);

        return samePayload
            ? new RefundTransitionResult(RefundTransitionOutcome.Replayed, existingRefund)
            : Conflict(
                existingRefund,
                "The idempotency key was already used with a different refund payload.");
    }

    private static string? ValidateRequest(
        int paymentId,
        long? paymentTransactionId,
        decimal amount,
        string currency,
        string idempotencyKey,
        string provider)
    {
        if (paymentId <= 0)
        {
            return "The payment identifier must be positive.";
        }

        if (paymentTransactionId <= 0)
        {
            return "The payment transaction identifier must be positive.";
        }

        if (amount <= 0 || amount > MaxMoneyAmount || decimal.Round(amount, 2) != amount)
        {
            return "The refund amount must be positive, fit decimal(18,2), and use at most two fractional digits.";
        }

        if (!IsValidCurrency(currency))
        {
            return "The currency must consist of exactly three uppercase ASCII letters.";
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Trim().Length > MaxIdempotencyKeyLength)
        {
            return $"The idempotency key must contain 1 to {MaxIdempotencyKeyLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(provider) || provider.Trim().Length > MaxProviderLength)
        {
            return $"The provider must contain 1 to {MaxProviderLength} characters.";
        }

        return null;
    }

    private static string? ValidateSuccess(
        long refundId,
        string providerRefundId,
        DateTimeOffset completedAt)
    {
        if (refundId <= 0)
        {
            return "The refund identifier must be positive.";
        }

        if (string.IsNullOrWhiteSpace(providerRefundId) ||
            providerRefundId.Trim().Length > MaxProviderRefundIdLength)
        {
            return $"The provider refund identifier must contain 1 to {MaxProviderRefundIdLength} characters.";
        }

        if (completedAt == default)
        {
            return "The refund completion time is required.";
        }

        return null;
    }

    private static string? ValidateFailure(long refundId, string failureCode)
    {
        if (refundId <= 0)
        {
            return "The refund identifier must be positive.";
        }

        if (string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Trim().Length > MaxFailureCodeLength)
        {
            return $"The failure code must contain 1 to {MaxFailureCodeLength} characters.";
        }

        return null;
    }

    private static bool IsValidCurrency(string currency)
    {
        return currency is { Length: 3 } &&
               currency.All(character => character is >= 'A' and <= 'Z');
    }

    private void DetachChangedEntries()
    {
        foreach (var entry in _context.ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private void DetachRequestEntries(Payment payment, Refund refund)
    {
        _context.Entry(refund).State = EntityState.Detached;
        _context.Entry(payment).State = EntityState.Detached;
    }

    private static RefundTransitionResult InvalidRequest(string message)
    {
        return new RefundTransitionResult(RefundTransitionOutcome.InvalidRequest, Message: message);
    }

    private static RefundTransitionResult Conflict(Refund? refund, string message)
    {
        return new RefundTransitionResult(RefundTransitionOutcome.Conflict, refund, message);
    }
}
