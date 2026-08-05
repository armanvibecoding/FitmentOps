using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public sealed record InventoryReservationLine(int ProductId, int Quantity);

public enum InventoryReservationOutcome
{
    Created,
    Updated,
    Replayed,
    NotFound,
    InventoryUnavailable,
    Conflict,
    InvalidRequest
}

public sealed record InventoryReservationResult(
    InventoryReservationOutcome Outcome,
    InventoryReservation? Reservation = null,
    string? Message = null);

public sealed class InventoryReservationService
{
    private const int MaxLines = 100;
    private const int MaxIdempotencyKeyLength = 100;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(2);

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public InventoryReservationService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InventoryReservationResult> ReserveAsync(
        string idempotencyKey,
        IReadOnlyCollection<InventoryReservationLine> lines,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        var now = _timeProvider.GetUtcNow();
        var validation = ValidateReserve(normalizedKey, lines, expiresAt, now);
        if (validation != null)
        {
            return Invalid(validation);
        }

        var normalizedLines = lines.OrderBy(line => line.ProductId).ToArray();
        var payloadHash = ComputePayloadHash(normalizedLines);
        var existing = await FindByKeyAsync(normalizedKey, cancellationToken);
        if (existing != null)
        {
            return ResolveReplay(existing, payloadHash);
        }

        if (_context.Database.CurrentTransaction != null)
        {
            return Invalid("Inventory reservation requires its own transaction boundary.");
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            }

            existing = await FindByKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return ResolveReplay(existing, payloadHash);
            }

            foreach (var line in normalizedLines)
            {
                var reserved = await DecrementAvailableStockAsync(line, now, cancellationToken);
                if (!reserved)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return new InventoryReservationResult(
                        InventoryReservationOutcome.InventoryUnavailable,
                        Message: $"Product {line.ProductId} does not have enough available stock.");
                }
            }

            var reservation = new InventoryReservation
            {
                IdempotencyKey = normalizedKey,
                PayloadHash = payloadHash,
                Status = InventoryReservationStatuses.Active,
                ExpiresAt = expiresAt.UtcDateTime,
                CreatedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime,
                Items = normalizedLines.Select(line => new InventoryReservationItem
                {
                    ProductId = line.ProductId,
                    Quantity = line.Quantity
                }).ToList()
            };
            _context.Set<InventoryReservation>().Add(reservation);
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new InventoryReservationResult(
                InventoryReservationOutcome.Created,
                reservation);
        }
        catch (DbUpdateException)
        {
            if (transaction != null)
            {
                await TryRollbackAndDisposeAsync(transaction, cancellationToken);
                transaction = null;
            }

            _context.ChangeTracker.Clear();
            existing = await FindByKeyAsync(normalizedKey, cancellationToken);
            if (existing == null)
            {
                throw;
            }

            return ResolveReplay(existing, payloadHash);
        }
        catch (DbException exception) when (IsRetryableConcurrencyException(exception))
        {
            if (transaction != null)
            {
                await TryRollbackAndDisposeAsync(transaction, cancellationToken);
                transaction = null;
            }

            _context.ChangeTracker.Clear();
            existing = await FindByKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                return ResolveReplay(existing, payloadHash);
            }

            return Conflict(null, "Inventory changed concurrently; retry the reservation.");
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public Task<InventoryReservationResult> ReleaseAsync(
        long reservationId,
        CancellationToken cancellationToken = default) =>
        FinishAsync(
            reservationId,
            InventoryReservationStatuses.Released,
            null,
            cancellationToken);

    public Task<InventoryReservationResult> CommitAsync(
        long reservationId,
        int orderId,
        CancellationToken cancellationToken = default) =>
        FinishAsync(
            reservationId,
            InventoryReservationStatuses.Committed,
            orderId,
            cancellationToken);

    public async Task<int> ExpireDueAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ids = await _context.Set<InventoryReservation>()
            .AsNoTracking()
            .Where(reservation =>
                reservation.Status == InventoryReservationStatuses.Active &&
                reservation.ExpiresAt <= now)
            .OrderBy(reservation => reservation.ExpiresAt)
            .ThenBy(reservation => reservation.Id)
            .Select(reservation => reservation.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var id in ids)
        {
            var result = await FinishAsync(
                id,
                InventoryReservationStatuses.Expired,
                null,
                cancellationToken);
            if (result.Outcome == InventoryReservationOutcome.Updated)
            {
                expired++;
            }
        }

        return expired;
    }

    private async Task<InventoryReservationResult> FinishAsync(
        long reservationId,
        string targetStatus,
        int? orderId,
        CancellationToken cancellationToken)
    {
        if (reservationId <= 0 ||
            (targetStatus == InventoryReservationStatuses.Committed && orderId is null or <= 0))
        {
            return Invalid("A valid reservation and commit order are required.");
        }

        var ambientTransaction = _context.Database.CurrentTransaction;
        IDbContextTransaction? ownedTransaction = null;
        var transitionTransaction = ambientTransaction;
        try
        {
            if (transitionTransaction == null && _context.Database.IsRelational())
            {
                ownedTransaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                transitionTransaction = ownedTransaction;
            }

            if (transitionTransaction != null)
            {
                var locked = await _context.Set<InventoryReservation>()
                    .Where(reservation => reservation.Id == reservationId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            reservation => reservation.Status,
                            reservation => reservation.Status),
                        cancellationToken);
                if (locked != 1)
                {
                    return new InventoryReservationResult(InventoryReservationOutcome.NotFound);
                }

                _context.ChangeTracker.Clear();
            }

            var reservation = await _context.Set<InventoryReservation>()
                .Include(candidate => candidate.Items)
                .SingleOrDefaultAsync(candidate => candidate.Id == reservationId, cancellationToken);
            if (reservation == null)
            {
                return new InventoryReservationResult(InventoryReservationOutcome.NotFound);
            }

            if (reservation.Status == targetStatus)
            {
                var exactCommit = targetStatus != InventoryReservationStatuses.Committed ||
                    reservation.CommittedOrderId == orderId;
                return exactCommit
                    ? new InventoryReservationResult(InventoryReservationOutcome.Replayed, reservation)
                    : Conflict(reservation, "The reservation was committed to a different order.");
            }

            if (reservation.Status != InventoryReservationStatuses.Active)
            {
                return Conflict(
                    reservation,
                    $"A reservation in {reservation.Status} status cannot enter {targetStatus}.");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (targetStatus == InventoryReservationStatuses.Committed)
            {
                if (reservation.ExpiresAt <= now)
                {
                    return Conflict(reservation, "An expired reservation cannot be committed.");
                }

                var orderExists = await _context.Orders
                    .AsNoTracking()
                    .AnyAsync(order => order.Id == orderId, cancellationToken);
                if (!orderExists)
                {
                    return new InventoryReservationResult(InventoryReservationOutcome.NotFound);
                }

                reservation.CommittedOrderId = orderId;
            }
            else
            {
                foreach (var item in reservation.Items.OrderBy(item => item.ProductId))
                {
                    var restored = await RestoreStockAsync(item, now, cancellationToken);
                    if (!restored)
                    {
                        return Conflict(
                            reservation,
                            "Restoring reserved stock would exceed the supported inventory range.");
                    }
                }
            }

            reservation.Status = targetStatus;
            reservation.UpdatedAt = now;
            reservation.ConcurrencyToken = Guid.NewGuid();
            await _context.SaveChangesAsync(cancellationToken);
            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return new InventoryReservationResult(InventoryReservationOutcome.Updated, reservation);
        }
        catch (DbUpdateConcurrencyException) when (ownedTransaction != null)
        {
            await TryRollbackAndDisposeAsync(ownedTransaction, cancellationToken);
            ownedTransaction = null;

            _context.ChangeTracker.Clear();
            return Conflict(null, "The reservation was updated concurrently.");
        }
        catch (DbUpdateException) when (
            ownedTransaction != null &&
            targetStatus == InventoryReservationStatuses.Committed)
        {
            await TryRollbackAndDisposeAsync(ownedTransaction, cancellationToken);
            ownedTransaction = null;

            _context.ChangeTracker.Clear();
            return await ResolveTransitionPersistenceConflictAsync(
                reservationId,
                targetStatus,
                orderId,
                cancellationToken);
        }
        catch (DbException exception) when (
            ownedTransaction != null &&
            IsRetryableConcurrencyException(exception))
        {
            await TryRollbackAndDisposeAsync(ownedTransaction, cancellationToken);
            ownedTransaction = null;

            _context.ChangeTracker.Clear();
            return Conflict(null, "The reservation was updated concurrently.");
        }
        finally
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    private async Task<bool> DecrementAvailableStockAsync(
        InventoryReservationLine line,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_context.Database.IsRelational())
        {
            return await _context.Products
                .Where(product => product.Id == line.ProductId && product.Stock >= line.Quantity)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(product => product.Stock, product => product.Stock - line.Quantity)
                        .SetProperty(product => product.UpdatedAt, now.UtcDateTime),
                    cancellationToken) == 1;
        }

        var product = await _context.Products.FindAsync([line.ProductId], cancellationToken);
        if (product == null || product.Stock < line.Quantity)
        {
            return false;
        }

        product.Stock -= line.Quantity;
        product.UpdatedAt = now.UtcDateTime;
        return true;
    }

    private async Task<bool> RestoreStockAsync(
        InventoryReservationItem item,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var maximumStockBeforeRestore = int.MaxValue - item.Quantity;
        if (_context.Database.IsRelational())
        {
            var restored = await _context.Products
                .Where(product =>
                    product.Id == item.ProductId &&
                    product.Stock <= maximumStockBeforeRestore)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(product => product.Stock, product => product.Stock + item.Quantity)
                        .SetProperty(product => product.UpdatedAt, now),
                    cancellationToken);
            if (restored != 1)
            {
                var productExists = await _context.Products
                    .AsNoTracking()
                    .AnyAsync(product => product.Id == item.ProductId, cancellationToken);
                if (!productExists)
                {
                    throw new InvalidOperationException("Reserved product no longer exists.");
                }

                return false;
            }

            return true;
        }

        var product = await _context.Products.FindAsync([item.ProductId], cancellationToken) ??
            throw new InvalidOperationException("Reserved product no longer exists.");
        if (product.Stock > maximumStockBeforeRestore)
        {
            return false;
        }

        product.Stock += item.Quantity;
        product.UpdatedAt = now;
        return true;
    }

    private async Task<InventoryReservationResult> ResolveTransitionPersistenceConflictAsync(
        long reservationId,
        string targetStatus,
        int? orderId,
        CancellationToken cancellationToken)
    {
        var current = await _context.Set<InventoryReservation>()
            .AsNoTracking()
            .Include(reservation => reservation.Items)
            .SingleOrDefaultAsync(
                reservation => reservation.Id == reservationId,
                cancellationToken);
        if (current == null)
        {
            return new InventoryReservationResult(InventoryReservationOutcome.NotFound);
        }

        if (current.Status == targetStatus)
        {
            var exactCommit = targetStatus != InventoryReservationStatuses.Committed ||
                current.CommittedOrderId == orderId;
            return exactCommit
                ? new InventoryReservationResult(InventoryReservationOutcome.Replayed, current)
                : Conflict(current, "The reservation was committed to a different order.");
        }

        if (targetStatus == InventoryReservationStatuses.Committed && orderId != null)
        {
            var orderAlreadyCommitted = await _context.Set<InventoryReservation>()
                .AsNoTracking()
                .AnyAsync(
                    reservation =>
                        reservation.Id != reservationId &&
                        reservation.CommittedOrderId == orderId,
                    cancellationToken);
            if (orderAlreadyCommitted)
            {
                return Conflict(current, "The order is already linked to another reservation.");
            }
        }

        return Conflict(current, "The reservation transition conflicted with persisted state.");
    }

    private Task<InventoryReservation?> FindByKeyAsync(
        string key,
        CancellationToken cancellationToken) =>
        _context.Set<InventoryReservation>()
            .AsNoTracking()
            .Include(reservation => reservation.Items)
            .SingleOrDefaultAsync(
                reservation => reservation.IdempotencyKey == key,
                cancellationToken);

    private static string? ValidateReserve(
        string key,
        IReadOnlyCollection<InventoryReservationLine>? lines,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxIdempotencyKeyLength)
        {
            return $"Idempotency key must contain 1 to {MaxIdempotencyKeyLength} characters.";
        }

        if (lines == null || lines.Count == 0 || lines.Count > MaxLines)
        {
            return $"A reservation must contain 1 to {MaxLines} product lines.";
        }

        if (lines.Any(line => line.ProductId <= 0 || line.Quantity <= 0) ||
            lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
        {
            return "Product ids and quantities must be positive and unique.";
        }

        if (expiresAt <= now || expiresAt - now > MaxLifetime)
        {
            return $"Expiration must be in the future and within {MaxLifetime.TotalHours:0} hours.";
        }

        return null;
    }

    private static string ComputePayloadHash(IEnumerable<InventoryReservationLine> lines)
    {
        var canonical = new StringBuilder();
        foreach (var line in lines)
        {
            canonical.Append(line.ProductId).Append(':').Append(line.Quantity).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static InventoryReservationResult ResolveReplay(
        InventoryReservation existing,
        string payloadHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(existing.PayloadHash),
            Encoding.ASCII.GetBytes(payloadHash))
            ? new InventoryReservationResult(InventoryReservationOutcome.Replayed, existing)
            : Conflict(existing, "The idempotency key was used with a different reservation payload.");

    private static async Task TryRollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original persistence exception.
        }
    }

    private static async Task TryRollbackAndDisposeAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await TryRollbackAsync(transaction, cancellationToken);
        await transaction.DisposeAsync();
    }

    private bool IsRetryableConcurrencyException(DbException exception) =>
        _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? exception.ErrorCode is 5 or 6
            : exception is SqlException { Number: 1205 or 1222 };

    private static InventoryReservationResult Invalid(string message) =>
        new(InventoryReservationOutcome.InvalidRequest, Message: message);

    private static InventoryReservationResult Conflict(
        InventoryReservation? reservation,
        string message) =>
        new(InventoryReservationOutcome.Conflict, reservation, message);
}
