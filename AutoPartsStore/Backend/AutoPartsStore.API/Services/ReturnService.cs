using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum ReturnServiceOutcome
{
    Created,
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record ReturnItemRequest(
    int OrderItemId,
    int Quantity,
    string ReasonCode);

public sealed record ReturnServiceResult(
    ReturnServiceOutcome Outcome,
    ReturnRequest? ReturnRequest = null,
    string? Message = null);

public sealed class ReturnService
{
    private const int MaxIdempotencyKeyLength = 100;
    private const int MaxItemsPerRequest = 100;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ReturnRequestStatuses.Requested] = SetOf(
                ReturnRequestStatuses.Approved,
                ReturnRequestStatuses.Rejected,
                ReturnRequestStatuses.Cancelled),
            [ReturnRequestStatuses.Approved] = SetOf(
                ReturnRequestStatuses.Received,
                ReturnRequestStatuses.Cancelled),
            [ReturnRequestStatuses.Received] = SetOf(ReturnRequestStatuses.Inspected),
            [ReturnRequestStatuses.Inspected] = SetOf(ReturnRequestStatuses.Rejected),
            [ReturnRequestStatuses.Refunded] = SetOf(ReturnRequestStatuses.Closed)
        };

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ReturnService(AutoPartsDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReturnServiceResult> RequestAsync(
        int orderId,
        string idempotencyKey,
        IReadOnlyCollection<ReturnItemRequest> items,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        var normalizedItems = NormalizeAndValidateItems(orderId, normalizedKey, items);
        if (normalizedItems.Error != null)
        {
            return Invalid(normalizedItems.Error);
        }

        var existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
        if (existing != null)
        {
            return ResolveRequestReplay(existing, orderId, normalizedItems.Items!);
        }

        var hasAmbientTransaction = _context.Database.CurrentTransaction != null;
        try
        {
            await using var ownedTransaction =
                _context.Database.IsRelational() && !hasAmbientTransaction
                ? await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                : null;

            existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return ResolveRequestReplay(existing, orderId, normalizedItems.Items!);
            }

            // This no-op write is an aggregate gate. It obtains a row/database write
            // lock before capacity is read, so separate DbContexts cannot both reserve
            // the same order-item quantity from a stale snapshot.
            var gated = await _context.Orders
                .Where(order =>
                    order.Id == orderId &&
                    order.Status == OrderStatuses.Delivered)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(order => order.Status, order => order.Status),
                    cancellationToken);

            if (gated == 0)
            {
                var order = await _context.Orders
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return order == null
                    ? new ReturnServiceResult(ReturnServiceOutcome.NotFound)
                    : Conflict(null, "Only delivered orders can receive a return request.");
            }

            var requestedIds = normalizedItems.Items!
                .Select(item => item.OrderItemId)
                .ToArray();
            var orderItems = await _context.OrderItems
                .AsNoTracking()
                .Where(item => item.OrderId == orderId && requestedIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);

            if (orderItems.Count != requestedIds.Length)
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return Invalid("Every return item must belong to the delivered order.");
            }

            var alreadyAccounted = await _context.Set<ReturnItem>()
                .AsNoTracking()
                .Where(item =>
                    item.ReturnRequest.OrderId == orderId &&
                    item.ReturnRequest.Status != ReturnRequestStatuses.Rejected &&
                    item.ReturnRequest.Status != ReturnRequestStatuses.Cancelled &&
                    requestedIds.Contains(item.OrderItemId))
                .GroupBy(item => item.OrderItemId)
                .Select(group => new
                {
                    OrderItemId = group.Key,
                    Quantity = group.Sum(item => (long)item.Quantity)
                })
                .ToDictionaryAsync(item => item.OrderItemId, item => item.Quantity, cancellationToken);

            foreach (var requestedItem in normalizedItems.Items!)
            {
                var accountedQuantity = alreadyAccounted.GetValueOrDefault(requestedItem.OrderItemId);
                if (accountedQuantity + (long)requestedItem.Quantity >
                    (long)orderItems[requestedItem.OrderItemId].Quantity)
                {
                    if (ownedTransaction != null)
                    {
                        await ownedTransaction.RollbackAsync(cancellationToken);
                    }

                    return Conflict(
                        null,
                        $"Return quantity exceeds the purchased quantity for order item {requestedItem.OrderItemId}.");
                }
            }

            var now = UtcNow();
            var request = new ReturnRequest
            {
                OrderId = orderId,
                IdempotencyKey = normalizedKey,
                Status = ReturnRequestStatuses.Requested,
                RequestedAt = now,
                UpdatedAt = now
            };
            foreach (var requestedItem in normalizedItems.Items!)
            {
                request.Items.Add(new ReturnItem
                {
                    OrderItemId = requestedItem.OrderItemId,
                    Quantity = requestedItem.Quantity,
                    ReasonCode = requestedItem.ReasonCode
                });
            }

            _context.Set<ReturnRequest>().Add(request);
            await _context.SaveChangesAsync(cancellationToken);
            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return new ReturnServiceResult(ReturnServiceOutcome.Created, request);
        }
        catch (DbUpdateException exception) when (!hasAmbientTransaction)
        {
            DetachChangedEntries();
            existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                return ResolveRequestReplay(existing, orderId, normalizedItems.Items!);
            }

            if (exception.InnerException is DbException databaseException &&
                IsRetryableConcurrencyException(databaseException))
            {
                return Conflict(null, "The return capacity changed concurrently; retry the request.");
            }

            throw;
        }
        catch (DbException exception) when (
            !hasAmbientTransaction && IsRetryableConcurrencyException(exception))
        {
            DetachChangedEntries();
            return Conflict(null, "The return capacity is being updated concurrently; retry the request.");
        }
    }

    public async Task<ReturnServiceResult> TransitionAsync(
        long returnRequestId,
        string targetStatus,
        CancellationToken cancellationToken = default)
    {
        if (returnRequestId <= 0 || string.IsNullOrWhiteSpace(targetStatus))
        {
            return Invalid("Return request id and target status are required.");
        }

        var normalizedTarget = targetStatus.Trim();
        if (normalizedTarget is ReturnRequestStatuses.RefundPending or ReturnRequestStatuses.Refunded)
        {
            return Invalid("Refund states require their dedicated external command.");
        }

        var request = await _context.Set<ReturnRequest>()
            .SingleOrDefaultAsync(candidate => candidate.Id == returnRequestId, cancellationToken);
        if (request == null)
        {
            return new ReturnServiceResult(ReturnServiceOutcome.NotFound);
        }

        if (request.Status == normalizedTarget)
        {
            return new ReturnServiceResult(ReturnServiceOutcome.Replayed, request);
        }

        if (!AllowedTransitions.TryGetValue(request.Status, out var allowed) ||
            !allowed.Contains(normalizedTarget))
        {
            return Conflict(request, $"A return in {request.Status} status cannot enter {normalizedTarget}.");
        }

        request.Status = normalizedTarget;
        Touch(request);
        return await SaveTransitionAsync(request, cancellationToken);
    }

    public async Task<ReturnServiceResult> MarkRefundPendingAsync(
        long returnRequestId,
        long refundId,
        CancellationToken cancellationToken = default)
    {
        if (returnRequestId <= 0 || refundId <= 0)
        {
            return Invalid("Return request and refund identifiers must be positive.");
        }

        var request = await LoadForRefundReconciliationAsync(returnRequestId, cancellationToken);
        if (request == null)
        {
            return new ReturnServiceResult(ReturnServiceOutcome.NotFound);
        }

        var refund = await _context.Refunds
            .Include(candidate => candidate.Payment)
            .Include(candidate => candidate.PaymentTransaction)
            .SingleOrDefaultAsync(candidate => candidate.Id == refundId, cancellationToken);
        if (refund == null)
        {
            return new ReturnServiceResult(ReturnServiceOutcome.NotFound);
        }

        var validationError = ValidateRefundBinding(request, refund);
        if (validationError != null)
        {
            return Conflict(request, validationError);
        }

        if (request.RefundId == refund.Id)
        {
            return await ApplyRefundStateAsync(request, refund, cancellationToken);
        }

        if (request.Status != ReturnRequestStatuses.Inspected)
        {
            return Conflict(request, $"A return in {request.Status} status cannot bind a refund.");
        }

        if (request.RefundId != null && request.Refund?.Status != RefundStatuses.Failed)
        {
            return Conflict(request, "Only a failed linked refund can be replaced.");
        }

        if (refund.Status == RefundStatuses.Failed)
        {
            return Conflict(request, "A failed refund cannot be bound as RefundPending.");
        }

        if (refund.Status is not (
                RefundStatuses.Requested or
                RefundStatuses.Processing or
                RefundStatuses.Unknown or
                RefundStatuses.Succeeded))
        {
            return Conflict(request, $"Unsupported refund status {refund.Status}.");
        }

        var completionError = ValidateSucceededRefundCompletion(request, refund);
        if (completionError != null)
        {
            return Conflict(request, completionError);
        }

        var linkedElsewhere = await _context.Set<ReturnRequest>()
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.Id != request.Id && candidate.RefundId == refund.Id,
                cancellationToken);
        if (linkedElsewhere)
        {
            return Conflict(request, "The refund is already linked to another return.");
        }

        request.RefundId = refund.Id;
        request.Refund = refund;
        request.ExternalRefundRequestReference = null;
        request.ExternalRefundConfirmationReference = null;
        request.RefundedAt = null;
        return await ApplyRefundStateAsync(request, refund, cancellationToken, isNewBinding: true);
    }

    public async Task<ReturnServiceResult> ReconcileRefundAsync(
        long returnRequestId,
        CancellationToken cancellationToken = default)
    {
        if (returnRequestId <= 0)
        {
            return Invalid("Return request id must be positive.");
        }

        var request = await LoadForRefundReconciliationAsync(returnRequestId, cancellationToken);
        if (request == null)
        {
            return new ReturnServiceResult(ReturnServiceOutcome.NotFound);
        }

        if (request.Refund == null || request.RefundId == null)
        {
            return Conflict(request, "The return has no linked refund to reconcile.");
        }

        var validationError = ValidateRefundBinding(request, request.Refund);
        if (validationError != null)
        {
            return Conflict(request, validationError);
        }

        return await ApplyRefundStateAsync(request, request.Refund, cancellationToken);
    }

    private async Task<ReturnServiceResult> ApplyRefundStateAsync(
        ReturnRequest request,
        Refund refund,
        CancellationToken cancellationToken,
        bool isNewBinding = false)
    {
        if (refund.Status is RefundStatuses.Requested or
            RefundStatuses.Processing or
            RefundStatuses.Unknown)
        {
            if (!isNewBinding && request.Status == ReturnRequestStatuses.RefundPending)
            {
                return new ReturnServiceResult(ReturnServiceOutcome.Replayed, request);
            }

            if (request.Status != ReturnRequestStatuses.Inspected)
            {
                return Conflict(request, $"A return in {request.Status} status cannot enter RefundPending.");
            }

            request.Status = ReturnRequestStatuses.RefundPending;
            Touch(request);
            return await SaveTransitionAsync(request, cancellationToken);
        }

        if (refund.Status == RefundStatuses.Failed)
        {
            if (request.Status == ReturnRequestStatuses.Inspected)
            {
                return new ReturnServiceResult(ReturnServiceOutcome.Replayed, request);
            }

            if (request.Status != ReturnRequestStatuses.RefundPending)
            {
                return Conflict(request, $"A return in {request.Status} status cannot reconcile a failed refund.");
            }

            request.Status = ReturnRequestStatuses.Inspected;
            request.RefundedAt = null;
            request.ExternalRefundConfirmationReference = null;
            Touch(request);
            return await SaveTransitionAsync(request, cancellationToken);
        }

        if (refund.Status != RefundStatuses.Succeeded)
        {
            return Conflict(request, $"Unsupported refund status {refund.Status}.");
        }

        var completionError = ValidateSucceededRefundCompletion(request, refund);
        if (completionError != null)
        {
            return Conflict(request, completionError);
        }

        var completionFingerprint = RefundCompletionFingerprint(refund);
        var completedAt = refund.CompletedAt.GetValueOrDefault();

        if (request.Status == ReturnRequestStatuses.Refunded)
        {
            return request.RefundId == refund.Id &&
                request.RefundedAt == completedAt &&
                string.Equals(
                    request.ExternalRefundConfirmationReference,
                    completionFingerprint,
                    StringComparison.Ordinal)
                ? new ReturnServiceResult(ReturnServiceOutcome.Replayed, request)
                : Conflict(request, "Stored RMA completion does not match immutable refund completion data.");
        }

        if (request.Status != ReturnRequestStatuses.RefundPending && !isNewBinding)
        {
            return Conflict(request, $"A return in {request.Status} status cannot enter Refunded.");
        }

        request.Status = ReturnRequestStatuses.Refunded;
        request.RefundedAt = completedAt;
        request.ExternalRefundConfirmationReference = completionFingerprint;
        Touch(request, completedAt);
        return await SaveTransitionAsync(request, cancellationToken);
    }

    private Task<ReturnRequest?> LoadForRefundReconciliationAsync(
        long returnRequestId,
        CancellationToken cancellationToken)
    {
        return _context.Set<ReturnRequest>()
            .Include(candidate => candidate.Order)
                .ThenInclude(order => order.Payment)
            .Include(candidate => candidate.Items)
                .ThenInclude(item => item.OrderItem)
            .Include(candidate => candidate.Refund)
                .ThenInclude(refund => refund!.Payment)
            .Include(candidate => candidate.Refund)
                .ThenInclude(refund => refund!.PaymentTransaction)
            .SingleOrDefaultAsync(candidate => candidate.Id == returnRequestId, cancellationToken);
    }

    private static string? ValidateRefundBinding(ReturnRequest request, Refund refund)
    {
        var payment = request.Order.Payment;
        if (payment == null || refund.PaymentId != payment.Id || refund.Payment.OrderId != request.OrderId)
        {
            return "The refund must belong to the return order payment.";
        }

        if (!string.Equals(refund.Currency, payment.Currency, StringComparison.Ordinal))
        {
            return "Refund and payment currencies must match.";
        }

        if (refund.Amount <= 0)
        {
            return "Refund amount must be positive.";
        }

        decimal snapshotUpperBound;
        try
        {
            snapshotUpperBound = request.Items.Aggregate(
                0m,
                (total, item) => checked(total + item.OrderItem.Price * item.Quantity));
        }
        catch (OverflowException)
        {
            return "RMA snapshot amount exceeds the supported monetary range.";
        }

        if (refund.Amount > snapshotUpperBound)
        {
            return "Refund amount exceeds the RMA line snapshot upper bound.";
        }

        if (refund.PaymentTransaction != null)
        {
            var returnedTransactionLine = request.Items.SingleOrDefault(
                item => item.OrderItemId == refund.PaymentTransaction.OrderItemId);
            if (returnedTransactionLine == null ||
                refund.Amount > returnedTransactionLine.OrderItem.Price * returnedTransactionLine.Quantity)
            {
                return "Transaction refund amount exceeds its returned order-line snapshot.";
            }
        }

        return null;
    }

    private static string RefundCompletionFingerprint(Refund refund)
    {
        var value = $"{refund.Provider}\n{refund.ProviderRefundId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static string? ValidateSucceededRefundCompletion(
        ReturnRequest request,
        Refund refund)
    {
        if (refund.Status != RefundStatuses.Succeeded)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(refund.ProviderRefundId) || refund.CompletedAt == null)
        {
            return "A succeeded refund requires provider confirmation id and completion time.";
        }

        return refund.CompletedAt < request.RequestedAt
            ? "Refund completion time cannot be before the return request."
            : null;
    }

    private async Task<ReturnServiceResult> SaveTransitionAsync(
        ReturnRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new ReturnServiceResult(ReturnServiceOutcome.Updated, request);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(request).State = EntityState.Detached;
            return Conflict(null, "The return request was concurrently updated.");
        }
        catch (DbUpdateException)
        {
            var refundLinkConflict =
                request.RefundId != null &&
                await _context.Set<ReturnRequest>()
                    .AsNoTracking()
                    .AnyAsync(
                        candidate =>
                            candidate.Id != request.Id &&
                            candidate.RefundId == request.RefundId,
                        cancellationToken);
            if (refundLinkConflict)
            {
                _context.Entry(request).State = EntityState.Detached;
                return Conflict(null, "The refund is already linked to another return.");
            }

            throw;
        }
    }

    private Task<ReturnRequest?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return _context.Set<ReturnRequest>()
            .AsNoTracking()
            .Include(request => request.Items)
            .SingleOrDefaultAsync(
                request => request.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    private static ReturnServiceResult ResolveRequestReplay(
        ReturnRequest existing,
        int orderId,
        IReadOnlyCollection<ReturnItemRequest> items)
    {
        var existingItems = existing.Items
            .OrderBy(item => item.OrderItemId)
            .Select(item => (item.OrderItemId, item.Quantity, item.ReasonCode));
        var requestedItems = items
            .OrderBy(item => item.OrderItemId)
            .Select(item => (item.OrderItemId, item.Quantity, item.ReasonCode));

        return existing.OrderId == orderId && existingItems.SequenceEqual(requestedItems)
            ? new ReturnServiceResult(ReturnServiceOutcome.Replayed, existing)
            : Conflict(existing, "The idempotency key was already used with a different return payload.");
    }

    private static (IReadOnlyCollection<ReturnItemRequest>? Items, string? Error)
        NormalizeAndValidateItems(
            int orderId,
            string idempotencyKey,
            IReadOnlyCollection<ReturnItemRequest>? items)
    {
        if (orderId <= 0)
        {
            return (null, "Order id must be positive.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return (null, $"Idempotency key must contain 1 to {MaxIdempotencyKeyLength} characters.");
        }

        if (items == null || items.Count == 0 || items.Count > MaxItemsPerRequest)
        {
            return (null, $"A return request must contain 1 to {MaxItemsPerRequest} items.");
        }

        if (items.Any(item => item.OrderItemId <= 0 || item.Quantity <= 0))
        {
            return (null, "Order item ids and quantities must be positive.");
        }

        if (items.Select(item => item.OrderItemId).Distinct().Count() != items.Count)
        {
            return (null, "Each order item can appear only once in a return request.");
        }

        var normalized = new List<ReturnItemRequest>(items.Count);
        foreach (var item in items)
        {
            var reasonCode = item.ReasonCode?.Trim() ?? string.Empty;
            if (!ReturnReasonCodes.Allowed.Contains(reasonCode))
            {
                return (null, "Every item must use an allowed machine-readable reason code.");
            }

            normalized.Add(item with { ReasonCode = reasonCode });
        }

        return (normalized, null);
    }

    private void Touch(ReturnRequest request, DateTime? now = null)
    {
        request.UpdatedAt = now ?? UtcNow();
        request.ConcurrencyToken = Guid.NewGuid();
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

    private bool IsRetryableConcurrencyException(DbException exception)
    {
        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return exception.ErrorCode is 5 or 6;
        }

        return exception is SqlException { Number: 1205 or 1222 };
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static IReadOnlySet<string> SetOf(params string[] statuses) =>
        new HashSet<string>(statuses, StringComparer.Ordinal);

    private static ReturnServiceResult Invalid(string message) =>
        new(ReturnServiceOutcome.InvalidRequest, Message: message);

    private static ReturnServiceResult Conflict(ReturnRequest? request, string message) =>
        new(ReturnServiceOutcome.Conflict, request, message);
}
